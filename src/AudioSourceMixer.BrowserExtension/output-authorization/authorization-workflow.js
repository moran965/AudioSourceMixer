import { normalizeDeviceLabel } from '../shared/protocol.js';

const GENERIC_TOKENS = new Set([
  'default', 'communications', 'speaker', 'speakers', 'headphone', 'headphones',
  '扬声器', '耳机', '蓝牙', 'bluetooth', 'audio', 'device', 'output', 'stereo', '立体声'
]);
const VIRTUAL_OUTPUT_DEVICE_IDS = new Set(['', 'default', 'communications']);

export function createOutputMappingCandidate(request, selected, browser, now = new Date().toISOString()) {
  if (!request?.windowsEndpointId) throw uiError('noPendingError', 'No pending Windows output device.');
  if (!selected?.deviceId) throw uiError('chooseBrowserDevice', 'No browser output device was selected.');
  return {
    browser,
    windowsEndpointId: request.windowsEndpointId,
    windowsEndpointName: request.windowsEndpointName || request.windowsEndpointId,
    browserLabel: selected.label || '',
    browserGroupId: selected.groupId || '',
    deviceId: selected.deviceId,
    authorizedAt: now,
    compatibility: compareDeviceNames(request.windowsEndpointName, selected.label)
  };
}

export function compareDeviceNames(windowsName, browserLabel) {
  const windowsTokens = meaningfulTokens(windowsName);
  const browserTokens = meaningfulTokens(browserLabel);
  if (windowsTokens.length === 0 || browserTokens.length === 0)
    return { level: 'unknown', messageCode: 'compatUnknown' };
  const overlap = windowsTokens.some((token) => browserTokens.some((candidate) =>
    candidate === token || candidate.includes(token) || token.includes(candidate)));
  if (overlap) return { level: 'likely', messageCode: 'compatLikely' };
  return { level: 'warning', messageCode: 'compatWarning' };
}

export async function playOutputTestTone(deviceId, options = {}) {
  if (!isConcreteOutputDeviceId(deviceId))
    throw uiError('sinkUnavailable', 'A concrete physical browser output device is required.');
  const createContext = options.createContext || ((contextOptions) => new AudioContext(contextOptions));
  const mediaDevices = options.mediaDevices || globalThis.navigator?.mediaDevices;
  const wait = options.wait || ((milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)));
  const durationMs = Math.min(1000, Math.max(500, options.durationMs || 700));
  await requireAvailableOutput(mediaDevices, deviceId);

  let context;
  let oscillator;
  let gain;
  let started = false;
  let stopped = false;
  let deviceChangeHandler;
  let signalDeviceRemoval;
  const deviceRemoval = new Promise((resolve) => { signalDeviceRemoval = resolve; });
  try {
    try {
      context = createContext({ sinkId: deviceId, latencyHint: 'interactive' });
    } catch {
      // Older Chromium builds may reject the constructor option. This fallback context remains
      // silent until setSinkId and both effective-sink checks have completed.
      context = createContext();
    }
    if (!context) throw uiError('testUnsupported', 'The browser could not create a test audio context.');

    deviceChangeHandler = () => {
      Promise.resolve().then(async () => {
        if (await outputIsAvailable(mediaDevices, deviceId)) return;
        stopOscillator();
        signalDeviceRemoval(uiError('sinkUnavailable', 'The selected output device disappeared during the test.'));
      }).catch(() => {
        stopOscillator();
        signalDeviceRemoval(uiError('sinkUnavailable', 'The output device list could not be revalidated.'));
      });
    };
    mediaDevices?.addEventListener?.('devicechange', deviceChangeHandler);

    if (readEffectiveSink(context) !== deviceId) {
      if (typeof context.setSinkId !== 'function')
        throw uiError('testUnsupported', 'The browser does not support output-device test playback.');
      try { await context.setSinkId(deviceId); }
      catch (error) { throw uiError('sinkUnavailable', 'The browser rejected the selected output device.', error); }
    }
    await verifyTarget(context, mediaDevices, deviceId);
    if (typeof context.resume === 'function') await context.resume();
    await verifyTarget(context, mediaDevices, deviceId);

    // No audible node is created or connected until the effective sink has been verified twice.
    oscillator = context.createOscillator();
    gain = context.createGain();
    oscillator.frequency.value = 660;
    if (typeof gain.gain.setValueAtTime === 'function') gain.gain.setValueAtTime(0.025, context.currentTime);
    else gain.gain.value = 0.025;
    oscillator.connect(gain);
    gain.connect(context.destination);
    oscillator.start();
    started = true;
    const removed = await Promise.race([
      wait(durationMs).then(() => null),
      deviceRemoval
    ]);
    if (removed) throw removed;
    await verifyTarget(context, mediaDevices, deviceId);
    return Object.freeze({ deviceId, effectiveSinkId: readEffectiveSink(context), verified: true });
  } finally {
    if (deviceChangeHandler) mediaDevices?.removeEventListener?.('devicechange', deviceChangeHandler);
    stopOscillator();
    try { oscillator?.disconnect(); } catch {}
    try { gain?.disconnect(); } catch {}
    if (context && context.state !== 'closed') await context.close();
  }

  function stopOscillator() {
    if (!started || stopped) return;
    stopped = true;
    try { oscillator.stop(); } catch {}
  }
}

