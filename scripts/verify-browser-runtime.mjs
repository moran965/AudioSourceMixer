import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const [, , executableArgument, extensionArgument, expectedBrowser, mode = 'active'] = process.argv;
if (!executableArgument || !extensionArgument || !expectedBrowser) {
  throw new Error('Usage: node verify-browser-runtime.mjs <browser.exe> <extension-directory> <chrome|edge>');
}

const executable = resolve(executableArgument);
const extensionDirectory = resolve(extensionArgument);
const extensionId = 'edbfelppckjcfhadggldaifbleoofkio';
const profileDirectory = await mkdtemp(join(tmpdir(), `AudioSourceMixer-${expectedBrowser}-`));
const devToolsPortFile = join(profileDirectory, 'DevToolsActivePort');
const extensionPage = `chrome-extension://${extensionId}/diagnostics/runtime-probe.html`;
let browser;

try {
  browser = spawn(executable, [
    `--user-data-dir=${profileDirectory}`,
    '--remote-debugging-port=0',
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-component-update',
    `--disable-extensions-except=${extensionDirectory}`,
    `--load-extension=${extensionDirectory}`,
    extensionPage
  ], { windowsHide: true, stdio: 'ignore' });

  const port = await waitForPort(devToolsPortFile);
  const targets = await waitForTargets(port);
  const page = targets.find((target) => target.type === 'page' && target.url.startsWith(`chrome-extension://${extensionId}/`));
  if (!page) throw new Error(`The ${expectedBrowser} runtime did not load the fixed-ID extension page.`);

  const client = await connectCdp(page.webSocketDebuggerUrl);
  let workerClient;
  try {
    const pageDetails = await evaluateJson(client, `(async () => JSON.stringify({
      manifestVersion: chrome.runtime.getManifest().version,
      manifestV3: chrome.runtime.getManifest().manifest_version,
      userAgent: navigator.userAgent,
      setSinkIdSupported: 'setSinkId' in AudioContext.prototype,
      outputDevices: (await navigator.mediaDevices.enumerateDevices())
        .filter(device => device.kind === 'audiooutput')
        .map(device => ({ deviceIdPresent: Boolean(device.deviceId), label: device.label }))
    }))()`);
    await client.send('Runtime.evaluate', {
      expression: `void chrome.runtime.sendMessage({ type: 'runtime.probe' }).catch(() => null)`,
      returnByValue: true
    });
    const workerTarget = await waitForServiceWorker(port);
    workerClient = await connectCdp(workerTarget.webSocketDebuggerUrl);
    const nativeMessage = mode === 'idle' ? await verifyIdleWorker(workerClient) : await evaluateJson(workerClient, `(() => new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error('Native Messaging protocol 2 handshake timed out.')), 10000);
        const port = chrome.runtime.connectNative('com.audiosourcemixer.bridge');
        port.onMessage.addListener(message => {
          clearTimeout(timeout);
          resolve(JSON.stringify(message));
          port.disconnect();
        });
        port.onDisconnect.addListener(() => {
          if (chrome.runtime.lastError) {
            clearTimeout(timeout);
            reject(new Error(chrome.runtime.lastError.message));
          }
        });
        port.postMessage({ protocolVersion: 2, type: 'bridge.hello' });
      }))()`);
    const result = { ...pageDetails, nativeMessage };
    if (result.manifestVersion !== '0.2.0' || result.manifestV3 !== 3) throw new Error(`Unexpected extension manifest: ${JSON.stringify(result)}`);
    if (mode !== 'idle' && (result.nativeMessage?.type !== 'bridge.status' || result.nativeMessage?.protocolVersion !== 2 || result.nativeMessage?.error)) {
      throw new Error(`Native Messaging handshake failed: ${JSON.stringify(result.nativeMessage)}`);
    }
    const detectedBrowser = result.userAgent.includes('Edg/') ? 'edge' : 'chrome';
    if (detectedBrowser !== expectedBrowser) throw new Error(`Expected ${expectedBrowser}, detected ${detectedBrowser}.`);
    console.log(JSON.stringify({ browser: expectedBrowser, ...result }, null, 2));
  } finally {
    workerClient?.close();
    try { await client.send('Browser.close'); } catch { /* Process cleanup below is the fallback. */ }
    client.close();
  }

  await waitForExit(browser, 10000);
} finally {
  if (browser && browser.exitCode === null) browser.kill();
  await rm(profileDirectory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}

async function waitForPort(path) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    try {
      const [port] = (await readFile(path, 'utf8')).trim().split(/\r?\n/);
      if (/^\d+$/.test(port)) return Number(port);
    } catch { /* Browser is still starting. */ }
    await delay(100);
  }
  throw new Error('Timed out waiting for DevToolsActivePort.');
}

async function waitForTargets(port) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`);
      const targets = await response.json();
      if (targets.some((target) => target.url.startsWith(`chrome-extension://${extensionId}/`))) return targets;
    } catch { /* Browser is still registering the unpacked extension. */ }
    await delay(100);
  }
  throw new Error('Timed out waiting for the unpacked extension target.');
}

async function waitForServiceWorker(port) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    const response = await fetch(`http://127.0.0.1:${port}/json/list`);
    const targets = await response.json();
    const worker = targets.find((target) => target.type === 'service_worker' &&
      target.url.startsWith(`chrome-extension://${extensionId}/service-worker/`));
    if (worker) return worker;
    await delay(100);
  }
  throw new Error('Timed out waiting for the extension service worker target.');
}

async function evaluateJson(client, expression) {
  const evaluation = await client.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (evaluation.result?.exceptionDetails) {
    throw new Error(evaluation.result.exceptionDetails.exception?.description || evaluation.result.exceptionDetails.text);
  }
  const serialized = evaluation.result?.result?.value;
  if (typeof serialized !== 'string') throw new Error(`The browser returned an unexpected CDP result: ${JSON.stringify(evaluation)}`);
  return JSON.parse(serialized);
}

async function verifyIdleWorker(client) {
  await delay(3000);
  const state = await evaluateJson(client, `(async () => JSON.stringify({
    nativeStatus: (await chrome.storage.session.get('nativeStatus')).nativeStatus || '',
    tabStates: (await chrome.storage.session.get('tabStates')).tabStates || {}
  }))()`);
  if (Object.keys(state.tabStates).length !== 0) throw new Error(`Idle profile unexpectedly has tab state: ${JSON.stringify(state)}`);
  return { type: 'not-requested', ...state };
}

async function connectCdp(url) {
  const socket = new WebSocket(url);
  await new Promise((resolveOpen, rejectOpen) => {
    socket.addEventListener('open', resolveOpen, { once: true });
    socket.addEventListener('error', rejectOpen, { once: true });
  });
  let nextId = 0;
  const pending = new Map();
  socket.addEventListener('message', (event) => {
    const message = JSON.parse(event.data);
    if (!message.id || !pending.has(message.id)) return;
    const { resolveMessage, rejectMessage } = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) rejectMessage(new Error(message.error.message));
    else resolveMessage(message);
  });
  return {
    send(method, params = {}) {
      const id = ++nextId;
      return new Promise((resolveMessage, rejectMessage) => {
        pending.set(id, { resolveMessage, rejectMessage });
        socket.send(JSON.stringify({ id, method, params }));
      });
    },
    close() { socket.close(); }
  };
}

async function waitForExit(process, timeout) {
  if (process.exitCode !== null) return;
  await Promise.race([
    new Promise((resolveExit) => process.once('exit', resolveExit)),
    delay(timeout).then(() => { throw new Error('Browser did not exit after Browser.close.'); })
  ]);
}

function delay(milliseconds) {
  return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}
