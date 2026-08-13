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

    [Fact]
    public void RuntimeAllowlistIsExplicitUniqueAndContainsOnlyReachableProductFiles()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "packaging", "runtime-allowlist.json")));
        var runtime = document.RootElement.GetProperty("runtimeFiles").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()!).ToArray();
        var portableOnly = document.RootElement.GetProperty("portableOnlyFiles").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()!).ToArray();
        var generated = document.RootElement.GetProperty("installerGeneratedFiles").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()!).ToArray();
        var all = runtime.Concat(portableOnly).Concat(generated).ToArray();

        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, path =>
        {
            Assert.DoesNotContain('\\', path);
            Assert.DoesNotContain("..", path);
            Assert.DoesNotMatch(@"(^|/)(docs|tests|tools|diagnostics)(/|$)", path);
            Assert.DoesNotMatch(@"\.(pdb|cs|csproj|sln|map)$", path);
        });
        Assert.Contains("BrowserExtension/shared/equalizer.js", runtime);
        Assert.DoesNotContain(runtime, path => path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase));

        foreach (var path in runtime.Where(path => path.StartsWith("BrowserExtension/", StringComparison.Ordinal)))
            Assert.True(File.Exists(Path.Combine(root, "src", "AudioSourceMixer.BrowserExtension",
                path["BrowserExtension/".Length..].Replace('/', Path.DirectorySeparatorChar))), path);

        var portableScript = File.ReadAllText(Path.Combine(root, "scripts", "package-portable.ps1"));
        var installerScript = File.ReadAllText(Path.Combine(root, "scripts", "package-installer.ps1"));
        Assert.Contains("Assert-RuntimePayload $portable 'Portable'", portableScript);
        Assert.DoesNotContain("Copy-Item -LiteralPath '.\\docs'", portableScript);
        Assert.Contains("Get-ExpectedPayloadPaths 'InstallerPayload'", installerScript);
        Assert.Contains("Assert-RuntimePayload $payloadDirectory 'InstallerPayload'", installerScript);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
