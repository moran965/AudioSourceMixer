import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const [, , executableArgument, extensionArgument, expectedBrowser] = process.argv;
if (!executableArgument || !extensionArgument || !['chrome', 'edge'].includes(expectedBrowser)) {
  throw new Error('Usage: node verify-browser-authorization-runtime.mjs <browser.exe> <extension-directory> <chrome|edge>');
}

const executable = resolve(executableArgument);
const extensionDirectory = resolve(extensionArgument);
const completion = main();

async function main() {
  const extensionId = await extensionIdFromManifest();
  const profileDirectory = await mkdtemp(join(tmpdir(), `AudioSourceMixer-auth-${expectedBrowser}-`));
  const devToolsPortFile = join(profileDirectory, 'DevToolsActivePort');
  const pageUrl = `chrome-extension://${extensionId}/output-authorization/authorize.html`;
  const errors = [];
  let browser;

  try {
    browser = spawn(executable, [
      `--user-data-dir=${profileDirectory}`,
      '--remote-debugging-port=0',
      '--enable-unsafe-extension-debugging',
      '--no-first-run',
      '--no-default-browser-check',
      '--disable-component-update',
      'about:blank'
    ], { windowsHide: true, stdio: 'ignore' });

    const port = await waitForPort(devToolsPortFile);
    const version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
    const browserClient = await CdpClient.connect(version.webSocketDebuggerUrl, 'browser', errors);
    const loaded = await browserClient.send('Extensions.loadUnpacked', { path: extensionDirectory });
    if (loaded.result?.id !== extensionId)
      throw new Error(`CDP loaded unexpected extension ID ${loaded.result?.id || 'none'}; expected ${extensionId}.`);
    await browserClient.send('Target.createTarget', { url: pageUrl });
    const pageTarget = await waitForTarget(port, (target) => target.type === 'page' && target.url === pageUrl,
      'authorization page');
    const page = await CdpClient.connect(pageTarget.webSocketDebuggerUrl, 'authorization-page', errors);
    let worker;
    try {
    await enableDiagnostics(page);
    await page.evaluate(`globalThis.__asmUnhandledRejections = [];
      addEventListener('unhandledrejection', event => {
        globalThis.__asmUnhandledRejections.push(String(event.reason?.stack || event.reason || 'unknown rejection'));
      });`);
    await page.evaluate(`chrome.runtime.sendMessage({ type: 'runtime.probe' }).catch(() => null)`);

    const workerTarget = await waitForTarget(port,
      (target) => target.type === 'service_worker' && target.url.startsWith(`chrome-extension://${extensionId}/service-worker/`),
      'extension service worker');
    worker = await CdpClient.connect(workerTarget.webSocketDebuggerUrl, 'service-worker', errors);
    await enableDiagnostics(worker);

    const result = await page.evaluateJson(`(async () => {
      const controllerModule = await import(chrome.runtime.getURL('output-authorization/authorization-controller.js'));
      const mappings = await import(chrome.runtime.getURL('output-authorization/mappings.js'));
      const browser = navigator.userAgent.includes('Edg/') ? 'edge' : 'chrome';
      const endpointId = 'cdp-runtime-endpoint';
      let token = 0;

      async function loadMappingStore() {
        const stored = await chrome.storage.local.get([mappings.OUTPUT_MAPPINGS_KEY, mappings.LEGACY_OUTPUT_MAPPINGS_KEY]);
        return mappings.migrateOutputMappingStore(stored[mappings.OUTPUT_MAPPINGS_KEY], stored[mappings.LEGACY_OUTPUT_MAPPINGS_KEY]);
      }
      const controller = controllerModule.createAuthorizationController({
        browser,
        loadMappingStore,
        localStorage: chrome.storage.local,
        sessionStorage: chrome.storage.session,
        sendMessage: message => chrome.runtime.sendMessage(message)
      });
      async function seedRequest(generation) {
        const request = {
          browser, tabId: 900 + generation, correlationId: 'cdp-' + generation, generation,
          windowsEndpointId: endpointId, windowsEndpointName: 'CDP Runtime USB DAC',
          outputDevices: [{ endpointId, friendlyName: 'CDP Runtime USB DAC' }]
        };
        const stored = await chrome.storage.session.get(mappings.PENDING_OUTPUT_AUTHORIZATION_KEY);
        const queue = mappings.queueAuthorizationRequest(stored[mappings.PENDING_OUTPUT_AUTHORIZATION_KEY], request);
        await chrome.storage.session.set({ [mappings.PENDING_OUTPUT_AUTHORIZATION_KEY]: queue });
        return mappings.pendingAuthorizationRequests(queue, browser).find(item => item.windowsEndpointId === endpointId);
      }
      async function confirm(deviceId, generation) {
        const request = await seedRequest(generation);
        const candidate = {
          browser, windowsEndpointId: endpointId, windowsEndpointName: 'CDP Runtime USB DAC',
          deviceId, browserLabel: 'CDP Runtime USB DAC ' + generation, browserGroupId: 'cdp-group',
          compatibility: { level: 'match', message: 'runtime test' },
          candidateGeneration: generation, deviceListGeneration: generation,
          testVerification: { status: 'verified', browser, windowsEndpointId: endpointId, deviceId,
            effectiveSinkId: deviceId, candidateGeneration: generation, deviceListGeneration: generation,
            verifiedAt: new Date().toISOString() }
        };
        return controller.confirm(candidate, request, ++token);
      }

      await chrome.storage.local.remove([mappings.OUTPUT_MAPPINGS_KEY, mappings.LEGACY_OUTPUT_MAPPINGS_KEY]);
      await chrome.storage.session.remove(mappings.PENDING_OUTPUT_AUTHORIZATION_KEY);
      const first = await confirm('cdp-device-one', 1);
      const afterFirst = mappings.findOutputMapping(await loadMappingStore(), browser, endpointId)?.deviceId;
      const modified = await confirm('cdp-device-two', 2);
      const afterModify = mappings.findOutputMapping(await loadMappingStore(), browser, endpointId)?.deviceId;

      let store = mappings.removeOutputMapping(await loadMappingStore(), browser, endpointId);
      await chrome.storage.local.set({ [mappings.OUTPUT_MAPPINGS_KEY]: store });
      const afterDelete = mappings.findOutputMapping(await loadMappingStore(), browser, endpointId);

      const repeated = await confirm('cdp-device-three', 3);
      const afterRepeat = mappings.findOutputMapping(await loadMappingStore(), browser, endpointId)?.deviceId;
      store = mappings.clearBrowserOutputMappings(await loadMappingStore(), browser);
      await chrome.storage.local.set({ [mappings.OUTPUT_MAPPINGS_KEY]: store });
      const queue = (await chrome.storage.session.get(mappings.PENDING_OUTPUT_AUTHORIZATION_KEY))[mappings.PENDING_OUTPUT_AUTHORIZATION_KEY] || {};
      const unhandled = globalThis.__asmUnhandledRejections || [];
      return JSON.stringify({ browser, first, modified, repeated, afterFirst, afterModify, afterDelete,
        afterRepeat, remainingMappings: Object.keys(mappings.outputMappings(store)).length,
        remainingRequests: mappings.pendingAuthorizationRequests(queue, browser).length, unhandled });
    })()`);

    await delay(500);
    const workerState = await worker.evaluateJson(`(async () => JSON.stringify({
      lastExtensionError: (await chrome.storage.session.get('lastExtensionError')).lastExtensionError || ''
    }))()`);
    if (result.browser !== expectedBrowser) throw new Error(`Expected ${expectedBrowser}, detected ${result.browser}.`);
    for (const operation of [result.first, result.modified, result.repeated]) {
      if (operation.status !== 'completed' || !operation.mappingSaved || !operation.notified)
        throw new Error(`Authorization transaction did not complete: ${JSON.stringify(operation)}`);
    }
    if (result.afterFirst !== 'cdp-device-one' || result.afterModify !== 'cdp-device-two' ||
        result.afterDelete !== null || result.afterRepeat !== 'cdp-device-three' ||
        result.remainingMappings !== 0 || result.remainingRequests !== 0) {
      throw new Error(`Authorization lifecycle verification failed: ${JSON.stringify(result)}`);
    }
    if (result.unhandled.length > 0) errors.push(...result.unhandled.map((message) => `page unhandledrejection: ${message}`));
    if (workerState.lastExtensionError) errors.push(`service worker lastExtensionError: ${workerState.lastExtensionError}`);
    if (errors.length > 0) throw new Error(`${expectedBrowser} extension diagnostics reported errors:\n${errors.join('\n')}`);
    console.log(JSON.stringify({ browser: expectedBrowser, authorizationOperations: 4,
      runtimeExceptions: 0, logErrors: 0, unhandledRejections: 0, serviceWorkerErrors: 0 }, null, 2));
    } finally {
      worker?.close();
      page.close();
      try { await browserClient.send('Browser.close'); } catch { /* process cleanup is the fallback */ }
      browserClient.close();
    }
    await waitForExit(browser, 10000);
  } finally {
    if (browser && browser.exitCode === null) browser.kill();
    await rm(profileDirectory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
}

async function extensionIdFromManifest() {
  const manifest = JSON.parse(await readFile(join(extensionDirectory, 'manifest.json'), 'utf8'));
  const hash = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest().subarray(0, 16);
  return [...hash].map((byte) => String.fromCharCode(97 + (byte >> 4), 97 + (byte & 15))).join('');
}

async function enableDiagnostics(client) {
  await client.send('Runtime.enable');
  await client.send('Log.enable');
}

async function waitForPort(path) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    try {
      const [port] = (await readFile(path, 'utf8')).trim().split(/\r?\n/);
      if (/^\d+$/.test(port)) return Number(port);
    } catch { /* browser is still starting */ }
    await delay(100);
  }
  throw new Error('Timed out waiting for DevToolsActivePort.');
}

