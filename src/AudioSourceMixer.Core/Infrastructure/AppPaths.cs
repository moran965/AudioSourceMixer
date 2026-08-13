namespace AudioSourceMixer.Core.Infrastructure;

public static class AppPaths
{
    public static string LocalDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioSourceMixer");

    public static string LogsDirectory => Path.Combine(LocalDataDirectory, "logs");
}
