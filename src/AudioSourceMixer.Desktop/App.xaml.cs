using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AudioSourceMixer.Core.Browser;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Persistence;
using AudioSourceMixer.Desktop.Diagnostics;
using AudioSourceMixer.Desktop.Localization;
using AudioSourceMixer.Desktop.ViewModels;
using AudioSourceMixer.WindowsAudio;
using Forms = System.Windows.Forms;

namespace AudioSourceMixer.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private Icon? _productIcon;
    private Font? _trayMenuFont;
    private WindowsAudioService? _audio;
    private BrowserBridgeServer? _bridge;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private RollingFileLogger? _logger;
    private UiSmokeMonitor? _uiSmokeMonitor;
    private StartupStage _startupStage;
    private bool _uiSmokeTest;
    private bool _uiInteractionTest;
    private int _fatalReported;
    private int _exiting;
    private int _exitCode;
    private bool _background;
    private bool _browserSetup;
    private string? _uiScreenshotDirectory;
    private uint? _liveMeterProcessId;
    private string? _liveMeterReportPath;
    private int _liveMeterDurationSeconds = 8;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitSignalRegistration;
    private string _exitSignalName = "Local\\AudioSourceMixer.Exit";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        ApplyAccessibilityColors();
        if (uint.TryParse(ArgumentValue(e.Args, "--ui-live-meter-pid"), out var liveMeterProcessId))
            _liveMeterProcessId = liveMeterProcessId;
        _liveMeterReportPath = ArgumentValue(e.Args, "--ui-live-meter-report");
        if (int.TryParse(ArgumentValue(e.Args, "--ui-live-meter-duration"), out var liveMeterDurationSeconds))
            _liveMeterDurationSeconds = Math.Clamp(liveMeterDurationSeconds, 3, 30);
        _uiInteractionTest = e.Args.Contains("--ui-interaction-test", StringComparer.OrdinalIgnoreCase);
        _uiSmokeTest = e.Args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase) ||
                       e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase) ||
                       _liveMeterProcessId.HasValue || _uiInteractionTest;
        _uiScreenshotDirectory = ArgumentValue(e.Args, "--ui-screenshot-dir");
        var diagnosticUi = _uiSmokeTest || _uiScreenshotDirectory is not null;
        _background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase) && !diagnosticUi;
        _browserSetup = e.Args.Contains("--browser-setup", StringComparer.OrdinalIgnoreCase) && !diagnosticUi;
        var dataDirectory = diagnosticUi
            ? Path.Combine(Path.GetTempPath(), "AudioSourceMixer", "ui-smoke", Environment.ProcessId.ToString())
            : AppPaths.LocalDataDirectory;

        try
        {
            _startupStage = StartupStage.Logging;
            InitializeLogger(Path.Combine(dataDirectory, "logs"));
            AttachExceptionHandlers();
            _logger!.Info($"Application startup began. Version={GetType().Assembly.GetName().Version}; OS={Environment.OSVersion.VersionString}; UiSmoke={_uiSmokeTest}.");
            if (diagnosticUi) _uiSmokeMonitor = new UiSmokeMonitor();

            _startupStage = StartupStage.SingleInstance;
            var mutexName = diagnosticUi
                ? $"Local\\AudioSourceMixer.Desktop.UiSmoke.{Environment.ProcessId}"
                : "Local\\AudioSourceMixer.Desktop";
            _singleInstance = new Mutex(true, mutexName, out _ownsMutex);
            if (!_ownsMutex)
            {
                if (!diagnosticUi)
                    System.Windows.MessageBox.Show(LocalizationService.Current["App.AlreadyRunning"], LocalizationService.Current["Common.ProductName"], MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
            if (diagnosticUi) _exitSignalName = $"Local\\AudioSourceMixer.Exit.{Environment.ProcessId}";
            RegisterGracefulExitSignal();

            var profileStore = new JsonAudioProfileStore(dataDirectory);
            var settingsStore = new JsonApplicationSettingsStore(dataDirectory);
            _audio = new WindowsAudioService(new JsonRollbackJournal(dataDirectory), _logger);

            _startupStage = StartupStage.BrowserBridge;
            _bridge = new BrowserBridgeServer(logger: _logger);
            _bridge.Start();

            _startupStage = StartupStage.ViewModel;
            _viewModel = new MainViewModel(_audio, _audio, _audio, _bridge, profileStore, settingsStore, _logger);
            await _viewModel.LoadSettingsAsync();
            var requestedLanguage = ArgumentValue(e.Args, "--language");
            if (requestedLanguage is not null)
            {
                if (!LocalizationService.SupportedLanguages.Contains(requestedLanguage, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"Unsupported --language value '{requestedLanguage}'. Use zh-CN or en-US.");
                _viewModel.SelectedLanguage = LocalizationService.NormalizeLanguage(requestedLanguage);
                _logger.Info($"UI language selected by command line: {_viewModel.SelectedLanguage}.");
            }
            if (_browserSetup)
            {
                _viewModel.RequestBrowserSetup();
                _logger.Info("Browser setup page requested by command line.");
            }

            _startupStage = StartupStage.WindowCreation;
            _window = new MainWindow(_viewModel);
            if (diagnosticUi && !_uiInteractionTest) ConfigureUiSmokeWindow(_window);

            _startupStage = StartupStage.TrayCreation;
            CreateTray(visible: !diagnosticUi);

            _startupStage = StartupStage.AudioInitialization;
            var diagnosticSources = diagnosticUi && !_liveMeterProcessId.HasValue
                ? UiSmokeVerifier.CreateDiagnosticSources() : null;
            await _viewModel.InitializeAsync(diagnosticSources);
            if (_liveMeterProcessId.HasValue) _viewModel.SelectMixerForDiagnostics();

            _startupStage = StartupStage.WindowDisplay;
            if (_background)
            {
                _startupStage = StartupStage.Complete;
                _logger.Info($"Application startup completed successfully in background tray mode. Sources={_viewModel.Sources.Count}.");
                return;
            }
            var loaded = WaitForLoadedAsync(_window);
            _window.Show();
            await loaded;
            await Dispatcher.InvokeAsync(() => _window.UpdateLayout(), DispatcherPriority.ApplicationIdle);

            if (diagnosticUi)
            {
                if (_uiInteractionTest)
                {
                    _startupStage = StartupStage.Complete;
                    _logger.Info($"Interactive WPF diagnostic ready. WindowShown={_window.IsVisible}; Sources={_viewModel.Sources.Count}.");
                    return;
                }
                if (_liveMeterProcessId is { } processId)
                {
                    var report = await LiveMeterVerifier.VerifyAsync(_window, _viewModel, _audio, processId,
                        _liveMeterDurationSeconds);
                    var reportPath = _liveMeterReportPath ?? Path.Combine(dataDirectory, "live-meter-report.json");
                    await LiveMeterVerifier.WriteAsync(reportPath, report);
                    await FlushAsynchronousFailuresAsync();
                    _uiSmokeMonitor!.ThrowIfFailed();
                    _startupStage = StartupStage.Complete;
                    _logger.Info($"Live WPF meter diagnostic succeeded. PID={processId}; Samples={report.SampleCount}; " +
                                 $"MaxRaw={report.MaximumRawPeak:F4}; MaxSmoothed={report.MaximumSmoothedPeak:F4}; " +
                                 $"MaxIndicatorWidth={report.MaximumIndicatorWidth:F2}; ReturnedToZero={report.ReturnedToZero}; Report={reportPath}.");
                    await ExitAndRestoreAsync(0);
                    return;
                }
                var source = _viewModel.Sources.Single(item => item.Id == diagnosticSources![0].Id);
                var result = await UiSmokeVerifier.VerifyAsync(_window, source);
                IReadOnlyList<string> screenshots = [];
                if (_uiScreenshotDirectory is not null)
                    screenshots = await UiScreenshotCapture.CaptureAsync(_window, _viewModel, _uiScreenshotDirectory);
                await FlushAsynchronousFailuresAsync();
                _uiSmokeMonitor!.ThrowIfFailed();
                _startupStage = StartupStage.Complete;
                _logger.Info($"UI diagnostic succeeded. WindowShown={_window.IsVisible}; Items={result.ItemCount}; Container={result.ContainerType}; Peak={result.PeakValue:F1}; TrackWidth={result.PeakTrackWidth:F2}; IndicatorWidth={result.PeakIndicatorWidth:F2}; AuditedBindings={result.Bindings.Count}; Screenshots={screenshots.Count}.");
                await ExitAndRestoreAsync(0);
                return;
            }

            var materializedItems = await WaitForNormalUiMaterializationAsync(_window, _viewModel);
            _startupStage = StartupStage.Complete;
            _logger.Info($"Application startup completed successfully. WindowShown={_window.IsVisible}; Sources={_viewModel.Sources.Count}; MaterializedItems={materializedItems}.");
        }
        catch (Exception exception)
        {
            _uiSmokeMonitor?.Record($"Startup/{_startupStage}", exception);
            await HandleStartupFailureAsync(exception);
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void InitializeLogger(string preferredDirectory)
    {
        try
        {
            _logger = new RollingFileLogger(preferredDirectory);
            _logger.Info("Logging initialized.");
        }
        catch (Exception primaryException)
        {
            var fallback = Path.Combine(Path.GetTempPath(), "AudioSourceMixer", "fallback-logs");
            _logger = new RollingFileLogger(fallback);
            _logger.Error($"Could not initialize the preferred log directory '{preferredDirectory}'.", primaryException);
        }
    }

    private void AttachExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SessionEnding += OnSessionEnding;
    }

    private void DetachExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        SessionEnding -= OnSessionEnding;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        _uiSmokeMonitor?.Record("DispatcherUnhandledException", args.Exception);
        QueueFatalExit("Unhandled UI exception.", args.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception ?? new InvalidOperationException(args.ExceptionObject?.ToString() ?? "Unknown AppDomain exception.");
        _uiSmokeMonitor?.Record("AppDomain.UnhandledException", exception);
        QueueFatalExit("Unhandled AppDomain exception.", exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();
        _uiSmokeMonitor?.Record("TaskScheduler.UnobservedTaskException", args.Exception);
        QueueFatalExit("Unobserved asynchronous exception.", args.Exception);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs args)
        => ExitAndRestoreAsync().GetAwaiter().GetResult();

    private void QueueFatalExit(string message, Exception exception)
    {
        if (Interlocked.Exchange(ref _fatalReported, 1) != 0) return;
        TryLogError(message, exception);
        try
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (!_uiSmokeTest) ShowFailureMessage(LocalizationService.Current["App.UnhandledError"]);
                await ExitAndRestoreAsync(1);
            }, DispatcherPriority.Send);
        }
        catch (Exception dispatchException) { TryLogError("Could not dispatch fatal shutdown.", dispatchException); }
    }

    private async Task HandleStartupFailureAsync(Exception exception)
    {
        if (Interlocked.Exchange(ref _fatalReported, 1) == 0)
        {
            TryLogError($"Application startup failed during {_startupStage}.", exception);
            if (!_uiSmokeTest) ShowFailureMessage(GetStartupFailureMessage(_startupStage, exception));
        }
        await ExitAndRestoreAsync(1);
    }

    private static string GetStartupFailureMessage(StartupStage stage, Exception exception)
    {
        var localization = LocalizationService.Current;
        var prefix = stage == StartupStage.AudioInitialization
            ? localization["App.AudioInitializationFailed"]
            : stage is StartupStage.WindowCreation or StartupStage.WindowDisplay
                ? localization["App.UiInitializationFailed"]
                : localization["App.StartupFailed"];
        return localization.Format("App.FailureFormat", prefix, exception.Message, localization["App.DetailsLogged"]);
    }

    private void ShowFailureMessage(string message)
    {
        try { System.Windows.MessageBox.Show(message, LocalizationService.Current["Common.ProductName"], MessageBoxButton.OK, MessageBoxImage.Error); }
        catch (Exception exception) { TryLogError("Could not display the fatal error dialog.", exception); }
    }

    private static Task WaitForLoadedAsync(Window window)
    {
        if (window.IsLoaded) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += Loaded;
        return completion.Task;

        void Loaded(object sender, RoutedEventArgs args)
        {
            window.Loaded -= Loaded;
            completion.TrySetResult();
        }
    }

    private static void ConfigureUiSmokeWindow(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
    }

    private static int CountMaterializedItems(MainWindow window)
    {
        window.SourceItems.UpdateLayout();
        var count = 0;
        for (var index = 0; index < window.SourceItems.Items.Count; index++)
            if (window.SourceItems.ItemContainerGenerator.ContainerFromIndex(index) is not null) count++;
        return count;
    }

    private static async Task<int> WaitForNormalUiMaterializationAsync(MainWindow window, MainViewModel viewModel)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var materialized = CountMaterializedItems(window);
        while (viewModel.Sources.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            materialized = CountMaterializedItems(window);
        }
        return materialized;
    }

    private async Task FlushAsynchronousFailuresAsync()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(100);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private void CreateTray(bool visible)
    {
        _productIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? throw new InvalidOperationException(LocalizationService.Current["App.IconUnavailable"]);
        _tray = new Forms.NotifyIcon { Icon = _productIcon, Text = LocalizationService.Current["Common.ProductName"], Visible = visible };
        LocalizationService.Current.CultureChanged += TrayCultureChanged;
        RebuildTrayMenu();
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void TrayCultureChanged(object? sender, EventArgs eventArgs) => RebuildTrayMenu();

    private void RebuildTrayMenu()
    {
        if (_tray is null) return;
        var previousMenu = _trayMenu;
        var previousFont = _trayMenuFont;
        var menu = new Forms.ContextMenuStrip();
        _trayMenuFont = CreateTrayMenuFont(menu.Font.Size);
        menu.Font = _trayMenuFont;
        menu.Items.Add(LocalizationService.Current["App.TrayOpen"], null, (_, _) => ShowMainWindow());
        menu.Items.Add(LocalizationService.Current["App.TrayRestoreAll"], null, async (_, _) =>
        {
            try
            {
                await InvokeOnDispatcherAsync(async () =>
                {
                    if (_viewModel is not null) await _viewModel.RestoreAllAsync();
                });
            }
            catch (Exception exception) { QueueFatalExit("Tray restore command failed.", exception); }
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(LocalizationService.Current["App.TrayExit"], null, async (_, _) => await ExitAndRestoreAsync());
        _trayMenu = menu;
        _tray.ContextMenuStrip = menu;
        previousMenu?.Dispose();
        previousFont?.Dispose();
    }

    private static Font CreateTrayMenuFont(float size)
    {
        var families = LocalizationService.Current.CurrentLanguage == LocalizationService.ChineseLanguage
            ? new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" }
            : new[] { "Segoe UI Variable Text", "Segoe UI", "Arial" };
        foreach (var family in families)
        {
            var font = new System.Drawing.Font(family, size, System.Drawing.FontStyle.Regular, GraphicsUnit.Point);
            if (font.Name.Equals(family, StringComparison.OrdinalIgnoreCase)) return font;
            font.Dispose();
        }
        var systemMenuFont = System.Drawing.SystemFonts.MenuFont;
        var fallbackFamily = systemMenuFont?.FontFamily ?? System.Drawing.FontFamily.GenericSansSerif;
        return new System.Drawing.Font(fallbackFamily, size, System.Drawing.FontStyle.Regular, GraphicsUnit.Point);
    }

    private void RegisterGracefulExitSignal()
    {
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, _exitSignalName);
        _exitSignalRegistration = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, timedOut) =>
        {
            if (!timedOut) _ = Dispatcher.InvokeAsync(() => ExitAndRestoreAsync(), DispatcherPriority.Send).Task.Unwrap();
        }, null, Timeout.Infinite, true);
    }

    public void HideToTray()
    {
        _window?.Hide();
        if (_viewModel?.TryConsumeTrayHint() == true)
            _tray?.ShowBalloonTip(1500, LocalizationService.Current["Common.ProductName"], LocalizationService.Current["App.TrayHint"], Forms.ToolTipIcon.Info);
    }

    private void ShowMainWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(ShowMainWindow, DispatcherPriority.Normal);
            return;
        }
        if (_window is null) return;
        _logger?.Info("Tray action started. Operation=Open main window; DispatcherAccess=True.");
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async Task InvokeOnDispatcherAsync(Func<Task> action)
    {
        if (Dispatcher.CheckAccess())
        {
            await action();
            return;
        }
        await Dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task.Unwrap();
    }

    public async Task ExitAndRestoreAsync(int exitCode = 0)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => ExitAndRestoreAsync(exitCode), DispatcherPriority.Send).Task.Unwrap();
            return;
        }
        if (exitCode != 0) Interlocked.Exchange(ref _exitCode, exitCode);
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return;

        await CleanupAsync("View model settings flush and pending operations", async () =>
        {
            if (_viewModel is not null) await _viewModel.PrepareForExitAsync();
        });
        await CleanupAsync("Core Audio restore/dispose", async () =>
        {
            if (_audio is not null) await _audio.DisposeAsync();
        });
        await CleanupAsync("Browser bridge dispose", async () =>
        {
            if (_bridge is not null) await _bridge.DisposeAsync();
        });
        await CleanupAsync("View model dispose", () =>
        {
            _viewModel?.Dispose();
            return Task.CompletedTask;
        });
        await CleanupAsync("Main window close", () =>
        {
            if (_window is not null)
            {
                _window.AllowClose = true;
                _window.Close();
            }
            return Task.CompletedTask;
        });
        await CleanupAsync("Tray dispose", () =>
        {
            LocalizationService.Current.CultureChanged -= TrayCultureChanged;
            _tray?.Dispose();
            _trayMenu?.Dispose();
            _trayMenu = null;
            _trayMenuFont?.Dispose();
            _trayMenuFont = null;
            _productIcon?.Dispose();
            _productIcon = null;
            return Task.CompletedTask;
        });
        await CleanupAsync("Single-instance mutex release", () =>
        {
            if (_ownsMutex) _singleInstance?.ReleaseMutex();
            _ownsMutex = false;
            _singleInstance?.Dispose();
            return Task.CompletedTask;
        });
        await CleanupAsync("Graceful-exit signal dispose", () =>
        {
            _exitSignalRegistration?.Unregister(null);
            _exitSignalRegistration = null;
            _exitSignal?.Dispose();
            _exitSignal = null;
            return Task.CompletedTask;
        });

        if (_uiSmokeTest && Volatile.Read(ref _exitCode) == 0)
        {
            try { _uiSmokeMonitor?.ThrowIfFailed(); }
            catch (Exception exception) { TryLogError("UI smoke test captured a failure during shutdown.", exception); Interlocked.Exchange(ref _exitCode, 1); }
        }
        try { _uiSmokeMonitor?.Dispose(); }
        catch (Exception exception) { TryLogError("UI smoke monitor dispose failed.", exception); Interlocked.Exchange(ref _exitCode, 1); }
        DetachExceptionHandlers();
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        Shutdown(Volatile.Read(ref _exitCode));
    }

    private void SystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) ApplyAccessibilityColors();
    }

    private void ApplyAccessibilityColors()
    {
        if (SystemParameters.HighContrast)
        {
            Resources["WindowBackgroundBrush"] = System.Windows.SystemColors.WindowBrush;
            Resources["SurfaceBrush"] = System.Windows.SystemColors.WindowBrush;
            Resources["SurfaceSecondaryBrush"] = System.Windows.SystemColors.ControlBrush;
            Resources["TextPrimaryBrush"] = System.Windows.SystemColors.WindowTextBrush;
            Resources["TextSecondaryBrush"] = System.Windows.SystemColors.WindowTextBrush;
            Resources["BorderBrush"] = System.Windows.SystemColors.ActiveBorderBrush;
            Resources["PrimaryBrush"] = System.Windows.SystemColors.HighlightBrush;
            Resources["PrimaryDarkBrush"] = System.Windows.SystemColors.HighlightBrush;
            Resources["PrimarySoftBrush"] = System.Windows.SystemColors.ControlBrush;
            Resources["SuccessBrush"] = System.Windows.SystemColors.WindowTextBrush;
            Resources["WarningBrush"] = System.Windows.SystemColors.WindowTextBrush;
            Resources["WarningSoftBrush"] = System.Windows.SystemColors.ControlBrush;
            Resources["WarningBorderBrush"] = System.Windows.SystemColors.ActiveBorderBrush;
            Resources["WarningTextBrush"] = System.Windows.SystemColors.WindowTextBrush;
            Resources["ErrorBrush"] = System.Windows.SystemColors.WindowTextBrush;
            Resources["WhiteBrush"] = System.Windows.SystemColors.HighlightTextBrush;
            Resources["DisabledBrush"] = System.Windows.SystemColors.GrayTextBrush;
            Resources["ShadowBrush"] = System.Windows.Media.Brushes.Transparent;
            return;
        }

        RestoreBrush("WindowBackgroundBrush", "WindowBackgroundColor");
        RestoreBrush("SurfaceBrush", "SurfaceColor");
        RestoreBrush("SurfaceSecondaryBrush", "SurfaceSecondaryColor");
        RestoreBrush("TextPrimaryBrush", "TextPrimaryColor");
        RestoreBrush("TextSecondaryBrush", "TextSecondaryColor");
        RestoreBrush("BorderBrush", "BorderColor");
        RestoreBrush("PrimaryBrush", "PrimaryColor");
        RestoreBrush("PrimaryDarkBrush", "PrimaryDarkColor");
        RestoreBrush("PrimarySoftBrush", "PrimarySoftColor");
        RestoreBrush("SuccessBrush", "SuccessColor");
        RestoreBrush("WarningBrush", "WarningColor");
        RestoreBrush("WarningSoftBrush", "WarningSoftColor");
        RestoreBrush("WarningBorderBrush", "WarningBorderColor");
        RestoreBrush("WarningTextBrush", "WarningTextColor");
        RestoreBrush("ErrorBrush", "ErrorColor");
        RestoreBrush("WhiteBrush", "WhiteColor");
        RestoreBrush("DisabledBrush", "DisabledColor");
        RestoreBrush("ShadowBrush", "ShadowColor");
    }

    private void RestoreBrush(string brushKey, string colorKey)
    {
        if (FindResource(colorKey) is System.Windows.Media.Color color)
            Resources[brushKey] = new System.Windows.Media.SolidColorBrush(color);
    }

    private async Task CleanupAsync(string operation, Func<Task> cleanup)
    {
        try { await cleanup(); }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _exitCode, 1);
            TryLogError($"{operation} failed.", exception);
        }
    }

    private void TryLogError(string message, Exception exception)
    {
        try { _logger?.Error(message, exception); }
        catch { System.Diagnostics.Trace.WriteLine($"{message}{Environment.NewLine}{exception}"); }
    }

    private enum StartupStage
    {
        Logging,
        SingleInstance,
        BrowserBridge,
        ViewModel,
        WindowCreation,
        TrayCreation,
        AudioInitialization,
        WindowDisplay,
        Complete
    }
}
