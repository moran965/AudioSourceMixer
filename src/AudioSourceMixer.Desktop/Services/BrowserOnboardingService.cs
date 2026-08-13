using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AudioSourceMixer.Desktop.Services;

internal sealed record BrowserInstallation(string Id, string DisplayName, string? ExecutablePath, Uri? StoreUri)
{
    public bool IsInstalled => ExecutablePath is not null;
}

internal interface IBrowserOnboardingService
{
    string ExtensionDirectory { get; }
    BrowserInstallation Detect(string browser);
    string NativeHostRegistrationStatus { get; }
    void OpenExtensionsPage(string browser);
    void OpenExtensionDirectory();
    void CopyExtensionDirectory();
}

internal sealed class BrowserOnboardingService : IBrowserOnboardingService
{
    private const string HostName = "com.audiosourcemixer.bridge";
    public string ExtensionDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "BrowserExtension"));

    public BrowserInstallation Detect(string browser)
    {
        var normalized = browser.ToLowerInvariant();
        var displayName = normalized == "edge" ? "Microsoft Edge" : normalized == "chrome" ? "Google Chrome"
            : throw new ArgumentOutOfRangeException(nameof(browser));
        var relative = normalized == "edge" ? @"Microsoft\Edge\Application\msedge.exe" : @"Google\Chrome\Application\chrome.exe";
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), relative),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), relative),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), relative)
        };
        return new BrowserInstallation(normalized, displayName,
            candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)), GetStoreUri(normalized));
    }

    public string NativeHostRegistrationStatus
    {
        get
        {
            var chrome = ReadRegisteredManifest(@$"Software\Google\Chrome\NativeMessagingHosts\{HostName}");
            var edge = ReadRegisteredManifest(@$"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}");
            if (chrome is null && edge is null) return "桌面桥尚未注册；请重新运行安装程序或便携版注册脚本。";
            if (!PathEquals(chrome, edge)) return "Chrome 与 Edge 的桌面桥注册不一致，建议重新安装。";
            return chrome is not null && File.Exists(chrome)
                ? "桌面桥注册正常。"
                : "桌面桥清单不存在，建议重新安装。";
        }
    }

    public void OpenExtensionsPage(string browser)
    {
        var installation = Detect(browser);
        if (!installation.IsInstalled) throw new FileNotFoundException($"未检测到 {installation.DisplayName}。 ");
        var url = installation.StoreUri?.AbsoluteUri ?? (installation.Id == "edge" ? "edge://extensions" : "chrome://extensions");
        var start = new ProcessStartInfo(installation.ExecutablePath!) { UseShellExecute = true };
        start.ArgumentList.Add("--new-tab");
        start.ArgumentList.Add(url);
        Process.Start(start);
    }

    public void OpenExtensionDirectory()
    {
        if (!Directory.Exists(ExtensionDirectory)) throw new DirectoryNotFoundException("安装目录中没有浏览器扩展文件。请修复安装。 ");
        Process.Start(new ProcessStartInfo(ExtensionDirectory) { UseShellExecute = true });
    }

    public void CopyExtensionDirectory()
    {
        if (!Directory.Exists(ExtensionDirectory)) throw new DirectoryNotFoundException("安装目录中没有浏览器扩展文件。请修复安装。 ");
        System.Windows.Clipboard.SetText(ExtensionDirectory);
    }

    private static string? ReadRegisteredManifest(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(null) as string;
    }

    private static bool PathEquals(string? left, string? right)
        => left is not null && right is not null && Path.GetFullPath(left)
            .Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static Uri? GetStoreUri(string browser)
    {
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "browser-extension-origins.json");
        if (!File.Exists(configurationPath)) return null;
        var configuration = System.Text.Json.JsonSerializer.Deserialize<TrustedBrowserConfiguration>(File.ReadAllText(configurationPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var rawUrl = browser == "edge" ? configuration?.EdgeStoreUrl : configuration?.ChromeStoreUrl;
        var extensionId = browser == "edge" ? configuration?.EdgeStoreExtensionId : configuration?.ChromeStoreExtensionId;
        if (string.IsNullOrWhiteSpace(rawUrl) && string.IsNullOrWhiteSpace(extensionId)) return null;
        if (string.IsNullOrWhiteSpace(rawUrl) || extensionId is null || !System.Text.RegularExpressions.Regex.IsMatch(extensionId, "^[a-p]{32}$"))
            throw new InvalidDataException($"{browser} 商店 URL 与受信任扩展 ID 必须同时配置。");
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"{browser} 商店 URL 必须使用 HTTPS。");
        var expectedHost = browser == "edge" ? "microsoftedge.microsoft.com" : "chromewebstore.google.com";
        if (!uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.TrimEnd('/').EndsWith('/' + extensionId, StringComparison.Ordinal))
            throw new InvalidDataException($"{browser} 商店 URL 必须是与受信任扩展 ID 匹配的官方商店页面。");
        return uri;
    }

    private sealed record TrustedBrowserConfiguration(string? ChromeStoreExtensionId, string? EdgeStoreExtensionId,
        string? ChromeStoreUrl, string? EdgeStoreUrl);
}
