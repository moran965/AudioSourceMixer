import { clamp } from './protocol.js';

export function smoothPeak(previous, current, intervalMs = 100, decayMs = 350) {
  const normalizedCurrent = Number.isFinite(current) ? clamp(current, 0, 1) : 0;
  const normalizedPrevious = Number.isFinite(previous) ? clamp(previous, 0, 1) : 0;
  if (normalizedCurrent >= normalizedPrevious) return normalizedCurrent;
  const elapsed = clamp(intervalMs, 20, 200);
  const next = Math.max(normalizedCurrent, normalizedPrevious - elapsed / decayMs);
  return normalizedCurrent === 0 && next < 0.002 ? 0 : clamp(next, 0, 1);
}
