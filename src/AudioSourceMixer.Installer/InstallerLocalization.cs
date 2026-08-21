using System.Globalization;

namespace AudioSourceMixer.Installer;

internal static class InstallerLocalization
{
    internal const string Chinese = "zh-CN";
    internal const string English = "en-US";
    internal static IReadOnlyList<string> SupportedLanguages { get; } = [Chinese, English];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Resources =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Chinese] = ChineseResources(),
            [English] = EnglishResources()
        };

    internal static string CurrentLanguage { get; private set; } = SystemLanguage();
    internal static IReadOnlyCollection<string> Keys => Resources[English].Keys.ToArray();
    internal static string Text(string key) => Get(key, CurrentLanguage);

    static InstallerLocalization()
    {
        var expected = Resources[English].Keys.Order(StringComparer.Ordinal).ToArray();
        foreach (var resource in Resources.Values)
        {
            if (!expected.SequenceEqual(resource.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
                resource.Values.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Installer localization resources are incomplete.");
        }
    }

    internal static string Normalize(string? language)
    {
        if (string.Equals(language, Chinese, StringComparison.OrdinalIgnoreCase)) return Chinese;
        if (string.Equals(language, English, StringComparison.OrdinalIgnoreCase)) return English;
        throw new ArgumentException($"Unsupported language '{language}'. Use zh-CN or en-US.", nameof(language));
    }

    internal static void SetLanguage(string language)
    {
        CurrentLanguage = Normalize(language);
        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    internal static string SystemLanguage() =>
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;

    internal static string Get(string key, string language)
    {
        var normalized = string.Equals(language, Chinese, StringComparison.OrdinalIgnoreCase) ? Chinese : English;
        return Resources[normalized].TryGetValue(key, out var value) ? value : Resources[English]["Common.TextUnavailable"];
    }

    internal static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    private static IReadOnlyDictionary<string, string> ChineseResources() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Common.TextUnavailable"] = "文本不可用",
        ["Common.ProductName"] = "Audio Source Mixer",
        ["Common.Cancel"] = "取消",
        ["Common.Close"] = "关闭",
        ["Language.Title"] = "Choose language / 选择语言",
        ["Language.Heading"] = "Choose language / 选择语言",
        ["Language.Description"] = "Select the language for setup and the first app launch.\r\n选择安装程序和应用首次启动时使用的语言。",
        ["Language.Chinese"] = "简体中文",
        ["Language.English"] = "English",
        ["Language.Label"] = "Language / 语言",
        ["Language.Continue"] = "Continue / 继续",
        ["Install.Title"] = "安装 Audio Source Mixer",
        ["Install.Location"] = "安装位置",
        ["Install.Browse"] = "浏览…",
        ["Install.BrowseDescription"] = "选择 Audio Source Mixer 安装目录",
        ["Install.DesktopShortcut"] = "创建桌面快捷方式",
        ["Install.StartWithWindows"] = "登录 Windows 后启动 Audio Source Mixer",
        ["Install.StartInTray"] = "启动后最小化到系统托盘",
        ["Install.BrowserSetup"] = "安装完成后设置浏览器标签页增强（可选）",
        ["Install.BrowserExplanation"] = "分别控制 Chrome/Edge 标签页；需要你在浏览器确认加载。不录音、不上传网页或音频，不影响普通应用控制。",
        ["Install.Ready"] = "准备安装",
        ["Install.Action"] = "安装/升级",
        ["Install.Working"] = "正在安全展开并提交安装文件…",
        ["Install.Completed"] = "安装/升级完成：{0}",
        ["Install.CompletedDialog"] = "安装/升级完成。\r\n位置：{0}",
        ["Install.Failed"] = "安装失败：{0}",
        ["Install.MoveBlocked"] = "升级时不能直接迁移安装目录；请先卸载旧版本（保留设置），再选择新位置安装。",
        ["Install.TargetWarning"] = "目标目录包含不属于 Audio Source Mixer 的文件。为保护这些文件，安装不会覆盖该目录。请选择空目录。",
        ["Install.TargetRejected"] = "目标目录非空且未通过产品身份验证。",
        ["Install.PathEmpty"] = "安装路径不能为空。",
        ["Install.DriveRoot"] = "不能安装到磁盘根目录。",
        ["Install.ProtectedRoot"] = "所选路径是受保护的系统、用户或仓库根目录。",
        ["Install.ParentMissing"] = "安装路径没有有效父目录。",
        ["Install.SetupPathUnknown"] = "无法确定安装程序路径。",
        ["Install.PayloadMissing"] = "安装负载缺失。",
        ["Install.PayloadUnsafe"] = "安装负载包含不安全路径。",
        ["Install.IconMissing"] = "无法读取安装器图标。",
        ["Install.OperationFailed"] = "操作失败：{0}",
        ["Uninstall.Title"] = "卸载 Audio Source Mixer",
        ["Uninstall.Heading"] = "卸载 Audio Source Mixer",
        ["Uninstall.Description"] = "程序会先恢复音频并退出，然后删除安装文件。",
        ["Uninstall.RemoveData"] = "同时删除用户设置和日志（默认保留）",
        ["Uninstall.Action"] = "卸载",
        ["Uninstall.Confirm"] = "卸载 Audio Source Mixer？默认保留用户设置和日志。",
        ["Uninstall.IdentityFailed"] = "卸载目录未通过产品身份和注册表交叉验证，已拒绝删除。",
        ["Uninstall.DataPathFailed"] = "用户数据路径验证失败。",
        ["Uninstall.RecordMissing"] = "找不到已安装产品记录。",
        ["Uninstall.LocationMismatch"] = "卸载程序位置与注册表安装位置不一致。",
        ["Audio.ExitTimeout"] = "Audio Source Mixer 未能在恢复音频后及时退出。请从托盘退出后重试。",
        ["Browser.ConfigVersion"] = "不支持的浏览器扩展信任配置版本。",
        ["Browser.ConfigIds"] = "浏览器扩展信任配置包含缺失或无效的扩展 ID。",
        ["Browser.ConfigDevelopmentId"] = "浏览器扩展信任配置缺少当前开发版扩展 ID。",
        ["Shortcut.Description"] = "独立控制 Windows 音频会话",
        ["Shortcut.ShellMissing"] = "WScript.Shell 不可用。"
    };

    private static IReadOnlyDictionary<string, string> EnglishResources() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Common.TextUnavailable"] = "Text unavailable",
        ["Common.ProductName"] = "Audio Source Mixer",
        ["Common.Cancel"] = "Cancel",
        ["Common.Close"] = "Close",
        ["Language.Title"] = "Choose language / 选择语言",
        ["Language.Heading"] = "Choose language / 选择语言",
        ["Language.Description"] = "Select the language for setup and the first app launch.\r\n选择安装程序和应用首次启动时使用的语言。",
        ["Language.Chinese"] = "简体中文",
        ["Language.English"] = "English",
        ["Language.Label"] = "Language / 语言",
        ["Language.Continue"] = "Continue / 继续",
        ["Install.Title"] = "Install Audio Source Mixer",
        ["Install.Location"] = "Install location",
        ["Install.Browse"] = "Browse…",
        ["Install.BrowseDescription"] = "Choose the Audio Source Mixer installation folder",
        ["Install.DesktopShortcut"] = "Create a desktop shortcut",
        ["Install.StartWithWindows"] = "Start Audio Source Mixer after signing in to Windows",
        ["Install.StartInTray"] = "Start minimized in the notification area",
        ["Install.BrowserSetup"] = "Set up browser-tab enhancement after installation (optional)",
        ["Install.BrowserExplanation"] = "Control Chrome and Edge tabs separately. You confirm loading in the browser. No recording or upload of pages or audio; normal app controls are unaffected.",
        ["Install.Ready"] = "Ready to install",
        ["Install.Action"] = "Install / upgrade",
        ["Install.Working"] = "Safely extracting and committing installation files…",
        ["Install.Completed"] = "Installation / upgrade completed: {0}",
        ["Install.CompletedDialog"] = "Installation / upgrade completed.\r\nLocation: {0}",
        ["Install.Failed"] = "Installation failed: {0}",
        ["Install.MoveBlocked"] = "The installation directory cannot be moved during an upgrade. Uninstall the old version while keeping settings, then install to the new location.",
        ["Install.TargetWarning"] = "The target folder contains files that do not belong to Audio Source Mixer. Setup will not overwrite it. Choose an empty folder.",
        ["Install.TargetRejected"] = "The target folder is not empty and did not pass product identity validation.",
        ["Install.PathEmpty"] = "The installation path cannot be empty.",
        ["Install.DriveRoot"] = "Installation to a drive root is not allowed.",
        ["Install.ProtectedRoot"] = "The selected path is a protected system, user, or repository root.",
        ["Install.ParentMissing"] = "The installation path has no valid parent folder.",
        ["Install.SetupPathUnknown"] = "The setup executable path could not be determined.",
        ["Install.PayloadMissing"] = "The installation payload is missing.",
        ["Install.PayloadUnsafe"] = "The installation payload contains an unsafe path.",
        ["Install.IconMissing"] = "The setup icon could not be loaded.",
        ["Install.OperationFailed"] = "Operation failed: {0}",
        ["Uninstall.Title"] = "Uninstall Audio Source Mixer",
        ["Uninstall.Heading"] = "Uninstall Audio Source Mixer",
        ["Uninstall.Description"] = "The app will restore audio and exit before installation files are removed.",
        ["Uninstall.RemoveData"] = "Also remove user settings and logs (kept by default)",
        ["Uninstall.Action"] = "Uninstall",
        ["Uninstall.Confirm"] = "Uninstall Audio Source Mixer? User settings and logs are kept by default.",
        ["Uninstall.IdentityFailed"] = "The uninstall folder failed product identity and registry validation. Deletion was refused.",
        ["Uninstall.DataPathFailed"] = "User-data path validation failed.",
        ["Uninstall.RecordMissing"] = "The installed product record could not be found.",
        ["Uninstall.LocationMismatch"] = "The uninstaller location does not match the registered installation location.",
        ["Audio.ExitTimeout"] = "Audio Source Mixer did not exit in time after restoring audio. Exit from the tray and retry.",
        ["Browser.ConfigVersion"] = "The browser-extension trust configuration version is unsupported.",
        ["Browser.ConfigIds"] = "The browser-extension trust configuration contains missing or invalid extension IDs.",
        ["Browser.ConfigDevelopmentId"] = "The browser-extension trust configuration is missing the current development extension ID.",
        ["Shortcut.Description"] = "Control Windows audio sessions independently",
        ["Shortcut.ShellMissing"] = "WScript.Shell is unavailable."
    };
}
