import { normalizeDeviceLabel } from '../shared/protocol.js';

const GENERIC_TOKENS = new Set([
  'default', 'communications', 'speaker', 'speakers', 'headphone', 'headphones',
  '扬声器', '耳机', '蓝牙', 'bluetooth', 'audio', 'device', 'output', 'stereo', '立体声'
]);

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
  const createContext = options.createContext || (() => new AudioContext());
  const wait = options.wait || ((milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)));
  const durationMs = Math.min(1000, Math.max(500, options.durationMs || 700));
  const context = createContext();
  let oscillator;
  let gain;
  let started = false;
  try {
    if (typeof context.setSinkId !== 'function') throw uiError('testUnsupported', 'The browser does not support output-device test playback.');
    await context.setSinkId(deviceId);
    oscillator = context.createOscillator();
    gain = context.createGain();
    oscillator.frequency.value = 660;
    if (typeof gain.gain.setValueAtTime === 'function') gain.gain.setValueAtTime(0.025, context.currentTime);
    else gain.gain.value = 0.025;
    oscillator.connect(gain);
    gain.connect(context.destination);
    if (typeof context.resume === 'function') await context.resume();
    oscillator.start();
    started = true;
    await wait(durationMs);
  } finally {
    if (started) try { oscillator.stop(); } catch {}
    try { oscillator?.disconnect(); } catch {}
    try { gain?.disconnect(); } catch {}
    if (context.state !== 'closed') await context.close();
  }
}

function uiError(code, message) {
  const error = new Error(message);
  error.uiMessageKey = code;
  return error;
}

function meaningfulTokens(value) {
  return normalizeDeviceLabel(value)
    .replace(/[()\[\]{},.:;，。；：_\\-]+/gu, ' ')
    .split(/\s+/u)
    .map((token) => token.trim())
    .filter((token) => token.length >= 2 && !GENERIC_TOKENS.has(token));
}
