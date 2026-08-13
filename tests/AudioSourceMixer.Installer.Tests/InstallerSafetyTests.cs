namespace AudioSourceMixer.Installer.Tests;

public sealed class InstallerSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AudioSourceMixer-installer-tests-{Guid.NewGuid():N}");

    [Fact]
    public void RejectsBroadOrProtectedInstallTargets()
    {
        Assert.ThrowsAny<Exception>(() => Program.NormalizeAndValidateInstallPath(Path.GetPathRoot(Environment.SystemDirectory)!));
        Assert.ThrowsAny<Exception>(() => Program.NormalizeAndValidateInstallPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.ThrowsAny<Exception>(() => Program.NormalizeAndValidateInstallPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Theory]
    [InlineData("Audio Mixer With Spaces")]
    [InlineData("音频混音器")]
    public void AcceptsWritableCustomPaths(string leaf)
    {
        var expected = Path.GetFullPath(Path.Combine(_root, leaf));
        Assert.Equal(expected, Program.NormalizeAndValidateInstallPath(expected));
    }

    [Fact]
    public void NewInstallDefaultsKeepStartupDisabled()
    {
        var options = new Program.InstallOptions(Path.Combine(_root, "default"), false, false, true);
        Assert.False(options.StartWithWindows);
        Assert.True(options.StartInBackground);
    }

    [Fact]
    public void InstalledUninstallerSourceHasDedicatedNoArgumentModeAndNoInstallButton()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "AudioSourceMixer.Installer", "Program.cs"));
        Assert.Contains("installedUninstaller && args.Length == 0", source);
        var formStart = source.IndexOf("private sealed class UninstallerForm", StringComparison.Ordinal);
        Assert.True(formStart > 0);
        Assert.DoesNotContain("安装/升级", source[formStart..]);
        Assert.Contains("Forms.ProgressBarStyle.Marquee", source);
        Assert.Contains("private async void InstallClicked", source);
        Assert.Contains("安装/升级完成", source);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