export function createCandidateTestVerification(candidate, candidateGeneration, deviceListGeneration,
    status = 'untested', effectiveSinkId = '', verifiedAt = null) {
  return Object.freeze({
    status,
    browser: candidate?.browser || '',
    windowsEndpointId: candidate?.windowsEndpointId || '',
    deviceId: candidate?.deviceId || '',
    candidateGeneration,
    deviceListGeneration,
    effectiveSinkId,
    verifiedAt
  });
}

export function candidateTestIsCurrent(candidate) {
  const proof = candidate?.testVerification;
  return Boolean(proof?.status === 'verified' && proof.browser === candidate.browser &&
    proof.windowsEndpointId === candidate.windowsEndpointId && proof.deviceId === candidate.deviceId &&
    proof.effectiveSinkId === candidate.deviceId && proof.candidateGeneration === candidate.candidateGeneration &&
    proof.deviceListGeneration === candidate.deviceListGeneration && proof.verifiedAt);
}

export function isConcreteOutputDeviceId(deviceId) {
  return typeof deviceId === 'string' && !VIRTUAL_OUTPUT_DEVICE_IDS.has(deviceId.trim().toLowerCase());
}

async function verifyTarget(context, mediaDevices, deviceId) {
  await requireAvailableOutput(mediaDevices, deviceId);
  if (readEffectiveSink(context) !== deviceId)
    throw uiError('sinkMismatch', 'The browser did not switch the test context to the selected output device.');
}

function readEffectiveSink(context) {
  try { return typeof context?.sinkId === 'string' ? context.sinkId : ''; }
  catch { return ''; }
}

async function requireAvailableOutput(mediaDevices, deviceId) {
  if (!await outputIsAvailable(mediaDevices, deviceId))
    throw uiError('sinkUnavailable', 'The selected physical output device is no longer available.');
}

async function outputIsAvailable(mediaDevices, deviceId) {
  if (typeof mediaDevices?.enumerateDevices !== 'function') return false;
  const devices = await mediaDevices.enumerateDevices();
  return devices.some((device) => device?.kind === 'audiooutput' && device.deviceId === deviceId &&
    isConcreteOutputDeviceId(device.deviceId));
}

function uiError(code, message, cause = undefined) {
  const error = new Error(message);
  error.uiMessageKey = code;
  if (cause !== undefined) error.cause = cause;
  return error;
}

function meaningfulTokens(value) {
  return normalizeDeviceLabel(value)
    .replace(/[()\[\]{},.:;，。；：_\\-]+/gu, ' ')
    .split(/\s+/u)
    .map((token) => token.trim())
    .filter((token) => token.length >= 2 && !GENERIC_TOKENS.has(token));
}
