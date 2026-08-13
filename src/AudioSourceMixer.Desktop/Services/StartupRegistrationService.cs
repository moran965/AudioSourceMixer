using Microsoft.Win32;
using System.IO;

namespace AudioSourceMixer.Desktop.Services;

public interface IStartupRegistrationService
{
    bool IsAvailable { get; }
    bool IsEnabled { get; }
    void SetEnabled(bool enabled, bool background);
}

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    public const string ValueName = "AudioSourceMixer";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioSourceMixer";
    private readonly string _executablePath;

    public StartupRegistrationService(string? executablePath = null)
    {
        _executablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定应用程序路径。"));
    }

    public bool IsAvailable
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            var installLocation = key?.GetValue("InstallLocation") as string;
            return !string.IsNullOrWhiteSpace(installLocation) &&
                   Path.GetFullPath(installLocation).TrimEnd(Path.DirectorySeparatorChar)
                       .Equals(Path.GetDirectoryName(_executablePath)!.TrimEnd(Path.DirectorySeparatorChar),
                           StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return TryGetOwnedCommand(key?.GetValue(ValueName) as string, out _);
        }
    }

    public void SetEnabled(bool enabled, bool background)
    {
        if (enabled && !IsAvailable)
            throw new InvalidOperationException("便携版不写入开机启动项；请安装后再启用。移动便携目录会使启动路径失效。");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(ValueName, $"\"{_executablePath}\"{(background ? " --background" : string.Empty)}");
        else if (TryGetOwnedCommand(key.GetValue(ValueName) as string, out _))
            key.DeleteValue(ValueName, false);
    }

    private bool TryGetOwnedCommand(string? command, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(command)) return false;
        var trimmed = command.Trim();
        path = trimmed.StartsWith('"')
            ? trimmed[1..].Split('"', 2)[0]
            : trimmed.Split(' ', 2)[0];
        try { return Path.GetFullPath(path).Equals(_executablePath, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
