using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Desktop.ViewModels;

public sealed class EqualizerBandViewModel : ObservableObject
{
    private readonly Action<EqualizerBandViewModel> _changed;
    private double _gainDb;
    private bool _synchronizing;

    public EqualizerBandViewModel(EqualizerBandDefinition definition, Action<EqualizerBandViewModel> changed)
    {
        Definition = definition;
        _changed = changed;
    }

    public EqualizerBandDefinition Definition { get; }
    public string Label => Definition.Label;
    public float FrequencyHz => Definition.FrequencyHz;
    public float Q => Definition.Q;
    public double GainDb
    {
        get => _gainDb;
        set
        {
            if (!Set(ref _gainDb, Math.Clamp(value, EqualizerCatalog.MinimumGainDb, EqualizerCatalog.MaximumGainDb))) return;
            if (!_synchronizing) _changed(this);
        }
    }

    internal void Synchronize(double value)
    {
        _synchronizing = true;
        try { Set(ref _gainDb, value, nameof(GainDb)); }
        finally { _synchronizing = false; }
    }
}

public sealed class EqualizerPresetOption(string id, string name) : ObservableObject
{
    private string _name = name;
    public string Id { get; } = id;
    public string Name => _name;
    internal void UpdateName(string value) => Set(ref _name, value, nameof(Name));
}
