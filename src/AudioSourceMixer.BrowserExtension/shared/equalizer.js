export const EQUALIZER_MIN_GAIN_DB = -12;
export const EQUALIZER_MAX_GAIN_DB = 12;
export const EQUALIZER_MIN_PREAMP_DB = -12;
export const EQUALIZER_MAX_PREAMP_DB = 0;
export const EQUALIZER_OFF_PRESET_ID = 'off';
export const EQUALIZER_FLAT_PRESET_ID = 'flat';
export const EQUALIZER_CUSTOM_PRESET_ID = 'custom';

export const EQUALIZER_BANDS = Object.freeze([
  Object.freeze({ frequencyHz: 31, q: 0.707, label: '31 Hz', filterType: 'lowshelf' }),
  Object.freeze({ frequencyHz: 62, q: 1, label: '62 Hz', filterType: 'peaking' }),
  Object.freeze({ frequencyHz: 125, q: 1, label: '125 Hz', filterType: 'peaking' }),
  Object.freeze({ frequencyHz: 250, q: 1, label: '250 Hz', filterType: 'peaking' }),
  Object.freeze({ frequencyHz: 500, q: 1, label: '500 Hz', filterType: 'peaking' }),
  Object.freeze({ frequencyHz: 1000, q: 1, label: '1 kHz', filterType: 'peaking' }),
  Object.freeze({ frequencyHz: 2000, q: 1, label: '2 kHz', filterType: 'peaking' }),
  Object.freeze({ frequencyHz: 4000, q: 1, label: '4 kHz', filterType: 'peaking' }),
  Object.freeze({ frequencyHz: 8000, q: 1, label: '8 kHz', filterType: 'peaking' }),
  Object.freeze({ frequencyHz: 16000, q: 0.707, label: '16 kHz', filterType: 'highshelf' })
]);

const PRESET_GAINS = Object.freeze({
  off: Object.freeze([0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
  flat: Object.freeze([0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
  bass: Object.freeze([6, 5, 3, 1, 0, 0, 0, 0, -1, -1]),
  vocal: Object.freeze([-3, -2, -1, 0, 1, 3, 4, 2, 0, -1]),
  treble: Object.freeze([-2, -2, -1, 0, 0, 1, 2, 4, 5, 6]),
  warm: Object.freeze([3, 3, 2, 1, 1, 0, -1, -1, -2, -2]),
  custom: Object.freeze([0, 0, 0, 0, 0, 0, 0, 0, 0, 0])
});

export const EQUALIZER_PRESETS = Object.freeze([
  Object.freeze({ id: 'off', name: '关闭', gainsDb: PRESET_GAINS.off }),
  Object.freeze({ id: 'flat', name: '平直', gainsDb: PRESET_GAINS.flat }),
  Object.freeze({ id: 'bass', name: '低频增强', gainsDb: PRESET_GAINS.bass }),
  Object.freeze({ id: 'vocal', name: '人声清晰', gainsDb: PRESET_GAINS.vocal }),
  Object.freeze({ id: 'treble', name: '高频增强', gainsDb: PRESET_GAINS.treble }),
  Object.freeze({ id: 'warm', name: '温暖', gainsDb: PRESET_GAINS.warm }),
  Object.freeze({ id: 'custom', name: '自定义', gainsDb: PRESET_GAINS.custom })
]);

function requireFiniteRange(value, minimum, maximum, name) {
  if (!Number.isFinite(value) || value < minimum || value > maximum)
    throw new Error(`${name} must be finite and between ${minimum} and ${maximum}.`);
  return value;
}

export function createEqualizerPreset(presetId, preampDb = 0) {
  const preset = EQUALIZER_PRESETS.find((candidate) => candidate.id === presetId);
  if (!preset) throw new Error('Unknown equalizer preset.');
  requireFiniteRange(preampDb, EQUALIZER_MIN_PREAMP_DB, EQUALIZER_MAX_PREAMP_DB, 'preampDb');
  return {
    enabled: presetId !== EQUALIZER_OFF_PRESET_ID,
    presetId,
    preampDb,
    bands: EQUALIZER_BANDS.map((definition, index) => ({
      frequencyHz: definition.frequencyHz,
      q: definition.q,
      gainDb: preset.gainsDb[index]
    }))
  };
}

export function normalizeEqualizer(settings) {
  if (settings === undefined || settings === null) return createEqualizerPreset(EQUALIZER_OFF_PRESET_ID);
  if (typeof settings !== 'object' || Array.isArray(settings)) throw new Error('Equalizer settings must be an object.');
  if (typeof settings.enabled !== 'boolean') throw new Error('Equalizer enabled must be a boolean.');
  if (!EQUALIZER_PRESETS.some((preset) => preset.id === settings.presetId)) throw new Error('Unknown equalizer preset.');
  requireFiniteRange(settings.preampDb, EQUALIZER_MIN_PREAMP_DB, EQUALIZER_MAX_PREAMP_DB, 'preampDb');
  if (!Array.isArray(settings.bands) || settings.bands.length !== EQUALIZER_BANDS.length)
    throw new Error(`Equalizer requires exactly ${EQUALIZER_BANDS.length} bands.`);

  const bands = settings.bands.map((band, index) => {
    const expected = EQUALIZER_BANDS[index];
    if (!band || typeof band !== 'object' ||
        !Number.isFinite(band.frequencyHz) || Math.abs(band.frequencyHz - expected.frequencyHz) > 0.01 ||
        !Number.isFinite(band.q) || Math.abs(band.q - expected.q) > 0.001)
      throw new Error(`Equalizer band ${index} has an invalid frequency or Q.`);
    return {
      frequencyHz: expected.frequencyHz,
      q: expected.q,
      gainDb: requireFiniteRange(band.gainDb, EQUALIZER_MIN_GAIN_DB, EQUALIZER_MAX_GAIN_DB, `band ${index} gainDb`)
    };
  });

  if (!settings.enabled) return createEqualizerPreset(EQUALIZER_OFF_PRESET_ID);
  return {
    enabled: true,
    presetId: settings.presetId,
    preampDb: settings.preampDb,
    bands,
    ...(typeof settings.updatedAt === 'string' ? { updatedAt: settings.updatedAt } : {})
  };
}

export function effectiveHeadroomDb(settings) {
  const equalizer = normalizeEqualizer(settings);
  if (!equalizer.enabled) return 0;
  const maximumBoost = Math.max(0, ...equalizer.bands.map((band) => band.gainDb));
  return Math.min(equalizer.preampDb, -maximumBoost);
}

export function decibelsToGain(decibels) {
  if (!Number.isFinite(decibels)) throw new Error('Decibel value must be finite.');
  return 10 ** (decibels / 20);
}
