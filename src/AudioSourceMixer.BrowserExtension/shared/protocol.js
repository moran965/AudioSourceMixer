import { normalizeEqualizer } from './equalizer.js';

export const PROTOCOL_VERSION = 3;
export const NATIVE_HOST_NAME = 'com.audiosourcemixer.bridge';

export function browserName(userAgent = navigator.userAgent) {
  return userAgent.includes('Edg/') ? 'edge' : 'chrome';
}

export function sourceId(browser, tabId) {
  if (!['chrome', 'edge'].includes(browser)) throw new Error('Unsupported browser.');
  if (!Number.isInteger(tabId) || tabId < 0) throw new Error('Invalid tabId.');
  return `${browser}:${tabId}`;
}

export function clamp(value, minimum, maximum) {
  if (!Number.isFinite(value)) throw new Error('Audio value must be finite.');
  return Math.min(maximum, Math.max(minimum, value));
}

export function sanitizeOrigin(url) {
  try { return new URL(url).origin; }
  catch { return ''; }
}

export function validateAudioCommand(message) {
  if (message.protocolVersion !== PROTOCOL_VERSION || message.type !== 'tab.setAudio') {
    throw new Error('Unsupported audio command.');
  }
  return {
    volume: clamp(message.volume ?? 1, 0, 2),
    balance: clamp(message.balance ?? 0, -1, 1),
    muted: Boolean(message.muted),
    outputDeviceId: typeof message.outputDeviceId === 'string' ? message.outputDeviceId : '',
    outputDeviceName: typeof message.outputDeviceName === 'string' ? message.outputDeviceName : '',
    followSystemDefault: Boolean(message.followSystemDefault),
    resolvedOutputDeviceId: typeof message.resolvedOutputDeviceId === 'string' ? message.resolvedOutputDeviceId : '',
    resolvedOutputDeviceName: typeof message.resolvedOutputDeviceName === 'string' ? message.resolvedOutputDeviceName : '',
    outputDevices: Array.isArray(message.outputDevices) ? message.outputDevices.filter((device) =>
      device && typeof device.endpointId === 'string' && typeof device.friendlyName === 'string') : [],
    correlationId: typeof message.correlationId === 'string' && message.correlationId
      ? message.correlationId : crypto.randomUUID(),
    generation: Number.isSafeInteger(message.generation) && message.generation >= 0 ? message.generation : 0,
    requestSource: ['User', 'DeviceReconnect', 'ProfileRestore'].includes(message.requestSource)
      ? message.requestSource : 'ProfileRestore',
    forceAuthorization: Boolean(message.forceAuthorization),
    equalizer: normalizeEqualizer(message.equalizer)
  };
}

export function normalizeDeviceLabel(label) {
  return String(label || '')
    .trim()
    .replace(/^(default|communications)\s*[-–—:]\s*/iu, '')
    .replace(/\s+/gu, ' ')
    .toLocaleLowerCase();
}

export function matchOutputDevice(devices, requestedName) {
  const requested = normalizeDeviceLabel(requestedName);
  if (!requested) return null;
  const outputs = devices.filter((device) => device.kind === 'audiooutput' && device.deviceId !== '');
  const exact = outputs.filter((device) => normalizeDeviceLabel(device.label) === requested);
  if (exact.length === 1) return exact[0];
  const compatible = outputs.filter((device) => {
    const label = normalizeDeviceLabel(device.label);
    return label && (label.includes(requested) || requested.includes(label));
  });
  return compatible.length === 1 ? compatible[0] : null;
}