async function waitForTarget(port, predicate, description) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const target = targets.find(predicate);
      if (target) return target;
    } catch { /* target is still being registered */ }
    await delay(100);
  }
  throw new Error(`Timed out waiting for ${description}.`);
}

class CdpClient {
  #socket;
  #nextId = 0;
  #pending = new Map();
  #label;
  #errors;

  static async connect(url, label, errors) {
    const client = new CdpClient(label, errors);
    client.#socket = new WebSocket(url);
    client.#socket.addEventListener('message', (event) => client.#receive(event));
    await new Promise((resolveOpen, rejectOpen) => {
      client.#socket.addEventListener('open', resolveOpen, { once: true });
      client.#socket.addEventListener('error', rejectOpen, { once: true });
    });
    return client;
  }

  constructor(label, errors) { this.#label = label; this.#errors = errors; }

  #receive(event) {
    const message = JSON.parse(event.data);
    if (message.id && this.#pending.has(message.id)) {
      const pending = this.#pending.get(message.id);
      this.#pending.delete(message.id);
      if (message.error) pending.reject(new Error(message.error.message));
      else pending.resolve(message);
      return;
    }
    if (message.method === 'Runtime.exceptionThrown')
      this.#errors.push(`${this.#label} Runtime.exceptionThrown: ${message.params.exceptionDetails?.exception?.description || message.params.exceptionDetails?.text}`);
    if (message.method === 'Log.entryAdded' && message.params.entry?.level === 'error')
      this.#errors.push(`${this.#label} Log.entryAdded: ${message.params.entry.text}`);
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error')
      this.#errors.push(`${this.#label} console.error: ${message.params.args?.map((arg) => arg.value || arg.description).join(' ')}`);
  }

  send(method, params = {}) {
    const id = ++this.#nextId;
    return new Promise((resolveMessage, rejectMessage) => {
      this.#pending.set(id, { resolve: resolveMessage, reject: rejectMessage });
      this.#socket.send(JSON.stringify({ id, method, params }));
    });
  }

  async evaluate(expression) {
    const evaluation = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (evaluation.result?.exceptionDetails)
      throw new Error(evaluation.result.exceptionDetails.exception?.description || evaluation.result.exceptionDetails.text);
    return evaluation.result?.result?.value;
  }

  async evaluateJson(expression) {
    const serialized = await this.evaluate(expression);
    if (typeof serialized !== 'string') throw new Error(`Unexpected CDP result: ${JSON.stringify(serialized)}`);
    return JSON.parse(serialized);
  }

  close() { this.#socket.close(); }
}

async function waitForExit(process, timeout) {
  if (process.exitCode !== null) return;
  await Promise.race([
    new Promise((resolveExit) => process.once('exit', resolveExit)),
    delay(timeout).then(() => { throw new Error('Browser did not exit after Browser.close.'); })
  ]);
}

function delay(milliseconds) { return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds)); }

await completion;
