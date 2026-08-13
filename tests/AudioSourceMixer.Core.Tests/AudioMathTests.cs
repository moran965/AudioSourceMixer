using AudioSourceMixer.Core;

namespace AudioSourceMixer.Core.Tests;

public sealed class AudioMathTests
{
    [Theory]
    [InlineData(-1, 1, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(-0.25, 1, 0.75)]
    [InlineData(0.25, 0.75, 1)]
    public void BalanceMapsToExpectedGains(float balance, float left, float right)
    {
        var gains = AudioMath.BalanceToGains(balance);
        Assert.Equal(left, gains.Left, 4);
        Assert.Equal(right, gains.Right, 4);
    }

    [Theory]
    [InlineData(-1.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void BalanceRejectsInvalidValues(float value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AudioMath.BalanceToGains(value));

    [Theory]
    [InlineData(0)]
    [InlineData(0.5f)]
    [InlineData(1)]
    public void VolumeAcceptsUnitInterval(float volume) => Assert.Equal(volume, AudioMath.EnsureVolume(volume));

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.PositiveInfinity)]
    public void VolumeRejectsInvalidValues(float volume)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AudioMath.EnsureVolume(volume));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0.01f)]
    [InlineData(50, 0.5f)]
    [InlineData(99, 0.99f)]
    [InlineData(100, 1f)]
    [InlineData(101, 1.01f)]
    [InlineData(125, 1.25f)]
    [InlineData(150, 1.5f)]
    [InlineData(175, 1.75f)]
    [InlineData(199, 1.99f)]
    [InlineData(200, 2f)]
    public void BrowserVolumePercentMapsToGain(double percent, float gain)
        => Assert.Equal(gain, AudioMath.VolumePercentToGain(percent), 5);

    [Theory]
    [InlineData(-1)]
    [InlineData(201)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void BrowserVolumePercentRejectsInvalidValues(double percent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AudioMath.VolumePercentToGain(percent));
}
