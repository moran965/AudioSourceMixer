using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using AudioSourceMixer.Core.Abstractions;
using AudioSourceMixer.Core.Browser;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;
using AudioSourceMixer.Desktop.Diagnostics;
using AudioSourceMixer.Desktop.Controls;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop.Tests;

public sealed class WpfBindingRegressionTests
{
    [Fact]
    public void CenterDetentHasHysteresisAndCanCrossCenter()
    {
        var detented = false;
        Assert.Equal(0, CenterDetent.Apply(4.9, ref detented));
        Assert.True(detented);
        Assert.Equal(0, CenterDetent.Apply(-7.9, ref detented));
        Assert.Equal(-8, CenterDetent.Apply(-8, ref detented));
        Assert.False(detented);
        Assert.Equal(-9, CenterDetent.Apply(-9, ref detented));
        Assert.Equal(0, CenterDetent.Apply(-5, ref detented));
        Assert.Equal(8, CenterDetent.Apply(8, ref detented));
    }

    [Fact]
    public async Task CenterDetentSliderDoesNotSnapProgrammaticUpdates()
    {
        var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { var slider = new CenterDetentSlider { Minimum = -100, Maximum = 100, Value = 3 }; completion.SetResult(slider.Value); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.Equal(3, await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MainWindowMaterializesTemplateAndUsesSafeBindingDirections()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunWpfTestAsync(completion)) { IsBackground = true, Name = "WPF regression STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "WPF regression STA thread did not terminate.");
    }

    private static void RunWpfTestAsync(TaskCompletionSource completion)
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            _ = app.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await ExecuteAssertionsAsync(app);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    app.Shutdown();
                    app.Dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task ExecuteAssertionsAsync(App app)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AudioSourceMixer.Desktop.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = UiSmokeVerifier.CreateDiagnosticSource();
        var fakeAudio = new FakeAudioService(source);
        var bridge = new BrowserBridgeServer();
        var profiles = new MemoryProfileStore();
        var logger = new RollingFileLogger(Path.Combine(directory, "logs"));
        var viewModel = new MainViewModel(fakeAudio, fakeAudio, fakeAudio, bridge, profiles,
            new JsonApplicationSettingsStore(directory), logger);
        MainWindow? window = null;

        try
        {
            await viewModel.InitializeAsync();
            window = new MainWindow(viewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false
            };

            using (var monitor = new UiSmokeMonitor())
            {
                var loaded = WaitForLoadedAsync(window);
                window.Show();
                await loaded;
                var result = await UiSmokeVerifier.VerifyAsync(window, viewModel.Sources.Single());

                Assert.True(window.IsVisible);
                Assert.True(result.ItemCount >= 1);
                Assert.Equal("ListBoxItem", result.ContainerType);
                Assert.Equal(UiSmokeVerifier.UpdatedPeak * 100d, result.PeakValue, 3);
                Assert.True(result.Bindings.Count >= 11);

                var sourceBindings = AuditSourceXaml();
                Assert.True(sourceBindings.Count >= 30);
                var peak = Assert.Single(sourceBindings.Where(entry => entry.SourceProperty == nameof(AudioSourceViewModel.PeakPercent)));
                Assert.Equal(BindingMode.OneWay, peak.DeclaredMode);
                Assert.False(peak.HasPublicSetter);

                var readOnly = sourceBindings.Where(entry => !entry.HasPublicSetter).ToArray();
                Assert.All(readOnly, entry => Assert.Equal(BindingMode.OneWay, entry.DeclaredMode));

                var twoWay = sourceBindings.Where(entry => entry.DeclaredMode == BindingMode.TwoWay).ToArray();
                Assert.Equal(
                    [nameof(MainViewModel.AutoApplyProfiles), nameof(AudioSourceViewModel.BalancePercent), nameof(MainViewModel.CloseToTray),
                     nameof(AudioSourceViewModel.EqualizerPreampDb), nameof(EqualizerBandViewModel.GainDb),
                     nameof(AudioSourceViewModel.IsEqualizerEnabled), nameof(AudioSourceViewModel.IsEqualizerExpanded),
                     nameof(MainViewModel.RememberProfiles), nameof(AudioSourceViewModel.SelectedEqualizerPresetId),
                     nameof(MainViewModel.ShowInactiveSessions), nameof(MainViewModel.ShowOperationTips),
                     nameof(MainViewModel.StartMinimizedToTray), nameof(MainViewModel.StartupEnabled),
                     nameof(AudioSourceViewModel.VolumePercent)],
                    twoWay.Select(entry => entry.SourceProperty).OrderBy(value => value).ToArray());
                Assert.All(twoWay, entry => Assert.True(entry.HasPublicSetter));
                var sourceList = Assert.IsType<ListBox>(window.SourceItems);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(sourceList));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(sourceList));
                Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(sourceList));

                var sliders = Descendants(window).OfType<Slider>().ToArray();
                var volume = Assert.Single(sliders.Where(slider => slider.Minimum == 0 && slider.Maximum == 100));
                var balance = Assert.Single(sliders.Where(slider => slider.Minimum == -100 && slider.Maximum == 100));
                volume.Value = 42;
                balance.Value = -25;
                await fakeAudio.WaitForControlsAsync(0.42f, -0.25f);
                Assert.All(viewModel.Sources.Where(item => item.Snapshot.Kind == AudioSourceKind.WindowsSession),
                    item => Assert.Equal(100, item.VolumeMaximum));
                var outputSelector = Assert.Single(Descendants(window).OfType<ComboBox>()
                    .Where(comboBox => System.Windows.Automation.AutomationProperties.GetName(comboBox) == "输出设备"));
                outputSelector.Focus();
                var stableDeviceCollection = viewModel.Sources.Single().OutputDevices;
                for (var refresh = 0; refresh < 100; refresh++)
                {
                    viewModel.Sources.Single().Update(source with { Peak = refresh / 100f, ObservedAt = DateTimeOffset.UtcNow });
                    viewModel.Sources.Single().UpdateOutputDevices([
                        OutputDeviceInfo.SystemDefault,
                        new OutputDeviceInfo("test-device", "Test Device"),
                        new OutputDeviceInfo("new-device", refresh % 2 == 0 ? "New Device" : "New Device (refreshed)")]);
                }
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, fakeAudio.RouteCalls);
                Assert.Same(stableDeviceCollection, outputSelector.ItemsSource);
                outputSelector.SelectedValue = "new-device";
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, fakeAudio.RouteCalls);
                var enter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(outputSelector), Environment.TickCount, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                outputSelector.RaiseEvent(enter);
                await WaitUntilAsync(() => fakeAudio.RouteCalls == 1);
                monitor.ThrowIfFailed();
            }

            using (var failureMonitor = new UiSmokeMonitor())
            {
                failureMonitor.Record("Injected XAML failure", new XamlParseException("regression sentinel"));
                Assert.Throws<InvalidOperationException>(() => failureMonitor.ThrowIfFailed());
            }

            var browserSnapshot = source with
            {
                Id = AudioSourceId.ForBrowserTab("edge", 77),
                Kind = AudioSourceKind.EdgeTab,
                Volume = 1.5f,
                DeviceId = "browser",
                OutputDeviceId = "usb-endpoint",
                OutputDeviceName = "USB DAC",
                RequestedOutputDeviceId = "usb-endpoint",
                RequestedOutputDeviceName = "USB DAC",
                EffectiveOutputDeviceId = "usb-endpoint",
                EffectiveOutputDeviceName = "USB DAC",
                RoutingState = AudioRoutingState.Applied,
                ProcessingMode = AudioProcessingMode.Advanced,
                Capabilities = new AudioSourceCapabilities(true, true, true, 2, true, true, true,
                    SupportsExtendedGain: true, SupportsOutputRouting: true, SupportsDeviceHotSwitch: true,
                    SupportsEqualizer: true),
                Effects = EqualizerCatalog.CreatePreset("bass")
            };
            using (var browserViewModel = new AudioSourceViewModel(browserSnapshot, fakeAudio, bridge, profiles,
                       () => new ApplicationSettings(), logger, [OutputDeviceInfo.SystemDefault]))
            {
                Assert.Equal(200, browserViewModel.VolumeMaximum);
                Assert.Equal(150, browserViewModel.VolumePercent);
                Assert.False(browserViewModel.SelectedOutputDevice!.IsAvailable);
                Assert.Contains("已生效", browserViewModel.OutputStatus);
                Assert.Contains("USB DAC", browserViewModel.OutputStatus);
                browserViewModel.UpdateOutputDevices([OutputDeviceInfo.SystemDefault,
                    new OutputDeviceInfo("usb-endpoint", "USB DAC", ChannelCount: 2, SampleRate: 48000)]);
                Assert.True(browserViewModel.SelectedOutputDevice!.IsAvailable);
                Assert.Equal("usb-endpoint", browserViewModel.SelectedOutputDevice.Id);
                Assert.True(browserViewModel.SupportsEqualizer);
                Assert.Equal("bass", browserViewModel.SelectedEqualizerPresetId);
                browserViewModel.EqualizerBands[3].GainDb = 4;
                Assert.Equal(EqualizerCatalog.CustomPresetId, browserViewModel.SelectedEqualizerPresetId);
                Assert.Equal(4, browserViewModel.Effects.Bands[3].GainDb);
                var preservedVolume = browserViewModel.VolumePercent;
                var preservedBalance = browserViewModel.BalancePercent;
                var preservedOutput = browserViewModel.SelectedOutputDeviceId;
                browserViewModel.SelectedEqualizerPresetId = "vocal";
                Assert.Equal(preservedVolume, browserViewModel.VolumePercent);
                Assert.Equal(preservedBalance, browserViewModel.BalancePercent);
                Assert.Equal(preservedOutput, browserViewModel.SelectedOutputDeviceId);
                browserViewModel.ResetEqualizerCommand.Execute(null);
                Assert.False(browserViewModel.Effects.Enabled);
                Assert.All(browserViewModel.Effects.Bands, band => Assert.Equal(0, band.GainDb));
            }

            using (var windowsViewModel = new AudioSourceViewModel(source with
                   {
                       OutputDeviceId = "test-device",
                       OutputDeviceName = "Test Device",
                       RequestedOutputDeviceId = "test-device",
                       RequestedOutputDeviceName = "Test Device",
                       EffectiveOutputDeviceId = "test-device",
                       EffectiveOutputDeviceName = "Test Device",
                       RoutingState = AudioRoutingState.Applied
                   }, fakeAudio, bridge, profiles, () => new ApplicationSettings(), logger,
                   [OutputDeviceInfo.SystemDefault, new OutputDeviceInfo("test-device", "Test Device")]))
            {
                Assert.Equal(100, windowsViewModel.VolumeMaximum);
                Assert.False(windowsViewModel.SupportsExtendedGain);
                Assert.True(windowsViewModel.SupportsOutputRouting);
                Assert.Contains("Test Device", windowsViewModel.OutputStatus);
                Assert.Contains("已生效", windowsViewModel.OutputStatus);
            }

            var processStart = DateTimeOffset.UnixEpoch.AddDays(1);
            var siblingOne = source with
            {
                Id = AudioSourceId.ForWindowsSession("test-device", "sibling-one"),
                ProcessId = 4242,
                ExecutablePath = "C:\\Player\\player.exe",
                SessionInstanceIdentifier = "sibling-one",
                ProcessStartTimeUtc = processStart
            };
            var siblingTwo = siblingOne with
            {
                Id = AudioSourceId.ForWindowsSession("test-device", "sibling-two"),
                SessionInstanceIdentifier = "sibling-two"
            };
            var saved = new AudioSourceProfile(ProfileKeys.For(siblingOne), 0.55f, -0.2f, false,
                OutputDeviceId: "test-device", OutputDeviceName: "Test Device");
            var siblingStore = new MemoryProfileStore(saved);
            var siblingAudio = new FakeAudioService(siblingOne, siblingTwo);
            var siblingViewModel = new MainViewModel(siblingAudio, siblingAudio, siblingAudio, bridge, siblingStore,
                new JsonApplicationSettingsStore(Path.Combine(directory, "siblings")), logger);
            try
            {
                await siblingViewModel.InitializeAsync();
                await WaitUntilAsync(() => siblingAudio.VolumeCallsFor(siblingOne.Id) > 0 &&
                    siblingAudio.VolumeCallsFor(siblingTwo.Id) > 0 && siblingAudio.RouteCalls == 1);
                Assert.Equal(1, siblingAudio.RouteCalls);

                var migrated = siblingOne with
                {
                    Id = AudioSourceId.ForWindowsSession("migrated-device", "sibling-three"),
                    DeviceId = "migrated-device",
                    SessionInstanceIdentifier = "sibling-three"
                };
                siblingAudio.PublishSources(siblingTwo, migrated);
                await WaitUntilAsync(() => siblingAudio.VolumeCallsFor(migrated.Id) > 0);
                Assert.Equal(1, siblingAudio.RouteCalls);

                siblingViewModel.Sources[0].RestoreCommand.Execute(null);
                await siblingStore.Removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.All(siblingViewModel.Sources, item => Assert.Equal(100, item.VolumePercent));
                Assert.Equal(1, siblingAudio.RestoreCalls);

                siblingViewModel.Sources[0].VolumePercent = 44;
                await siblingViewModel.RestoreAllAsync();
                await Task.Delay(150);
                Assert.True(siblingStore.Cleared);
                Assert.Equal(0, siblingStore.SavesAfterClear);
                Assert.All(siblingViewModel.Sources, item => Assert.Equal(100, item.VolumePercent));
            }
            finally { siblingViewModel.Dispose(); }

            await AssertUserRouteSurvivesSessionMigrationAsync(directory, bridge, logger,
                "PotPlayer", "C:\\Player\\PotPlayerMini64.exe", 5101);
            await AssertUserRouteSurvivesSessionMigrationAsync(directory, bridge, logger,
                "Edge", "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", 5102);
            await AssertBrowserOriginProfileIsInitialOnlyAsync(directory, logger);

            var ignoredSettings = new JsonApplicationSettingsStore(Path.Combine(directory, "remember-disabled"));
            await ignoredSettings.SaveAsync(new ApplicationSettings(RememberProfiles: false));
            var ignoredAudio = new FakeAudioService(siblingOne);
            var ignoredViewModel = new MainViewModel(ignoredAudio, ignoredAudio, ignoredAudio, bridge,
                new MemoryProfileStore(saved), ignoredSettings, logger);
            try
            {
                await ignoredViewModel.InitializeAsync();
                await Task.Delay(150);
                Assert.Equal(0, ignoredAudio.RouteCalls);
                Assert.Equal(0, ignoredAudio.VolumeCallsFor(siblingOne.Id));
            }
            finally { ignoredViewModel.Dispose(); }

            var persistedSettingsDirectory = Path.Combine(directory, "settings-ordering");
            var persistedSettingsStore = new JsonApplicationSettingsStore(persistedSettingsDirectory);
            await persistedSettingsStore.SaveAsync(new ApplicationSettings(CloseToTray: false,
                AutoApplyProfiles: false, RememberProfiles: true, ShowInactiveSessions: false));
            var settingsViewModel = new MainViewModel(new FakeAudioService(siblingOne), new FakeAudioService(siblingOne),
                new FakeAudioService(siblingOne), bridge, new MemoryProfileStore(), persistedSettingsStore, logger);
            try
            {
                await settingsViewModel.LoadSettingsAsync();
                Assert.False(settingsViewModel.CloseToTray);
                Assert.False(settingsViewModel.ShowInactiveSessions);
                settingsViewModel.CloseToTray = true;
                settingsViewModel.CloseToTray = false;
                settingsViewModel.ShowInactiveSessions = true;
                settingsViewModel.ShowInactiveSessions = false;
                settingsViewModel.RememberProfiles = false;
                settingsViewModel.RememberProfiles = true;
                await settingsViewModel.FlushSettingsAsync();
                var persisted = await persistedSettingsStore.LoadAsync();
                Assert.False(persisted.CloseToTray);
                Assert.False(persisted.ShowInactiveSessions);
                Assert.True(persisted.RememberProfiles);
                Assert.Empty(Directory.GetFiles(persistedSettingsDirectory, "*.tmp"));
            }
            finally { settingsViewModel.Dispose(); }
        }
        finally
        {
            if (window is not null)
            {
                window.AllowClose = true;
                window.Close();
            }
            viewModel.Dispose();
            await bridge.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task AssertUserRouteSurvivesSessionMigrationAsync(
        string directory,
        BrowserBridgeServer bridge,
        RollingFileLogger logger,
        string displayName,
        string executablePath,
        int processId)
    {
        var initial = UiSmokeVerifier.CreateDiagnosticSource() with
        {
            Id = AudioSourceId.ForWindowsSession("headphones", $"{displayName}-initial"),
            DisplayName = displayName,
            ProcessId = (uint)processId,
            ExecutablePath = executablePath,
            SessionInstanceIdentifier = $"{displayName}-initial",
            ProcessStartTimeUtc = DateTimeOffset.UnixEpoch.AddDays(processId),
            DeviceId = "headphones",
            OutputDeviceId = string.Empty,
            OutputDeviceName = OutputDeviceInfo.SystemDefault.Name,
            RequestedOutputDeviceId = string.Empty,
            RequestedOutputDeviceName = OutputDeviceInfo.SystemDefault.Name,
            EffectiveOutputDeviceId = "headphones",
            EffectiveOutputDeviceName = "Headphones",
            RoutingState = AudioRoutingState.SystemDefault
        };
        var realtek = new OutputDeviceInfo("realtek-speakers", "Realtek Speakers", ChannelCount: 2, SampleRate: 48000);
        var profile = new AudioSourceProfile(ProfileKeys.For(initial), 1f, 0f, false,
            OutputDeviceId: string.Empty, OutputDeviceName: OutputDeviceInfo.SystemDefault.Name);
        var profileStore = new MemoryProfileStore(profile);
        var audio = new FakeAudioService(initial);
        var viewModel = new MainViewModel(audio, audio, audio, bridge, profileStore,
            new JsonApplicationSettingsStore(Path.Combine(directory, $"route-race-{displayName}")), logger);

        try
        {
            await viewModel.InitializeAsync();
            await WaitUntilAsync(() => audio.RouteCalls == 1);
            audio.ResetRoutes();

            var migrated = initial with
            {
                Id = AudioSourceId.ForWindowsSession("realtek-speakers", $"{displayName}-migrated"),
                SessionInstanceIdentifier = $"{displayName}-migrated",
                DeviceId = "realtek-speakers",
                OutputDeviceId = "realtek-speakers",
                OutputDeviceName = realtek.Name,
                RequestedOutputDeviceId = "realtek-speakers",
                RequestedOutputDeviceName = realtek.Name,
                EffectiveOutputDeviceId = "realtek-speakers",
                EffectiveOutputDeviceName = realtek.Name,
                RoutingState = AudioRoutingState.PendingStreamRestart
            };
            audio.RouteHook = (sourceId, endpointId, requestSource, cancellationToken) =>
            {
                if (requestSource != AudioRouteRequestSource.User) return Task.CompletedTask;
                audio.PublishSources();
                audio.PublishSources(migrated);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };

            await viewModel.Sources.Single().UserSelectOutputDeviceAsync(realtek);
            await WaitUntilAsync(() => viewModel.Sources.Count == 1 && viewModel.Sources[0].Snapshot.Id == migrated.Id);
            await Task.Delay(150);

            Assert.Equal(
                [("realtek-speakers", AudioRouteRequestSource.User)],
                audio.RouteRequests.Select(request => (request.EndpointId, request.RequestSource)).ToArray());
            Assert.Equal("realtek-speakers", profileStore.Find(profile.StableKey)?.OutputDeviceId);
        }
        finally
        {
            viewModel.Dispose();
        }
    }

    private static async Task AssertBrowserOriginProfileIsInitialOnlyAsync(string directory, RollingFileLogger logger)
    {
        var pipeName = $"{BrowserBridgeServer.PipeName}.BrowserProfile.{Guid.NewGuid():N}";
        await using var browserBridge = new BrowserBridgeServer(pipeName, commandTimeout: TimeSpan.FromSeconds(2));
        var audio = new FakeAudioService();
        var store = new MemoryProfileStore();
        var viewModel = new MainViewModel(audio, audio, audio, browserBridge, store,
            new JsonApplicationSettingsStore(Path.Combine(directory, "browser-profile-initial-only")), logger);
        browserBridge.Start();
        try
        {
            await viewModel.InitializeAsync();
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            const string origin = "http://same-origin.test";
            await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.register","browser":"chrome","tabId":11,"title":"A","origin":"{{origin}}","generation":1}""");
            await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.register","browser":"chrome","tabId":12,"title":"B","origin":"{{origin}}","generation":1}""");
            await WaitUntilAsync(() => viewModel.Sources.Count(item => item.Snapshot.Kind == AudioSourceKind.ChromeTab) == 2);

            var tabA = viewModel.Sources.Single(item => item.Id == AudioSourceId.ForBrowserTab("chrome", 11));
            var tabB = viewModel.Sources.Single(item => item.Id == AudioSourceId.ForBrowserTab("chrome", 12));
            var route = tabA.UserSelectOutputDeviceAsync(new OutputDeviceInfo("realtek-speakers", "Realtek Speakers"));
            var command = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(
                (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!));
            Assert.Equal(11, command.TabId);
            Assert.Equal(2, command.Generation);
            await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.update","browser":"chrome","tabId":11,"routingState":"PendingAuthorization","outputDeviceId":"realtek-speakers","outputDeviceName":"Realtek Speakers","correlationId":"{{command.CorrelationId}}","generation":{{command.Generation}}}""");
            await route;
            Assert.Equal("realtek-speakers", store.Find(tabA.StableProfileKey)?.OutputDeviceId);

            await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.update","browser":"chrome","tabId":11,"title":"A","origin":"{{origin}}","peak":0.2,"generation":{{command.Generation}}}""");
            await Task.Delay(150);
            Assert.Equal(string.Empty, tabB.SelectedOutputDeviceId);
        }
        finally { viewModel.Dispose(); }
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(25, timeout.Token);
    }

    private static IReadOnlyList<SourceBinding> AuditSourceXaml()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml");
        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        var entries = new List<SourceBinding>();
        foreach (var element in document.Descendants())
        {
            var templateDepth = element.Ancestors().Count(ancestor => ancestor.Name.LocalName == "DataTemplate");
            var sourceType = templateDepth > 1 ? typeof(EqualizerBandViewModel)
                : templateDepth == 1 ? typeof(AudioSourceViewModel)
                : typeof(MainViewModel);
            foreach (var attribute in element.Attributes().Where(attribute => attribute.Value.StartsWith("{Binding", StringComparison.Ordinal)))
            {
                var expression = attribute.Value["{Binding".Length..].TrimEnd('}').Trim();
                var sourceProperty = expression.Split(',', 2)[0].Trim();
                var modeMatch = Regex.Match(expression, @"(?:^|,)\s*Mode\s*=\s*(OneWay|TwoWay|OneWayToSource|OneTime)(?:\s*,|\s*$)");
                Assert.True(modeMatch.Success, $"Binding mode is not explicit at {element.Name.LocalName}.{attribute.Name.LocalName}: {attribute.Value}");
                var property = sourceType.GetProperty(sourceProperty, BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(property);
                var mode = Enum.Parse<BindingMode>(modeMatch.Groups[1].Value);
                entries.Add(new SourceBinding(element.Name.LocalName, attribute.Name.LocalName, sourceType.Name,
                    sourceProperty, mode, property.SetMethod?.IsPublic == true));
            }
        }
        return entries;
    }

    private sealed record SourceBinding(
        string Target,
        string TargetProperty,
        string Source,
        string SourceProperty,
        BindingMode DeclaredMode,
        bool HasPublicSetter);

    private sealed class FakeAudioService(params AudioSourceSnapshot[] initialSources) : IAudioSourceDiscovery, IAudioSourceController,
        IAudioOutputDeviceService, IAudioRoutingController
    {
        private AudioSourceSnapshot[] _sources = initialSources;
        private readonly ConcurrentQueue<(AudioSourceId SourceId, float Volume)> _volumes = new();
        private readonly ConcurrentQueue<float> _balances = new();
        private readonly ConcurrentQueue<(AudioSourceId SourceId, string EndpointId, AudioRouteRequestSource RequestSource)> _routeRequests = new();
        private int _routeCalls;
        private int _restoreCalls;
        public int RouteCalls => Volatile.Read(ref _routeCalls);
        public int RestoreCalls => Volatile.Read(ref _restoreCalls);
        public IReadOnlyList<(AudioSourceId SourceId, string EndpointId, AudioRouteRequestSource RequestSource)> RouteRequests
            => _routeRequests.ToArray();
        public Func<AudioSourceId, string, AudioRouteRequestSource, CancellationToken, Task>? RouteHook { get; set; }
        public event EventHandler<IReadOnlyList<AudioSourceSnapshot>>? SourcesChanged;
        public event EventHandler<OutputDeviceInfo>? DefaultDeviceChanged { add { } remove { } }
        public event EventHandler<IReadOnlyList<OutputDeviceInfo>>? OutputDevicesChanged;
        public event EventHandler<AudioRouteResult>? RoutingStateChanged { add { } remove { } }
        public Task<OutputDeviceInfo> InitializeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OutputDeviceInfo("test-device", "Test Device", ChannelCount: 2));
        public Task<IReadOnlyList<AudioSourceSnapshot>> GetSourcesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AudioSourceSnapshot>>(_sources);
        public Task<IReadOnlyList<OutputDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OutputDeviceInfo>>([OutputDeviceInfo.SystemDefault,
                new OutputDeviceInfo("test-device", "Test Device", ChannelCount: 2, SampleRate: 48000),
                new OutputDeviceInfo("realtek-speakers", "Realtek Speakers", ChannelCount: 2, SampleRate: 48000)]);
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(AudioSourceId sourceId, float volume, CancellationToken cancellationToken = default)
        {
            _volumes.Enqueue((sourceId, volume));
            return Task.CompletedTask;
        }
        public Task SetMuteAsync(AudioSourceId sourceId, bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBalanceAsync(AudioSourceId sourceId, float balance, CancellationToken cancellationToken = default)
        {
            _balances.Enqueue(balance);
            return Task.CompletedTask;
        }
        public Task RestoreAsync(AudioSourceId sourceId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _restoreCalls);
            return Task.CompletedTask;
        }
        public Task RestoreAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<AudioRouteResult> SetOutputDeviceAsync(AudioSourceId sourceId, string endpointId,
            AudioRouteRequestSource requestSource = AudioRouteRequestSource.User,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _routeCalls);
            var source = _sources.First(item => item.Id == sourceId);
            _routeRequests.Enqueue((sourceId, endpointId, requestSource));
            if (RouteHook is not null) await RouteHook(sourceId, endpointId, requestSource, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new AudioRouteResult(sourceId, source.ProcessId, endpointId,
                string.IsNullOrEmpty(endpointId) ? "test-device" : endpointId,
                string.IsNullOrEmpty(endpointId) ? AudioRoutingState.SystemDefault : AudioRoutingState.Applied);
        }

        public Task<string?> GetEffectiveOutputDeviceAsync(AudioSourceId sourceId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("test-device");

        public Task CancelPendingRoutesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task WaitForControlsAsync(float volume, float balance)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!_volumes.Any(value => Math.Abs(value.Volume - volume) < 0.001f) ||
                   !_balances.Any(value => Math.Abs(value - balance) < 0.001f))
                await Task.Delay(25, timeout.Token);
        }

        public int VolumeCallsFor(AudioSourceId sourceId) => _volumes.Count(value => value.SourceId == sourceId);

        public void PublishSources(params AudioSourceSnapshot[] sources)
        {
            _sources = sources;
            SourcesChanged?.Invoke(this, sources);
        }

        public void PublishOutputDevices(params OutputDeviceInfo[] devices)
            => OutputDevicesChanged?.Invoke(this, devices);

        public void ResetRoutes()
        {
            Interlocked.Exchange(ref _routeCalls, 0);
            while (_routeRequests.TryDequeue(out _)) { }
        }

    }

    private sealed class MemoryProfileStore(params AudioSourceProfile[] initial) : IAudioProfileStore
    {
        private readonly ConcurrentDictionary<string, AudioSourceProfile> _profiles =
            new(initial.ToDictionary(profile => profile.StableKey, StringComparer.Ordinal));
        private int _savesAfterClear;
        public TaskCompletionSource Removed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cleared { get; private set; }
        public int SavesAfterClear => Volatile.Read(ref _savesAfterClear);
        public AudioSourceProfile? Find(string stableKey) => _profiles.GetValueOrDefault(stableKey);
        public Task<IReadOnlyDictionary<string, AudioSourceProfile>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, AudioSourceProfile>>(new Dictionary<string, AudioSourceProfile>(_profiles));
        public Task SaveAsync(AudioSourceProfile profile, CancellationToken cancellationToken = default)
        {
            if (Cleared) Interlocked.Increment(ref _savesAfterClear);
            _profiles[profile.StableKey] = profile;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string stableKey, CancellationToken cancellationToken = default)
        {
            _profiles.TryRemove(stableKey, out _);
            Removed.TrySetResult();
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Cleared = true;
            _profiles.Clear();
            return Task.CompletedTask;
        }
    }
}
