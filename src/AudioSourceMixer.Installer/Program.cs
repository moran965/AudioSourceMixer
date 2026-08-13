using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AudioSourceMixer.Installer;

internal static class Program
{
    internal const string ProductId = "AudioSourceMixer";
    internal const string HostName = "com.audiosourcemixer.bridge";
    internal const string ExtensionId = "edbfelppckjcfhadggldaifbleoofkio";
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioSourceMixer";
    internal static readonly string DefaultInstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AudioSourceMixer");
    internal static readonly string ProductVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.2.1";
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "AudioSourceMixer-Installer.log");

    [STAThread]
    private static int Main(string[] args)
    {
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        var silent = Has(args, "--silent-install") || Has(args, "--silent-uninstall");
        try
        {
            var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
            var installedUninstaller = executableName.Equals("AudioSourceMixer.Uninstall", StringComparison.OrdinalIgnoreCase);
            if (Has(args, "--silent-uninstall")) return Uninstall(ResolveUninstallDirectory(), removeUserData: Has(args, "--remove-user-data"), prompt: false);
            if (Has(args, "--uninstall") || (installedUninstaller && args.Length == 0))
            {
                if (Has(args, "--uninstall")) return Uninstall(ResolveUninstallDirectory(), removeUserData: false, prompt: true);
                using var uninstallForm = new UninstallerForm();
                return uninstallForm.ShowDialog() == Forms.DialogResult.OK
                    ? Uninstall(ResolveUninstallDirectory(), uninstallForm.RemoveUserData, prompt: false) : 1;
            }

            var existingDirectory = ReadInstallLocation();
            var explicitDirectory = ArgumentValue(args, "--install-dir");
            var target = NormalizeAndValidateInstallPath(explicitDirectory ?? existingDirectory ?? DefaultInstallDirectory);
            if (existingDirectory is not null && !PathEquals(existingDirectory, target))
                throw new InvalidOperationException("升级时不能直接迁移安装目录；请先卸载旧版本（保留设置），再选择新位置安装。");
            var existingStartup = ReadOwnedStartup(existingDirectory, out var existingBackground);
            if (Has(args, "--silent-install"))
            {
                var startupSpecified = Has(args, "--startup") || Has(args, "--startup-background") || Has(args, "--no-startup");
                var startup = startupSpecified ? !Has(args, "--no-startup") : existingDirectory is not null && existingStartup;
                var background = Has(args, "--startup-background") || (!startupSpecified && existingBackground);
                return Install(new InstallOptions(target, Has(args, "--desktop-shortcut"), startup, background,
                    Has(args, "--test-fail-after-backup")), showCompletion: false);
            }

            using var form = new InstallerForm(target, existingDirectory is not null && existingStartup, existingBackground);
            return form.ShowDialog() == Forms.DialogResult.OK ? form.ResultCode : 1;
        }
        catch (Exception exception)
        {
            Log("Operation failed", exception);
            if (!silent) Forms.MessageBox.Show($"操作失败：{exception.Message}", "Audio Source Mixer", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static int Install(InstallOptions options, bool showCompletion)
    {
        var target = NormalizeAndValidateInstallPath(options.InstallDirectory);
        EnsureTargetIsEmptyOrProduct(target, showCompletion);
        RequestGracefulDesktopExit();
        StopInstalledNativeHosts(target);

        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var operationId = Guid.NewGuid().ToString("N");
        var leaf = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar));
        var staging = Path.Combine(parent, $"{leaf}.installing.{operationId}");
        var backup = Path.Combine(parent, $"{leaf}.backup.{operationId}");
        var previousMoved = false;
        var installed = false;
        var committed = false;
        try
        {
            ExtractPayload(staging);
            File.WriteAllText(Path.Combine(staging, "install-identity.json"), JsonSerializer.Serialize(new
            {
                productId = ProductId, version = ProductVersion, installedAt = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定安装程序路径。"),
                Path.Combine(staging, "AudioSourceMixer.Uninstall.exe"), true);
            if (Directory.Exists(target)) { Directory.Move(target, backup); previousMoved = true; }
            if (options.TestFailAfterBackup) throw new IOException("Injected failure after backup for rollback verification.");
            Directory.Move(staging, target); installed = true;

            RegisterNativeHost(target);
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Audio Source Mixer.lnk"), target);
            if (options.DesktopShortcut)
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Audio Source Mixer.lnk"), target);
            else DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Audio Source Mixer.lnk"));
            RegisterUninstaller(target);
            WriteStartup(options.StartWithWindows, options.StartInBackground, target);
            committed = true;
            if (previousMoved) Directory.Delete(backup, true);
            Log($"Install completed. Target={target}; Startup={options.StartWithWindows}; Background={options.StartInBackground}");
            if (showCompletion) Forms.MessageBox.Show($"安装/升级完成。\r\n位置：{target}", "Audio Source Mixer", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
            return 0;
        }
        catch
        {
            if (!committed)
            {
                if (installed && Directory.Exists(target) && IsProductDirectory(target)) Directory.Delete(target, true);
                if (previousMoved && Directory.Exists(backup)) Directory.Move(backup, target);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    internal static string NormalizeAndValidateInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("安装路径不能为空。", nameof(path));
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || full.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能安装到磁盘根目录。");
        var forbidden = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            FindRepositoryRoot(AppContext.BaseDirectory)
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Path.GetFullPath(value!).TrimEnd(Path.DirectorySeparatorChar));
        if (forbidden.Any(value => full.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("所选路径是受保护的系统、用户或仓库根目录。");
        var parent = Path.GetDirectoryName(full) ?? throw new InvalidOperationException("安装路径没有有效父目录。");
        Directory.CreateDirectory(parent);
        var probe = Path.Combine(parent, $".AudioSourceMixer-write-{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(probe, "write-test"); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        return full;
    }

    private static void EnsureTargetIsEmptyOrProduct(string target, bool interactive)
    {
        if (!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any()) return;
        if (IsProductDirectory(target) || PathEquals(ReadInstallLocation(), target)) return;
        if (!interactive || Forms.MessageBox.Show("目标目录包含不属于 Audio Source Mixer 的文件。为保护这些文件，安装不会覆盖该目录。请选择空目录。",
                "Audio Source Mixer", Forms.MessageBoxButtons.OKCancel, Forms.MessageBoxIcon.Warning) == Forms.DialogResult.OK)
            throw new InvalidOperationException("目标目录非空且未通过产品身份验证。");
    }

    private static void ExtractPayload(string staging)
    {
        Directory.CreateDirectory(staging);
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("AudioSourceMixer.Payload.zip")
            ?? throw new InvalidOperationException("安装负载缺失。");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装负载包含不安全路径。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    internal static int Uninstall(string installDirectory, bool removeUserData, bool prompt)
    {
        var target = NormalizeAndValidateInstallPath(installDirectory);
        if (!IsProductDirectory(target) || !PathEquals(ReadInstallLocation(), target))
            throw new InvalidOperationException("卸载目录未通过产品身份和注册表交叉验证，已拒绝删除。");
        if (prompt && Forms.MessageBox.Show("卸载 Audio Source Mixer？默认保留用户设置和日志。", "Audio Source Mixer",
                Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question) != Forms.DialogResult.Yes) return 1;
        RequestGracefulDesktopExit();
        StopInstalledNativeHosts(target);
        UnregisterNativeHost();
        DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Audio Source Mixer.lnk"));
        DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Audio Source Mixer.lnk"));
        RemoveOwnedStartup(target);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
        if (removeUserData)
        {
            var data = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSourceMixer"));
            var expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSourceMixer"));
            if (!data.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("用户数据路径验证失败。");
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
        ScheduleSelfRemoval(target);
        Log($"Uninstall cleanup scheduled. Target={target}; RemoveUserData={removeUserData}");
        return 0;
    }

    private static void RequestGracefulDesktopExit()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting("Local\\AudioSourceMixer.Exit");
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Process.GetProcessesByName("AudioSourceMixer").Any(process => { using (process) return !process.HasExited; }) && DateTime.UtcNow < deadline)
            Thread.Sleep(100);
        if (Process.GetProcessesByName("AudioSourceMixer").Any())
            throw new IOException("Audio Source Mixer 未能在恢复音频后及时退出。请从托盘退出后重试。");
    }

    private static void StopInstalledNativeHosts(string installDirectory)
    {
        var expected = Path.GetFullPath(Path.Combine(installDirectory, "AudioSourceMixer.NativeHost.exe"));
        foreach (var process in Process.GetProcessesByName("AudioSourceMixer.NativeHost"))
        using (process)
        {
            string? path;
            try { path = process.MainModule?.FileName; } catch { continue; }
            if (!PathEquals(path, expected)) continue;
            try { process.Kill(true); process.WaitForExit(5000); } catch (InvalidOperationException) { }
        }
    }

    private static void RegisterNativeHost(string directory)
    {
        var manifestPath = Path.Combine(directory, "native-host-manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { name = HostName,
            description = "Audio Source Mixer browser bridge", path = Path.Combine(directory, "AudioSourceMixer.NativeHost.exe"),
            type = "stdio", allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" } }, new JsonSerializerOptions { WriteIndented = true }));
        SetDefaultValue($@"Software\Google\Chrome\NativeMessagingHosts\{HostName}", manifestPath);
        SetDefaultValue($@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}", manifestPath);
    }

    private static void UnregisterNativeHost()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Google\Chrome\NativeMessagingHosts\{HostName}", false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}", false);
    }

    private static void RegisterUninstaller(string directory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", "Audio Source Mixer"); key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("Publisher", "Audio Source Mixer contributors"); key.SetValue("InstallLocation", directory);
        key.SetValue("DisplayIcon", Path.Combine(directory, "AudioSourceMixer.exe"));
        key.SetValue("UninstallString", $"\"{Path.Combine(directory, "AudioSourceMixer.Uninstall.exe")}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{Path.Combine(directory, "AudioSourceMixer.Uninstall.exe")}\" --silent-uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void WriteStartup(bool enabled, bool background, string directory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled) key.SetValue(ProductId, $"\"{Path.Combine(directory, "AudioSourceMixer.exe")}\"{(background ? " --background" : string.Empty)}");
        else RemoveOwnedStartup(directory, key);
    }

    private static bool ReadOwnedStartup(string? directory, out bool background)
    {
        background = false;
        if (directory is null) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var command = key?.GetValue(ProductId) as string;
        background = command?.Contains("--background", StringComparison.OrdinalIgnoreCase) == true;
        return command?.Contains(Path.Combine(directory, "AudioSourceMixer.exe"), StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void RemoveOwnedStartup(string directory, RegistryKey? suppliedKey = null)
    {
        var ownsKey = suppliedKey is null;
        using var opened = ownsKey ? Registry.CurrentUser.CreateSubKey(RunKeyPath) : null;
        var key = suppliedKey ?? opened!;
        var command = key.GetValue(ProductId) as string;
        if (command?.Contains(Path.Combine(directory, "AudioSourceMixer.exe"), StringComparison.OrdinalIgnoreCase) == true)
            key.DeleteValue(ProductId, false);
    }

    private static string ResolveUninstallDirectory()
    {
        var registry = ReadInstallLocation() ?? throw new InvalidOperationException("找不到已安装产品记录。");
        var self = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "AudioSourceMixer.Uninstall", StringComparison.OrdinalIgnoreCase) && !PathEquals(self, registry))
            throw new InvalidOperationException("卸载程序位置与注册表安装位置不一致。");
        return registry;
    }

    private static string? ReadInstallLocation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("InstallLocation") as string;
    }

    private static bool IsProductDirectory(string directory)
    {
        try
        {
            var identity = Path.Combine(directory, "install-identity.json");
            if (File.Exists(identity)) return JsonDocument.Parse(File.ReadAllText(identity)).RootElement.GetProperty("productId").GetString() == ProductId;
            return PathEquals(ReadInstallLocation(), directory) && File.Exists(Path.Combine(directory, "AudioSourceMixer.exe")) &&
                   File.Exists(Path.Combine(directory, "AudioSourceMixer.Uninstall.exe"));
        }
        catch { return false; }
    }

    private static void ScheduleSelfRemoval(string target)
    {
        var safeTarget = target.Replace("'", "''");
        var safeLog = LogPath.Replace("'", "''");
        var script = $"Start-Sleep -Seconds 2; try {{ Remove-Item -LiteralPath '{safeTarget}' -Recurse -Force -ErrorAction Stop; Add-Content -LiteralPath '{safeLog}' -Value 'Self-removal completed.' }} catch {{ Add-Content -LiteralPath '{safeLog}' -Value ('Self-removal failed: ' + $_) }}";
        Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, ArgumentList = { "-NoProfile", "-WindowStyle", "Hidden", "-Command", script } });
    }

    private static void CreateShortcut(string shortcutPath, string directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell 不可用。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = Path.Combine(directory, "AudioSourceMixer.exe"); shortcut.WorkingDirectory = directory;
        shortcut.IconLocation = Path.Combine(directory, "AudioSourceMixer.exe") + ",0";
        shortcut.Description = "独立控制 Windows 音频会话"; shortcut.Save();
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null) { if (File.Exists(Path.Combine(directory.FullName, "AudioSourceMixer.sln"))) return directory.FullName; directory = directory.Parent; }
        return null;
    }

    private static void SetDefaultValue(string path, string value) { using var key = Registry.CurrentUser.CreateSubKey(path); key.SetValue(null, value); }
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
    private static bool PathEquals(string? left, string? right) { if (left is null || right is null) return false; try { return Path.GetFullPath(left).TrimEnd('\\').Equals(Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    private static bool Has(string[] args, string value) => args.Contains(value, StringComparer.OrdinalIgnoreCase);
    private static string? ArgumentValue(string[] args, string name) { var index = Array.FindIndex(args, item => item.Equals(name, StringComparison.OrdinalIgnoreCase)); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private static void Log(string message, Exception? exception = null) { try { File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}{exception}{Environment.NewLine}"); } catch { } }

    internal sealed record InstallOptions(string InstallDirectory, bool DesktopShortcut, bool StartWithWindows,
        bool StartInBackground, bool TestFailAfterBackup = false);

    private sealed class InstallerForm : Forms.Form
    {
        private readonly Forms.TextBox _path;
        private readonly Forms.CheckBox _desktop = new() { Text = "创建桌面快捷方式", Checked = true, AutoSize = true };
        private readonly Forms.CheckBox _startup = new() { Text = "登录 Windows 后启动 Audio Source Mixer", AutoSize = true };
        private readonly Forms.CheckBox _background = new() { Text = "启动后最小化到系统托盘", AutoSize = true, Checked = true };
        private readonly Forms.ProgressBar _progress = new() { Left = 27, Top = 244, Width = 583, Height = 10, Style = Forms.ProgressBarStyle.Continuous };
        private readonly Forms.Label _status = new() { Text = "准备安装", AutoSize = true, Left = 27, Top = 266 };
        private readonly Forms.Button _install = new() { Text = "安装/升级", Left = 500, Top = 292, Width = 110, Height = 34 };
        private readonly Forms.Button _cancel = new() { Text = "取消", DialogResult = Forms.DialogResult.Cancel, Left = 380, Top = 292, Width = 110, Height = 34 };
        private bool _finished;
        public InstallOptions Options => new(NormalizeAndValidateInstallPath(_path.Text), _desktop.Checked, _startup.Checked, _background.Checked);
        public int ResultCode { get; private set; } = 1;

        public InstallerForm(string initialPath, bool startup, bool background)
        {
            Text = "安装 Audio Source Mixer"; Width = 650; Height = 390; StartPosition = Forms.FormStartPosition.CenterScreen;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? throw new InvalidOperationException("无法读取安装器图标。");
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            Controls.Add(new Forms.Label { Text = "Audio Source Mixer", Font = new System.Drawing.Font("Segoe UI", 18, System.Drawing.FontStyle.Bold), AutoSize = true, Left = 24, Top = 20 });
            Controls.Add(new Forms.Label { Text = "安装位置", AutoSize = true, Left = 27, Top = 75 });
            _path = new Forms.TextBox { Text = initialPath, Left = 27, Top = 98, Width = 485 };
            var browse = new Forms.Button { Text = "浏览…", Left = 520, Top = 96, Width = 90 };
            browse.Click += (_, _) => { using var dialog = new Forms.FolderBrowserDialog { SelectedPath = _path.Text, Description = "选择 Audio Source Mixer 安装目录" }; if (dialog.ShowDialog() == Forms.DialogResult.OK) _path.Text = dialog.SelectedPath; };
            _desktop.SetBounds(27, 145, 400, 24); _startup.SetBounds(27, 178, 400, 24); _startup.Checked = startup;
            _background.SetBounds(52, 209, 400, 24); _background.Checked = background; _background.Enabled = _startup.Checked;
            _startup.CheckedChanged += (_, _) => _background.Enabled = _startup.Checked;
            _install.Click += InstallClicked;
            Controls.AddRange([_path, browse, _desktop, _startup, _background, _progress, _status, _install, _cancel]);
            AcceptButton = _install; CancelButton = _cancel;
        }

        private async void InstallClicked(object? sender, EventArgs eventArgs)
        {
            if (_finished) { DialogResult = Forms.DialogResult.OK; Close(); return; }
            InstallOptions options;
            try { options = Options; }
            catch (Exception exception) { Forms.MessageBox.Show(exception.Message, "Audio Source Mixer", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning); return; }
            foreach (Forms.Control control in Controls) control.Enabled = false;
            _progress.Enabled = true; _status.Enabled = true;
            _progress.Style = Forms.ProgressBarStyle.Marquee;
            _status.Text = "正在安全展开并提交安装文件…";
            try
            {
                ResultCode = await Task.Run(() => Install(options, showCompletion: false));
                _status.Text = $"安装/升级完成：{options.InstallDirectory}";
                _progress.Style = Forms.ProgressBarStyle.Continuous; _progress.Value = 100;
            }
            catch (Exception exception)
            {
                ResultCode = 1; Log("Interactive install failed", exception);
                _status.Text = $"安装失败：{exception.Message}";
                _progress.Style = Forms.ProgressBarStyle.Continuous; _progress.Value = 0;
            }
            _finished = true;
            _install.Text = "关闭"; _install.Enabled = true;
            AcceptButton = _install;
        }
    }

    private sealed class UninstallerForm : Forms.Form
    {
        private readonly Forms.CheckBox _removeData = new() { Text = "同时删除用户设置和日志（默认保留）", Checked = false, AutoSize = true };
        public bool RemoveUserData => _removeData.Checked;
        public UninstallerForm()
        {
            Text = "卸载 Audio Source Mixer"; Width = 520; Height = 240; StartPosition = Forms.FormStartPosition.CenterScreen;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? throw new InvalidOperationException("无法读取卸载器图标。");
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            Controls.Add(new Forms.Label { Text = "卸载 Audio Source Mixer", Font = new System.Drawing.Font("Segoe UI", 16, System.Drawing.FontStyle.Bold), AutoSize = true, Left = 24, Top = 24 });
            Controls.Add(new Forms.Label { Text = "程序会先恢复音频并退出，然后删除安装文件。", AutoSize = true, Left = 27, Top = 70 });
            _removeData.SetBounds(27, 105, 430, 24);
            var uninstall = new Forms.Button { Text = "卸载", DialogResult = Forms.DialogResult.OK, Left = 380, Top = 145, Width = 100, Height = 34 };
            var cancel = new Forms.Button { Text = "取消", DialogResult = Forms.DialogResult.Cancel, Left = 270, Top = 145, Width = 100, Height = 34 };
            Controls.AddRange([_removeData, uninstall, cancel]); AcceptButton = uninstall; CancelButton = cancel;
        }
    }
}
