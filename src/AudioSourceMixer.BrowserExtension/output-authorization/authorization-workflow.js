import { normalizeDeviceLabel } from '../shared/protocol.js';

const GENERIC_TOKENS = new Set([
  'default', 'communications', 'speaker', 'speakers', 'headphone', 'headphones',
  '扬声器', '耳机', '蓝牙', 'bluetooth', 'audio', 'device', 'output', 'stereo', '立体声'
]);

export function createOutputMappingCandidate(request, selected, browser, now = new Date().toISOString()) {
  if (!request?.windowsEndpointId) throw new Error('没有待设置的 Windows 输出设备。');
  if (!selected?.deviceId) throw new Error('没有选择浏览器输出设备。');
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
    return { level: 'unknown', message: '设备名称信息不足，请通过测试声音确认。' };
  const overlap = windowsTokens.some((token) => browserTokens.some((candidate) =>
    candidate === token || candidate.includes(token) || token.includes(candidate)));
  if (overlap) return { level: 'likely', message: '名称看起来一致，仍请播放测试声音确认。' };
  return { level: 'warning', message: '两个名称看起来不一致。请重新选择；若确实是同一设备，保存前需要再次确认。' };
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
    if (typeof context.setSinkId !== 'function') throw new Error('当前浏览器不支持输出设备试听。');
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

function meaningfulTokens(value) {
  return normalizeDeviceLabel(value)
    .replace(/[()\[\]{},.:;，。；：_\\-]+/gu, ' ')
    .split(/\s+/u)
    .map((token) => token.trim())
    .filter((token) => token.length >= 2 && !GENERIC_TOKENS.has(token));
}
