using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;
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

internal interface IBrowserProcessLauncher
{
    void Launch(string executablePath, string address);
}

internal sealed class BrowserProcessLauncher : IBrowserProcessLauncher
{
    public void Launch(string executablePath, string address)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false
        };
        start.ArgumentList.Add(address);
        try
        {
            using var startedProcess = Process.Start(start);
            if (startedProcess is null)
                throw new InvalidOperationException($"浏览器进程未启动：{executablePath}");
            EnsureInternalPageOpened(executablePath, address, startedProcess);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException
                                          or ElementNotAvailableException)
        {
            throw new InvalidOperationException($"无法启动浏览器并打开 {address}。可复制该地址到对应浏览器的地址栏。", exception);
        }
    }

    private static void EnsureInternalPageOpened(string executablePath, string address, Process startedProcess)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        nint windowHandle = 0;
        ValuePattern? valuePattern = null;
        AutomationElement? addressBar = null;
        while (DateTime.UtcNow < deadline)
        {
            windowHandle = FindBrowserWindow(processName, startedProcess);
            if (windowHandle != 0)
            {
                var window = AutomationElement.FromHandle(windowHandle);
                addressBar = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews"));
                if (addressBar?.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) == true)
                {
                    valuePattern = (ValuePattern)pattern;
                    if (AddressMatches(valuePattern.Current.Value, address)) return;
                    break;
                }
            }
            Thread.Sleep(100);
        }

        if (windowHandle == 0 || addressBar is null || valuePattern is null)
            throw new InvalidOperationException("浏览器窗口已启动，但没有找到可验证的地址栏。");

        // Chromium 151 may discard chrome:// or edge:// arguments during a cold start. The explicit
        // user action authorizes bringing that browser's real omnibox forward and completing navigation.
        SetForegroundWindow(windowHandle);
        addressBar.SetFocus();
        valuePattern.SetValue(address);
        SendKeys.SendWait("{ENTER}");
        // SetValue changes the omnibox text before Chromium has accepted the navigation. Wait for
        // the browser to either commit the internal page or replace the text with its fallback page.
        Thread.Sleep(500);
        while (DateTime.UtcNow < deadline)
        {
            if (AddressMatches(valuePattern.Current.Value, address)) return;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException($"浏览器已启动，但管理页未能打开：{address}");
    }

    private static nint FindBrowserWindow(string processName, Process startedProcess)
    {
        var foreground = GetForegroundWindow();
        if (foreground != 0 && WindowBelongsToProcess(foreground, processName)) return foreground;
        try
        {
            startedProcess.Refresh();
            if (startedProcess.MainWindowHandle != 0) return startedProcess.MainWindowHandle;
        }
        catch (InvalidOperationException) { }
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.MainWindowHandle != 0) return process.MainWindowHandle;
            }
        }
        return 0;
    }

    private static bool WindowBelongsToProcess(nint windowHandle, string processName)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool AddressMatches(string? actual, string expected)
        => actual?.TrimEnd('/').Equals(expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}

internal sealed class BrowserOnboardingService : IBrowserOnboardingService
{
    private const string HostName = "com.audiosourcemixer.bridge";
    private readonly IBrowserProcessLauncher _processLauncher;
    private readonly Func<string, BrowserInstallation>? _detector;

    internal BrowserOnboardingService(IBrowserProcessLauncher? processLauncher = null,
        Func<string, BrowserInstallation>? detector = null)
    {
        _processLauncher = processLauncher ?? new BrowserProcessLauncher();
        _detector = detector;
    }
    public string ExtensionDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "BrowserExtension"));

    public BrowserInstallation Detect(string browser)
    {
        if (_detector is not null) return _detector(browser);
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
        var address = installation.Id switch
        {
            "edge" => "edge://extensions/",
            "chrome" => "chrome://extensions/",
            _ => throw new ArgumentOutOfRangeException(nameof(browser))
        };
        if (!installation.IsInstalled)
            throw new FileNotFoundException($"未检测到 {installation.DisplayName}。请安装后重试，或在对应浏览器地址栏输入 {address}。");
        _processLauncher.Launch(installation.ExecutablePath!, address);
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
