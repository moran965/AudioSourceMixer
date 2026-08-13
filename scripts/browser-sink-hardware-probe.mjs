import process from 'node:process';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';

const [portText, firstLabel, secondLabel, pageMode = 'extension'] = process.argv.slice(2);
const port = Number(portText);
if (!Number.isInteger(port) || !firstLabel || !secondLabel || !['extension', 'page'].includes(pageMode)) {
  console.error('Usage: node browser-sink-hardware-probe.mjs <debugPort> <firstDeviceLabelPart> <secondDeviceLabelPart> [extension|page]');
  process.exit(2);
}

const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

async function getTargets(deadline = Date.now() + 20_000) {
  let lastError;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`);
      if (response.ok) return await response.json();
    } catch (error) { lastError = error; }
    await delay(200);
  }
  throw new Error(`Chromium DevTools endpoint did not become ready on port ${port}.`, { cause: lastError });
}

class CdpClient {
  #socket;
  #nextId = 1;
  #pending = new Map();

  static async connect(url) {
    const client = new CdpClient();
    client.#socket = new WebSocket(url);
    client.#socket.addEventListener('message', (event) => client.#receive(event));
    await new Promise((resolve, reject) => {
      client.#socket.addEventListener('open', resolve, { once: true });
      client.#socket.addEventListener('error', reject, { once: true });
    });
    return client;
  }

  send(method, params = {}, timeoutMilliseconds = 20_000) {
    const id = this.#nextId++;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.#pending.delete(id);
        reject(new Error(`${method} timed out after ${timeoutMilliseconds} ms.`));
      }, timeoutMilliseconds);
      this.#pending.set(id, { resolve, reject, timeout });
      this.#socket.send(JSON.stringify({ id, method, params }));
    });
  }

  #receive(event) {
    const message = JSON.parse(event.data);
    if (!message.id) return;
    const pending = this.#pending.get(message.id);
    if (!pending) return;
    clearTimeout(pending.timeout);
    this.#pending.delete(message.id);
    if (message.error) pending.reject(new Error(`${message.error.code}: ${message.error.message}`));
    else pending.resolve(message.result);
  }

  close() { this.#socket.close(); }
}

function extensionIdFromTargets(targets) {
  for (const target of targets) {
    if (!target.url.endsWith('/service-worker/service-worker.js')) continue;
    const match = /^chrome-extension:\/\/([a-p]{32})\//.exec(target.url);
    if (match) return match[1];
  }
  return null;
}

async function extensionIdFromManifestKey() {
  const manifest = JSON.parse(await readFile('src/AudioSourceMixer.BrowserExtension/manifest.json', 'utf8'));
  const hash = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest().subarray(0, 16);
  return [...hash].map((byte) => String.fromCharCode(97 + (byte >> 4), 97 + (byte & 15))).join('');
}

let targets = await getTargets();
const extensionId = extensionIdFromTargets(targets) || await extensionIdFromManifestKey();

let probeServer;
let probeUrl;
if (pageMode === 'extension') {
  probeUrl = `chrome-extension://${extensionId}/output-authorization/authorize.html`;
} else {
  probeServer = createServer((_, response) => {
    response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
    response.end('<!doctype html><html><title>Audio Source Mixer browser sink probe</title><body>sink probe</body></html>');
  });
  await new Promise((resolve, reject) => {
    probeServer.once('error', reject);
    probeServer.listen(0, '127.0.0.1', resolve);
  });
  const address = probeServer.address();
  probeUrl = `http://127.0.0.1:${address.port}/`;
}
const createResponse = await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent(probeUrl)}`, { method: 'PUT' });
if (!createResponse.ok) throw new Error(`Could not open extension authorization page: HTTP ${createResponse.status}.`);
const created = await createResponse.json();
const client = await CdpClient.connect(created.webSocketDebuggerUrl);

try {
  await client.send('Runtime.enable');
  const readyDeadline = Date.now() + 15_000;
  let pageState;
  while (Date.now() < readyDeadline) {
    const ready = await client.send('Runtime.evaluate', {
      expression: `({ href: location.href, readyState: document.readyState,
        hasMediaDevices: Boolean(navigator.mediaDevices) })`,
      returnByValue: true
    });
    pageState = ready.result.value;
    if (pageState?.href === probeUrl && pageState.readyState === 'complete' && pageState.hasMediaDevices) break;
    await delay(200);
  }
  if (pageState?.href !== probeUrl || pageState.readyState !== 'complete' || !pageState.hasMediaDevices) {
    throw new Error(`Extension authorization page did not become ready: ${JSON.stringify(pageState)}`);
  }
  const expression = `
    (async () => {
      const startedAt = performance.now();
      let stream;
      try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
      } finally {
        stream?.getTracks().forEach((track) => track.stop());
      }
      const devices = (await navigator.mediaDevices.enumerateDevices())
        .filter((device) => device.kind === 'audiooutput' && device.deviceId)
        .map((device) => ({ deviceId: device.deviceId, label: device.label, groupId: device.groupId }));
      const find = (part) => {
        const matches = devices.filter((device) => device.label.toLocaleLowerCase().includes(part.toLocaleLowerCase()));
        return matches.find((device) => !['default', 'communications'].includes(device.deviceId)) || matches[0];
      };
      const requested = [${JSON.stringify(firstLabel)}, ${JSON.stringify(secondLabel)}].map(find);
      if (requested.some((device) => !device)) {
        throw new Error('Requested physical outputs were not both visible: ' + JSON.stringify({
          requestedLabels: [${JSON.stringify(firstLabel)}, ${JSON.stringify(secondLabel)}],
          visibleLabels: devices.map((device) => device.label)
        }));
      }
      const context = new AudioContext();
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      gain.gain.value = 0.015;
      oscillator.frequency.value = 440;
      oscillator.connect(gain).connect(context.destination);
      oscillator.start();
      const checks = [];
      try {
        for (const device of requested) {
          const before = performance.now();
          await context.setSinkId(device.deviceId);
          await context.resume();
          await new Promise((resolve) => setTimeout(resolve, 750));
          checks.push({
            label: device.label,
            requestedSinkId: device.deviceId,
            effectiveSinkId: context.sinkId,
            matched: context.sinkId === device.deviceId,
            setSinkMilliseconds: Math.round((performance.now() - before) * 1000) / 1000,
            state: context.state
          });
        }
      } finally {
        oscillator.stop();
        oscillator.disconnect();
        gain.disconnect();
        await context.close();
      }
      return {
        userAgent: navigator.userAgent,
        extensionId: globalThis.chrome?.runtime?.id || null,
        probeContext: ${JSON.stringify(pageMode)},
        setSinkIdSupported: typeof AudioContext.prototype.setSinkId === 'function',
        selectAudioOutputSupported: typeof navigator.mediaDevices.selectAudioOutput === 'function',
        visibleOutputLabels: devices.map((device) => device.label),
        checks,
        microphoneTracksStopped: !stream || stream.getTracks().every((track) => track.readyState === 'ended'),
        totalMilliseconds: Math.round((performance.now() - startedAt) * 1000) / 1000
      };
    })()`;
  const evaluated = await client.send('Runtime.evaluate', {
    expression,
    awaitPromise: true,
    returnByValue: true,
    userGesture: true
  }, 45_000);
  if (evaluated.exceptionDetails) throw new Error(evaluated.exceptionDetails.exception?.description || evaluated.exceptionDetails.text);
  const result = evaluated.result.value;
  if (!result?.setSinkIdSupported || !result?.microphoneTracksStopped || result.checks?.length !== 2 ||
      result.checks.some((check) => !check.matched || check.state !== 'running')) {
    throw new Error(`Browser sink verification failed: ${JSON.stringify(result)}`);
  }
  console.log(JSON.stringify(result, null, 2));
} finally {
  client.close();
  if (probeServer) await new Promise((resolve) => probeServer.close(resolve));
}
