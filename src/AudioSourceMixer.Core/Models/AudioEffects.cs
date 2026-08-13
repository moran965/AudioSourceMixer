namespace AudioSourceMixer.Core.Models;

public sealed record EqualizerBandDefinition(float FrequencyHz, float Q, string Label, string FilterType);

public sealed record EqualizerBandSetting(float FrequencyHz, float Q, float GainDb);

public sealed record AudioEffectSettings(
    bool Enabled,
    string PresetId,
    float PreampDb,
    IReadOnlyList<EqualizerBandSetting> Bands,
    DateTimeOffset UpdatedAt = default);

public sealed record EqualizerPreset(string Id, string Name, IReadOnlyList<float> GainsDb);

public static class EqualizerCatalog
{
    public const float MinimumGainDb = -12;
    public const float MaximumGainDb = 12;
    public const float MinimumPreampDb = -12;
    public const float MaximumPreampDb = 0;
    public const string OffPresetId = "off";
    public const string FlatPresetId = "flat";
    public const string CustomPresetId = "custom";

    public static IReadOnlyList<EqualizerBandDefinition> Bands { get; } =
    [
        new(31, 0.707f, "31 Hz", "lowshelf"),
        new(62, 1, "62 Hz", "peaking"),
        new(125, 1, "125 Hz", "peaking"),
        new(250, 1, "250 Hz", "peaking"),
        new(500, 1, "500 Hz", "peaking"),
        new(1000, 1, "1 kHz", "peaking"),
        new(2000, 1, "2 kHz", "peaking"),
        new(4000, 1, "4 kHz", "peaking"),
        new(8000, 1, "8 kHz", "peaking"),
        new(16000, 0.707f, "16 kHz", "highshelf")
    ];

    public static IReadOnlyList<EqualizerPreset> Presets { get; } =
    [
        new(OffPresetId, "关闭", [0,0,0,0,0,0,0,0,0,0]),
        new(FlatPresetId, "平直", [0,0,0,0,0,0,0,0,0,0]),
        new("bass", "低频增强", [6,5,3,1,0,0,0,0,-1,-1]),
        new("vocal", "人声清晰", [-3,-2,-1,0,1,3,4,2,0,-1]),
        new("treble", "高频增强", [-2,-2,-1,0,0,1,2,4,5,6]),
        new("warm", "温暖", [3,3,2,1,1,0,-1,-1,-2,-2]),
        new(CustomPresetId, "自定义", [0,0,0,0,0,0,0,0,0,0])
    ];

    public static AudioEffectSettings Off { get; } = CreatePreset(OffPresetId);

    public static AudioEffectSettings CreatePreset(string presetId, float preampDb = 0)
    {
        var preset = Presets.SingleOrDefault(item => item.Id == presetId)
                     ?? throw new ArgumentOutOfRangeException(nameof(presetId));
        var enabled = preset.Id != OffPresetId;
        var gains = preset.Id == CustomPresetId ? Presets.Single(item => item.Id == FlatPresetId).GainsDb : preset.GainsDb;
        return new AudioEffectSettings(enabled, preset.Id, EnsurePreamp(preampDb),
            Bands.Select((band, index) => new EqualizerBandSetting(band.FrequencyHz, band.Q, gains[index])).ToArray());
    }

    public static AudioEffectSettings Normalize(AudioEffectSettings? settings)
    {
        if (settings is null) return Off;
        Validate(settings);
        if (!settings.Enabled) return Off;
        return settings with
        {
            PresetId = Presets.Any(item => item.Id == settings.PresetId) ? settings.PresetId : CustomPresetId,
            PreampDb = EnsurePreamp(settings.PreampDb),
            Bands = settings.Bands.Select((band, index) => band with
            {
                FrequencyHz = Bands[index].FrequencyHz,
                Q = Bands[index].Q,
                GainDb = EnsureGain(band.GainDb)
            }).ToArray()
        };
    }

    public static void Validate(AudioEffectSettings settings)
    {
        if (!Presets.Any(item => item.Id == settings.PresetId))
            throw new ArgumentException("Equalizer preset is invalid.", nameof(settings));
        if (settings.Bands is null || settings.Bands.Count != Bands.Count)
            throw new ArgumentException($"Equalizer requires exactly {Bands.Count} bands.", nameof(settings));
        _ = EnsurePreamp(settings.PreampDb);
        for (var index = 0; index < Bands.Count; index++)
        {
            var actual = settings.Bands[index];
            var expected = Bands[index];
            if (!float.IsFinite(actual.FrequencyHz) || Math.Abs(actual.FrequencyHz - expected.FrequencyHz) > 0.01f ||
                !float.IsFinite(actual.Q) || Math.Abs(actual.Q - expected.Q) > 0.001f)
                throw new ArgumentException($"Equalizer band {index} has an invalid frequency or Q.", nameof(settings));
            _ = EnsureGain(actual.GainDb);
        }
    }

    public static float EffectiveHeadroomDb(AudioEffectSettings settings)
    {
        var normalized = Normalize(settings);
        if (!normalized.Enabled) return 0;
        var maximumBoost = normalized.Bands.Max(band => Math.Max(0, band.GainDb));
        return Math.Min(normalized.PreampDb, -maximumBoost);
    }

    private static float EnsureGain(float value)
    {
        if (!float.IsFinite(value) || value < MinimumGainDb || value > MaximumGainDb)
            throw new ArgumentOutOfRangeException(nameof(value), $"EQ gain must be between {MinimumGainDb} and {MaximumGainDb} dB.");
        return value;
    }

    private static float EnsurePreamp(float value)
    {
        if (!float.IsFinite(value) || value < MinimumPreampDb || value > MaximumPreampDb)
            throw new ArgumentOutOfRangeException(nameof(value), $"EQ preamp must be between {MinimumPreampDb} and {MaximumPreampDb} dB.");
        return value;
    }
}
