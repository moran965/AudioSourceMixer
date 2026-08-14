import test from 'node:test';
import assert from 'node:assert/strict';
import { smoothPeak } from '../../src/AudioSourceMixer.BrowserExtension/shared/levels.js';

test('tab meter attacks immediately, decays smoothly, clamps, and reaches zero', () => {
  assert.equal(smoothPeak(0.2, 0.8), 0.8);
  let peak = 1;
  for (let index = 0; index < 5; index += 1) peak = smoothPeak(peak, 0);
  assert.equal(peak, 0);
  assert.equal(smoothPeak(0, 4), 1);
  assert.ok(smoothPeak(0.7, Number.NaN) < 0.7);
});

test('independent tab meter state never crosses source identities', () => {
  const peaks = new Map([['chrome:1', 0], ['chrome:2', 0]]);
  peaks.set('chrome:1', smoothPeak(peaks.get('chrome:1'), 0.9));
  peaks.set('chrome:2', smoothPeak(peaks.get('chrome:2'), 0.2));
  assert.equal(peaks.get('chrome:1'), 0.9);
  assert.equal(peaks.get('chrome:2'), 0.2);
});
