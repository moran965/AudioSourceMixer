import test from 'node:test';
import assert from 'node:assert/strict';
import {
  EQUALIZER_BANDS,
  EQUALIZER_PRESETS,
  createEqualizerPreset,
  decibelsToGain,
  effectiveHeadroomDb,
  normalizeEqualizer
} from '../../src/AudioSourceMixer.BrowserExtension/shared/equalizer.js';

test('equalizer catalog defines the exact immutable ten-band layout and presets', () => {
  assert.deepEqual(EQUALIZER_BANDS.map((band) => band.frequencyHz),
    [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]);
  assert.equal(EQUALIZER_BANDS[0].filterType, 'lowshelf');
  assert.equal(EQUALIZER_BANDS.at(-1).filterType, 'highshelf');
  assert.deepEqual(EQUALIZER_PRESETS.map((preset) => preset.id),
    ['off', 'flat', 'bass', 'vocal', 'treble', 'warm', 'custom']);
  assert.deepEqual(createEqualizerPreset('bass').bands.map((band) => band.gainDb),
    [6, 5, 3, 1, 0, 0, 0, 0, -1, -1]);
  assert.deepEqual(createEqualizerPreset('vocal').bands.map((band) => band.gainDb),
    [-3, -2, -1, 0, 1, 3, 4, 2, 0, -1]);
  assert.ok(Object.isFrozen(EQUALIZER_BANDS));
  assert.ok(Object.isFrozen(EQUALIZER_BANDS[0]));
});

test('off and flat are zero dB while manual changes validate as custom', () => {
  const off = createEqualizerPreset('off');
  const flat = createEqualizerPreset('flat');
  assert.equal(off.enabled, false);
  assert.equal(flat.enabled, true);
  assert.ok(off.bands.every((band) => band.gainDb === 0));
  assert.ok(flat.bands.every((band) => band.gainDb === 0));
  const custom = normalizeEqualizer({ ...flat, presetId: 'custom', bands: flat.bands.map((band, index) =>
    index === 4 ? { ...band, gainDb: 3.5 } : band) });
  assert.equal(custom.presetId, 'custom');
  assert.equal(custom.bands[4].gainDb, 3.5);
});

test('equalizer rejects missing, non-finite, out-of-range, and arbitrary band input', () => {
  const valid = createEqualizerPreset('warm');
  assert.throws(() => normalizeEqualizer({ ...valid, bands: valid.bands.slice(1) }), /exactly 10/);
  assert.throws(() => normalizeEqualizer({ ...valid, preampDb: Number.NaN }), /finite/);
  assert.throws(() => normalizeEqualizer({ ...valid, presetId: 'invented' }), /Unknown/);
  assert.throws(() => normalizeEqualizer({ ...valid, bands: valid.bands.map((band, index) =>
    index === 0 ? { ...band, gainDb: Infinity } : band) }), /finite/);
  assert.throws(() => normalizeEqualizer({ ...valid, bands: valid.bands.map((band, index) =>
    index === 2 ? { ...band, frequencyHz: 126 } : band) }), /frequency or Q/);
  assert.throws(() => normalizeEqualizer({ ...valid, bands: valid.bands.map((band, index) =>
    index === 2 ? { ...band, q: 0 } : band) }), /frequency or Q/);
});

test('headroom gain is independent and conservatively offsets boost', () => {
  assert.equal(effectiveHeadroomDb(createEqualizerPreset('bass')), -6);
  assert.equal(effectiveHeadroomDb(createEqualizerPreset('bass', -9)), -9);
  assert.equal(effectiveHeadroomDb(createEqualizerPreset('off')), 0);
  assert.ok(Math.abs(decibelsToGain(-6) - 0.501187) < 0.000001);
});
