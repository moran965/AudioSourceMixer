namespace AudioSourceMixer.Core;

public readonly record struct StereoGains(float Left, float Right);

public static class AudioMath
{
    public static StereoGains BalanceToGains(float balance)
    {
        EnsureRange(balance, -1f, 1f, nameof(balance));
        return balance <= 0
            ? new StereoGains(1f, 1f + balance)
            : new StereoGains(1f - balance, 1f);
    }

    public static float EnsureVolume(float volume)
        => EnsureSessionVolume(volume);

    public static float EnsureSessionVolume(float volume)
    {
        EnsureRange(volume, 0f, 1f, nameof(volume));
        return volume;
    }

    public static float EnsureUserGain(float gain)
    {
        EnsureRange(gain, 0f, 2f, nameof(gain));
        return gain;
    }

    public static float VolumePercentToGain(double volumePercent)
    {
        if (double.IsNaN(volumePercent) || double.IsInfinity(volumePercent) || volumePercent < 0 || volumePercent > 200)
            throw new ArgumentOutOfRangeException(nameof(volumePercent), volumePercent, "Volume percent must be between 0 and 200.");
        return (float)(volumePercent / 100d);
    }

    private static void EnsureRange(float value, float minimum, float maximum, string parameterName)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be between {minimum} and {maximum}.");
        }
    }
}
