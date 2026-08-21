using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AudioSourceMixer.Installer;

internal static class Program
{
    internal const string ProductId = "AudioSourceMixer";
    internal const string HostName = "com.audiosourcemixer.bridge";
    internal const string DevelopmentExtensionId = "edbfelppckjcfhadggldaifbleoofkio";
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioSourceMixer";
    internal static readonly string DefaultInstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AudioSourceMixer");
    internal static readonly string ProductVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.2.2";
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "AudioSourceMixer-Installer.log");

    [STAThread]
    private static int Main(string[] args)
    {
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        var silent = Has(args, "--silent-install") || Has(args, "--silent-uninstall");
        try
        {
            var requestedLanguage = ArgumentValue(args, "--language");
            if (requestedLanguage is not null) InstallerLocalization.SetLanguage(requestedLanguage);
            var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
            var installedUninstaller = executableName.Equals("AudioSourceMixer.Uninstall", StringComparison.OrdinalIgnoreCase);
            if (Has(args, "--silent-uninstall"))
            {
                if (requestedLanguage is null) InstallerLocalization.SetLanguage(ReadInstalledLanguage());
                return Uninstall(ResolveUninstallDirectory(), removeUserData: Has(args, "--remove-user-data"), prompt: false);
            }
            if (Has(args, "--uninstall") || (installedUninstaller && args.Length == 0))
            {
                if (requestedLanguage is null) InstallerLocalization.SetLanguage(ReadInstalledLanguage());
                using var uninstallForm = new UninstallerForm(InstallerLocalization.CurrentLanguage);
                return uninstallForm.ShowDialog() == Forms.DialogResult.OK
                    ? Uninstall(ResolveUninstallDirectory(), uninstallForm.RemoveUserData, prompt: false) : 1;
            }

            if (!silent && requestedLanguage is null)
            {
                using var languageForm = new LanguageSelectionForm();
                if (languageForm.ShowDialog() != Forms.DialogResult.OK) return 1;
                InstallerLocalization.SetLanguage(languageForm.SelectedLanguage);
            }
            else if (requestedLanguage is null)
            {
                InstallerLocalization.SetLanguage(InstallerLocalization.SystemLanguage());
            }

            var existingDirectory = ReadInstallLocation();
            var explicitDirectory = ArgumentValue(args, "--install-dir");
            var target = NormalizeAndValidateInstallPath(explicitDirectory ?? existingDirectory ?? DefaultInstallDirectory);
            if (existingDirectory is not null && !PathEquals(existingDirectory, target))
                throw new InvalidOperationException(L("Install.MoveBlocked"));
            var existingStartup = ReadOwnedStartup(existingDirectory, out var existingBackground);
            if (Has(args, "--silent-install"))
            {
                var startupSpecified = Has(args, "--startup") || Has(args, "--startup-background") || Has(args, "--no-startup");
                var startup = startupSpecified ? !Has(args, "--no-startup") : existingDirectory is not null && existingStartup;
                var background = Has(args, "--startup-background") || (!startupSpecified && existingBackground);
                return Install(new InstallOptions(target, Has(args, "--desktop-shortcut"), startup, background,
                    Has(args, "--test-fail-after-backup"), Has(args, "--browser-setup"), InstallerLocalization.CurrentLanguage), showCompletion: false);
            }

            using var form = new InstallerForm(target, existingDirectory is not null && existingStartup, existingBackground,
                InstallerLocalization.CurrentLanguage);
            return form.ShowDialog() == Forms.DialogResult.OK ? form.ResultCode : 1;
        }
        catch (Exception exception)
        {
            Log("Operation failed", exception);
            if (!silent) Forms.MessageBox.Show(InstallerLocalization.Format("Install.OperationFailed", exception.Message),
                L("Common.ProductName"), Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static int Install(InstallOptions options, bool showCompletion)
    {
        InstallerLocalization.SetLanguage(options.Language);
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
            File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException(L("Install.SetupPathUnknown")),
                Path.Combine(staging, "AudioSourceMixer.Uninstall.exe"), true);
            if (Directory.Exists(target)) { Directory.Move(target, backup); previousMoved = true; }
            if (options.TestFailAfterBackup) throw new IOException("Injected failure after backup for rollback verification.");
            Directory.Move(staging, target); installed = true;

            RegisterNativeHost(target);
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Audio Source Mixer.lnk"), target);
            if (options.DesktopShortcut)
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Audio Source Mixer.lnk"), target);
            else DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Audio Source Mixer.lnk"));
            RegisterUninstaller(target, options.Language);
            WriteStartup(options.StartWithWindows, options.StartInBackground, target);
            WriteInitialLanguageBootstrap(options.Language);
            committed = true;
            if (previousMoved) Directory.Delete(backup, true);
            Log($"Install completed. Target={target}; Startup={options.StartWithWindows}; Background={options.StartInBackground}");
            if (showCompletion) Forms.MessageBox.Show(InstallerLocalization.Format("Install.CompletedDialog", target),
                L("Common.ProductName"), Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
            if (options.BrowserSetup) LaunchBrowserSetup(target, options.Language);
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
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(L("Install.PathEmpty"), nameof(path));
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || full.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L("Install.DriveRoot"));
        var forbidden = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            FindRepositoryRoot(AppContext.BaseDirectory)
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Path.GetFullPath(value!).TrimEnd(Path.DirectorySeparatorChar));
        if (forbidden.Any(value => full.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L("Install.ProtectedRoot"));
        var parent = Path.GetDirectoryName(full) ?? throw new InvalidOperationException(L("Install.ParentMissing"));
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
        if (!interactive || Forms.MessageBox.Show(L("Install.TargetWarning"),
                L("Common.ProductName"), Forms.MessageBoxButtons.OKCancel, Forms.MessageBoxIcon.Warning) == Forms.DialogResult.OK)
            throw new InvalidOperationException(L("Install.TargetRejected"));
    }

    private static void ExtractPayload(string staging)
    {
        Directory.CreateDirectory(staging);
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("AudioSourceMixer.Payload.zip")
            ?? throw new InvalidOperationException(L("Install.PayloadMissing"));
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L("Install.PayloadUnsafe"));
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    internal static int Uninstall(string installDirectory, bool removeUserData, bool prompt)
    {
        var target = NormalizeAndValidateInstallPath(installDirectory);
        if (!IsProductDirectory(target) || !PathEquals(ReadInstallLocation(), target))
            throw new InvalidOperationException(L("Uninstall.IdentityFailed"));
        if (prompt && Forms.MessageBox.Show(L("Uninstall.Confirm"), L("Common.ProductName"),
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
            if (!data.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L("Uninstall.DataPathFailed"));
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
            throw new IOException(L("Audio.ExitTimeout"));
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
        var allowedOrigins = LoadTrustedExtensionOrigins(Path.Combine(directory, "browser-extension-origins.json"));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { name = HostName,
            description = "Audio Source Mixer browser bridge", path = Path.Combine(directory, "AudioSourceMixer.NativeHost.exe"),
            type = "stdio", allowed_origins = allowedOrigins }, new JsonSerializerOptions { WriteIndented = true }));
        SetDefaultValue($@"Software\Google\Chrome\NativeMessagingHosts\{HostName}", manifestPath);
        SetDefaultValue($@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}", manifestPath);
    }

    internal static string[] LoadTrustedExtensionOrigins(string configurationPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configurationPath));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException(L("Browser.ConfigVersion"));
        var ids = new[] { "developmentExtensionId", "chromeStoreExtensionId", "edgeStoreExtensionId" }
            .Select(name => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0 || ids.Any(id => !Regex.IsMatch(id, "^[a-p]{32}$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException(L("Browser.ConfigIds"));
        if (!ids.Contains(DevelopmentExtensionId, StringComparer.Ordinal))
            throw new InvalidDataException(L("Browser.ConfigDevelopmentId"));
        return ids.Select(id => $"chrome-extension://{id}/").ToArray();
    }

    private static void LaunchBrowserSetup(string directory, string language)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(directory, "AudioSourceMixer.exe"), $"--browser-setup --language {language}")
        {
            UseShellExecute = true
        });
        Log("Browser enhancement setup was explicitly requested after installation.");
    }

    private static void UnregisterNativeHost()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Google\Chrome\NativeMessagingHosts\{HostName}", false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}", false);
    }

    private static void RegisterUninstaller(string directory, string language)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", "Audio Source Mixer"); key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("Publisher", "Audio Source Mixer contributors"); key.SetValue("InstallLocation", directory);
        key.SetValue("DisplayIcon", Path.Combine(directory, "AudioSourceMixer.exe"));
        key.SetValue("UninstallString", $"\"{Path.Combine(directory, "AudioSourceMixer.Uninstall.exe")}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{Path.Combine(directory, "AudioSourceMixer.Uninstall.exe")}\" --silent-uninstall --language {language}");
        key.SetValue("Language", language);
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
        var registry = ReadInstallLocation() ?? throw new InvalidOperationException(L("Uninstall.RecordMissing"));
        var self = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "AudioSourceMixer.Uninstall", StringComparison.OrdinalIgnoreCase) && !PathEquals(self, registry))
            throw new InvalidOperationException(L("Uninstall.LocationMismatch"));
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
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(L("Shortcut.ShellMissing"));
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = Path.Combine(directory, "AudioSourceMixer.exe"); shortcut.WorkingDirectory = directory;
        shortcut.IconLocation = Path.Combine(directory, "AudioSourceMixer.exe") + ",0";
        shortcut.Description = L("Shortcut.Description"); shortcut.Save();
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

    private static string L(string key) => InstallerLocalization.Text(key);

    private static string ReadInstalledLanguage()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        var language = key?.GetValue("Language") as string;
        return InstallerLocalization.SupportedLanguages.Contains(language ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            ? InstallerLocalization.Normalize(language) : InstallerLocalization.SystemLanguage();
    }

    private static void WriteInitialLanguageBootstrap(string language)
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSourceMixer");
        var settingsPath = Path.Combine(dataDirectory, "settings.json");
        if (File.Exists(settingsPath)) return;
        Directory.CreateDirectory(dataDirectory);
        var destination = Path.Combine(dataDirectory, "initial-language.json");
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { language = InstallerLocalization.Normalize(language) }));
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal sealed record InstallOptions(string InstallDirectory, bool DesktopShortcut, bool StartWithWindows,
        bool StartInBackground, bool TestFailAfterBackup = false, bool BrowserSetup = false, string Language = "zh-CN");

    private static System.Drawing.Font CreateUiFont(string language, float size = 9F, System.Drawing.FontStyle style = System.Drawing.FontStyle.Regular)
        => new(language == InstallerLocalization.Chinese ? "Microsoft YaHei UI" : "Segoe UI", size, style);

    private sealed class LanguageSelectionForm : Forms.Form
    {
        private readonly Forms.ComboBox _language = new()
        {
            DropDownStyle = Forms.ComboBoxStyle.DropDownList,
            Left = 27,
            Top = 112,
            Width = 430
        };

        public string SelectedLanguage => _language.SelectedIndex == 1 ? InstallerLocalization.English : InstallerLocalization.Chinese;

        public LanguageSelectionForm()
        {
            Text = InstallerLocalization.Text("Language.Title");
            Width = 520; Height = 250; StartPosition = Forms.FormStartPosition.CenterScreen;
            Font = CreateUiFont(InstallerLocalization.SystemLanguage());
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                   ?? throw new InvalidOperationException(InstallerLocalization.Text("Install.IconMissing"));
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var heading = new Forms.Label { Text = InstallerLocalization.Text("Language.Heading"), Font = CreateUiFont(InstallerLocalization.SystemLanguage(), 15, System.Drawing.FontStyle.Bold), AutoSize = true, Left = 24, Top = 20 };
            var description = new Forms.Label { Text = InstallerLocalization.Text("Language.Description"), AutoSize = false, Left = 27, Top = 58, Width = 430, Height = 48 };
            _language.Items.AddRange([InstallerLocalization.Text("Language.Chinese"), InstallerLocalization.Text("Language.English")]);
            _language.SelectedIndex = InstallerLocalization.SystemLanguage() == InstallerLocalization.Chinese ? 0 : 1;
            var proceed = new Forms.Button { Text = InstallerLocalization.Text("Language.Continue"), DialogResult = Forms.DialogResult.OK, Left = 327, Top = 157, Width = 130, Height = 34 };
            var cancel = new Forms.Button { Text = InstallerLocalization.Text("Common.Cancel"), DialogResult = Forms.DialogResult.Cancel, Left = 217, Top = 157, Width = 100, Height = 34 };
            Controls.AddRange([heading, description, _language, proceed, cancel]);
            AcceptButton = proceed; CancelButton = cancel;
        }
    }

    private sealed class InstallerForm : Forms.Form
    {
        private readonly Forms.TextBox _path;
        private readonly Forms.CheckBox _desktop = new() { Checked = true, AutoSize = true };
        private readonly Forms.CheckBox _startup = new() { AutoSize = true };
        private readonly Forms.CheckBox _background = new() { AutoSize = true, Checked = true };
        private readonly Forms.CheckBox _browserSetup = new() { AutoSize = true };
        private readonly Forms.ProgressBar _progress = new() { Left = 27, Top = 337, Width = 643, Height = 10, Style = Forms.ProgressBarStyle.Continuous };
        private readonly Forms.Label _status = new() { AutoSize = true, Left = 27, Top = 359 };
        private readonly Forms.Button _install = new() { Left = 540, Top = 395, Width = 130, Height = 34 };
        private readonly Forms.Button _cancel = new() { DialogResult = Forms.DialogResult.Cancel, Left = 420, Top = 395, Width = 110, Height = 34 };
        private readonly string _language;
        private bool _finished;
        public InstallOptions Options => new(NormalizeAndValidateInstallPath(_path.Text), _desktop.Checked, _startup.Checked,
            _background.Checked, BrowserSetup: _browserSetup.Checked, Language: _language);
        public int ResultCode { get; private set; } = 1;

        public InstallerForm(string initialPath, bool startup, bool background, string language)
        {
            _language = InstallerLocalization.Normalize(language);
            Text = L("Install.Title"); Width = 720; Height = 505; StartPosition = Forms.FormStartPosition.CenterScreen;
            Font = CreateUiFont(_language);
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? throw new InvalidOperationException(L("Install.IconMissing"));
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            _desktop.Text = L("Install.DesktopShortcut"); _startup.Text = L("Install.StartWithWindows");
            _background.Text = L("Install.StartInTray"); _browserSetup.Text = L("Install.BrowserSetup");
            _status.Text = L("Install.Ready"); _install.Text = L("Install.Action"); _cancel.Text = L("Common.Cancel");
            Controls.Add(new Forms.Label { Text = L("Common.ProductName"), Font = CreateUiFont(_language, 18, System.Drawing.FontStyle.Bold), AutoSize = true, Left = 24, Top = 20 });
            Controls.Add(new Forms.Label { Text = L("Install.Location"), AutoSize = true, Left = 27, Top = 75 });
            _path = new Forms.TextBox { Text = initialPath, Left = 27, Top = 98, Width = 545 };
            var browse = new Forms.Button { Text = L("Install.Browse"), Left = 580, Top = 96, Width = 90 };
            browse.Click += (_, _) => { using var dialog = new Forms.FolderBrowserDialog { SelectedPath = _path.Text, Description = L("Install.BrowseDescription") }; if (dialog.ShowDialog() == Forms.DialogResult.OK) _path.Text = dialog.SelectedPath; };
            _desktop.SetBounds(27, 145, 640, 24); _startup.SetBounds(27, 178, 640, 24); _startup.Checked = startup;
            _background.SetBounds(52, 209, 615, 24); _background.Checked = background; _background.Enabled = _startup.Checked;
            _browserSetup.SetBounds(27, 244, 640, 24); _browserSetup.Checked = false;
            var browserExplanation = new Forms.Label
            {
                Text = L("Install.BrowserExplanation"),
                AutoSize = false, Left = 52, Top = 271, Width = 615, Height = 56
            };
            _startup.CheckedChanged += (_, _) => _background.Enabled = _startup.Checked;
            _install.Click += InstallClicked;
            Controls.AddRange([_path, browse, _desktop, _startup, _background, _browserSetup, browserExplanation,
                _progress, _status, _install, _cancel]);
            AcceptButton = _install; CancelButton = _cancel;
        }

        private async void InstallClicked(object? sender, EventArgs eventArgs)
        {
            if (_finished) { DialogResult = Forms.DialogResult.OK; Close(); return; }
            InstallOptions options;
            try { options = Options; }
            catch (Exception exception) { Forms.MessageBox.Show(exception.Message, L("Common.ProductName"), Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning); return; }
            foreach (Forms.Control control in Controls) control.Enabled = false;
            _progress.Enabled = true; _status.Enabled = true;
            _progress.Style = Forms.ProgressBarStyle.Marquee;
            _status.Text = L("Install.Working");
            try
            {
                ResultCode = await Task.Run(() => Install(options, showCompletion: false));
                _status.Text = InstallerLocalization.Format("Install.Completed", options.InstallDirectory);
                _progress.Style = Forms.ProgressBarStyle.Continuous; _progress.Value = 100;
            }
            catch (Exception exception)
            {
                ResultCode = 1; Log("Interactive install failed", exception);
                _status.Text = InstallerLocalization.Format("Install.Failed", exception.Message);
                _progress.Style = Forms.ProgressBarStyle.Continuous; _progress.Value = 0;
            }
            _finished = true;
            _install.Text = L("Common.Close"); _install.Enabled = true;
            AcceptButton = _install;
        }
    }

    private sealed class UninstallerForm : Forms.Form
    {
        private readonly Forms.CheckBox _removeData = new() { Checked = false, AutoSize = true };
        private readonly Forms.Label _heading = new() { AutoSize = true, Left = 24, Top = 24 };
        private readonly Forms.Label _description = new() { AutoSize = false, Left = 27, Top = 70, Width = 470, Height = 42 };
        private readonly Forms.Label _languageLabel = new() { AutoSize = true, Left = 27, Top = 116 };
        private readonly Forms.ComboBox _language = new() { DropDownStyle = Forms.ComboBoxStyle.DropDownList, Left = 27, Top = 138, Width = 180 };
        private readonly Forms.Button _uninstall = new() { DialogResult = Forms.DialogResult.OK, Left = 400, Top = 195, Width = 100, Height = 34 };
        private readonly Forms.Button _cancel = new() { DialogResult = Forms.DialogResult.Cancel, Left = 290, Top = 195, Width = 100, Height = 34 };
        public bool RemoveUserData => _removeData.Checked;
        public UninstallerForm(string language)
        {
            InstallerLocalization.SetLanguage(language);
            Text = L("Uninstall.Title"); Width = 550; Height = 290; StartPosition = Forms.FormStartPosition.CenterScreen;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? throw new InvalidOperationException(L("Install.IconMissing"));
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            _language.Items.AddRange([InstallerLocalization.Get("Language.Chinese", InstallerLocalization.Chinese),
                InstallerLocalization.Get("Language.English", InstallerLocalization.English)]);
            _language.SelectedIndex = InstallerLocalization.CurrentLanguage == InstallerLocalization.Chinese ? 0 : 1;
            _language.SelectedIndexChanged += (_, _) =>
            {
                InstallerLocalization.SetLanguage(_language.SelectedIndex == 1 ? InstallerLocalization.English : InstallerLocalization.Chinese);
                ApplyLanguage();
            };
            _removeData.SetBounds(27, 171, 470, 24);
            Controls.AddRange([_heading, _description, _languageLabel, _language, _removeData, _uninstall, _cancel]);
            ApplyLanguage();
            AcceptButton = _uninstall; CancelButton = _cancel;
        }

        private void ApplyLanguage()
        {
            Font = CreateUiFont(InstallerLocalization.CurrentLanguage);
            Text = L("Uninstall.Title");
            _heading.Text = L("Uninstall.Heading");
            _heading.Font = CreateUiFont(InstallerLocalization.CurrentLanguage, 16, System.Drawing.FontStyle.Bold);
            _description.Text = L("Uninstall.Description");
            _languageLabel.Text = L("Language.Label");
            _removeData.Text = L("Uninstall.RemoveData");
            _uninstall.Text = L("Uninstall.Action");
            _cancel.Text = L("Common.Cancel");
        }
    }
}
