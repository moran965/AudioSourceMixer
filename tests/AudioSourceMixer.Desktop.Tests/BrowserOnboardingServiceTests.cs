using AudioSourceMixer.Desktop.Services;
using System.IO;

namespace AudioSourceMixer.Desktop.Tests;

public sealed class BrowserOnboardingServiceTests
{
    [Theory]
    [InlineData("edge", "C:\\Browser\\msedge.exe", "edge://extensions/")]
    [InlineData("chrome", "C:\\Browser\\chrome.exe", "chrome://extensions/")]
    public void ExtensionManagementUsesExactBrowserExecutableAndInternalAddressOnly(
        string browser, string executable, string expectedAddress)
    {
        var launcher = new RecordingLauncher();
        var service = new BrowserOnboardingService(launcher, id => new BrowserInstallation(
            id, id == "edge" ? "Microsoft Edge" : "Google Chrome", executable,
            new Uri("https://chromewebstore.google.com/detail/example/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));

        service.OpenExtensionsPage(browser);

        Assert.Equal(executable, launcher.ExecutablePath);
        Assert.Equal(expectedAddress, launcher.Address);
        Assert.DoesNotContain("--new-tab", launcher.Address, StringComparison.Ordinal);
        Assert.DoesNotContain("http", launcher.Address, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingBrowserReportsExactFallbackAddress()
    {
        var service = new BrowserOnboardingService(new RecordingLauncher(), id =>
            new BrowserInstallation(id, "Google Chrome", null, null));
        var exception = Assert.Throws<FileNotFoundException>(() => service.OpenExtensionsPage("chrome"));
        Assert.Contains("chrome://extensions/", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingLauncher : IBrowserProcessLauncher
    {
        public string ExecutablePath { get; private set; } = string.Empty;
        public string Address { get; private set; } = string.Empty;
        public void Launch(string executablePath, string address)
        {
            ExecutablePath = executablePath;
            Address = address;
        }
    }
}
