using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Tests;

public sealed class AudioEffectsTests
{
    [Fact]
    public void CatalogDefinesExactTenBandFiltersAndFlatBypass()
    {
        Assert.Equal([31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f],
            EqualizerCatalog.Bands.Select(band => band.FrequencyHz).ToArray());
        Assert.Equal("lowshelf", EqualizerCatalog.Bands[0].FilterType);
        Assert.Equal("highshelf", EqualizerCatalog.Bands[^1].FilterType);
        Assert.All(EqualizerCatalog.CreatePreset(EqualizerCatalog.OffPresetId).Bands, band => Assert.Equal(0, band.GainDb));
        Assert.All(EqualizerCatalog.CreatePreset(EqualizerCatalog.FlatPresetId).Bands, band => Assert.Equal(0, band.GainDb));
        Assert.False(EqualizerCatalog.Off.Enabled);
        Assert.True(EqualizerCatalog.CreatePreset(EqualizerCatalog.FlatPresetId).Enabled);
    }

    [Theory]
    [InlineData("bass", new float[] { 6, 5, 3, 1, 0, 0, 0, 0, -1, -1 })]
    [InlineData("vocal", new float[] { -3, -2, -1, 0, 1, 3, 4, 2, 0, -1 })]
    [InlineData("treble", new float[] { -2, -2, -1, 0, 0, 1, 2, 4, 5, 6 })]
    [InlineData("warm", new float[] { 3, 3, 2, 1, 1, 0, -1, -1, -2, -2 })]
    public void PresetsHaveStableDocumentedGains(string presetId, float[] expected)
        => Assert.Equal(expected, EqualizerCatalog.CreatePreset(presetId).Bands.Select(band => band.GainDb).ToArray());

    [Fact]
    public void ValidationRejectsMissingBandsNonFiniteValuesWrongDefinitionsAndUnknownPreset()
    {
        var valid = EqualizerCatalog.CreatePreset("bass");
        Assert.Throws<ArgumentException>(() => EqualizerCatalog.Validate(valid with { Bands = valid.Bands.Take(9).ToArray() }));
        Assert.Throws<ArgumentException>(() => EqualizerCatalog.Validate(valid with { PresetId = "invented" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => EqualizerCatalog.Validate(valid with { PreampDb = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => EqualizerCatalog.Validate(valid with
            { Bands = valid.Bands.Select((band, index) => index == 0 ? band with { GainDb = float.PositiveInfinity } : band).ToArray() }));
        Assert.Throws<ArgumentException>(() => EqualizerCatalog.Validate(valid with
            { Bands = valid.Bands.Select((band, index) => index == 4 ? band with { FrequencyHz = 501 } : band).ToArray() }));
        Assert.Throws<ArgumentException>(() => EqualizerCatalog.Validate(valid with
            { Bands = valid.Bands.Select((band, index) => index == 4 ? band with { Q = 0 } : band).ToArray() }));
    }

    [Fact]
    public void HeadroomIsIndependentAndConservativelyOffsetsPositiveBoost()
    {
        var bass = EqualizerCatalog.CreatePreset("bass");
        Assert.Equal(-6, EqualizerCatalog.EffectiveHeadroomDb(bass));
        Assert.Equal(-9, EqualizerCatalog.EffectiveHeadroomDb(bass with { PreampDb = -9 }));
        Assert.Equal(0, EqualizerCatalog.EffectiveHeadroomDb(EqualizerCatalog.Off));
    }
}
