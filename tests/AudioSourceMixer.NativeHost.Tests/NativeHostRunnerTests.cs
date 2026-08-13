namespace AudioSourceMixer.NativeHost.Tests;

public sealed class NativeHostRunnerTests
{
    [Fact]
    public async Task RejectsNonExtensionCallerBeforeOpeningPipe()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        using var error = new StringWriter();
        var exitCode = await NativeHostRunner.RunAsync(["https://example.com/"], input, output, error);
        Assert.Equal(3, exitCode);
        Assert.Contains("not a valid", error.ToString());
    }

    [Fact]
    public async Task PipeTimeoutReportsUnavailableWithoutStartingDesktop()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        using var error = new StringWriter();
        var desktopProcessesBefore = System.Diagnostics.Process.GetProcessesByName("AudioSourceMixer")
            .Select(process => process.Id).ToHashSet();

        var exitCode = await NativeHostRunner.RunAsync([], input, output, error,
            pipeName: $"AudioSourceMixer.Missing.{Guid.NewGuid():N}", connectTimeoutMilliseconds: 25);

        Assert.Equal(2, exitCode);
        Assert.Contains("without starting", error.ToString());
        var newDesktopProcesses = System.Diagnostics.Process.GetProcessesByName("AudioSourceMixer")
            .Where(process => !desktopProcessesBefore.Contains(process.Id)).ToArray();
        try { Assert.Empty(newDesktopProcesses); }
        finally { foreach (var process in newDesktopProcesses) process.Dispose(); }
    }
}
