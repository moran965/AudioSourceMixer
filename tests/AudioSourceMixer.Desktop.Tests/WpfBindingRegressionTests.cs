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
using AudioSourceMixer.Desktop.Localization;
using AudioSourceMixer.Desktop.ViewModels;
using AudioSourceMixer.Desktop.Views;

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

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF regression STA thread did not terminate.");
    }

    private static void RunWpfTestAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            var app = new App();
            app.InitializeComponent();
            _ = app.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await ExecuteAssertionsAsync(app);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    // Begin shutdown after the async dispatcher callback has returned. Calling
                    // Application.Shutdown followed by synchronous InvokeShutdown from inside
                    // that callback can leave the hosted Windows runner's dispatcher frame alive.
                    app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            });
            Dispatcher.Run();

            if (failure is null)
                completion.TrySetResult();
            else
                completion.TrySetException(failure);
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
                Assert.Equal("ContentPresenter", result.ContainerType);
                Assert.Equal(UiSmokeVerifier.UpdatedPeak * 100d, result.PeakValue, 3);
                Assert.True(result.PeakTrackWidth > 0);
                Assert.InRange(result.PeakIndicatorWidth / result.PeakTrackWidth, 0.67, 0.79);
                Assert.True(result.Bindings.Count >= 11);

                await WpfUiStyleAssertions.AssertAsync(app, window, viewModel);

                var localizedSources = viewModel.Sources.ToArray();
                var routeCallsBeforeLanguageChange = fakeAudio.RouteCalls;
                var restoreCallsBeforeLanguageChange = fakeAudio.RestoreCalls;
                var volumesBeforeLanguageChange = localizedSources.Select(item => item.VolumePercent).ToArray();
                viewModel.SelectedLanguage = LocalizationService.EnglishLanguage;
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                window.SelectSettingsPage();
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                Assert.Equal("en-US", window.Language.IetfLanguageTag, ignoreCase: true);
                Assert.Contains(Descendants(window.SettingsPage).OfType<TextBlock>(),
                    text => text.Text == LocalizationService.Current["Settings.LanguageSection"]);
                Assert.Equal(localizedSources.Length, viewModel.Sources.Count);
                Assert.All(viewModel.Sources.Select((item, index) => (item, index)), pair => Assert.Same(localizedSources[pair.index], pair.item));
                Assert.Equal(volumesBeforeLanguageChange, viewModel.Sources.Select(item => item.VolumePercent).ToArray());
                Assert.Equal(routeCallsBeforeLanguageChange, fakeAudio.RouteCalls);
                Assert.Equal(restoreCallsBeforeLanguageChange, fakeAudio.RestoreCalls);
                viewModel.SelectedLanguage = LocalizationService.ChineseLanguage;
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                window.SelectMixerPage();

                AssertEveryBindingDeclaresMode();
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
                     nameof(MainViewModel.HideBrowserAggregateSessions),
                     nameof(AudioSourceViewModel.IsEqualizerEnabled), nameof(AudioSourceViewModel.IsEqualizerExpanded),
                     nameof(MainViewModel.IsHiddenSourcesPopupOpen),
                     nameof(MainViewModel.RememberProfiles), nameof(AudioSourceViewModel.SelectedEqualizerPresetId),
                     nameof(MainViewModel.SelectedLanguage),
                     nameof(MainViewModel.ShowInactiveSessions), nameof(MainViewModel.ShowOperationTips),
                     nameof(MainViewModel.StartMinimizedToTray), nameof(MainViewModel.StartupEnabled),
                     nameof(AudioSourceViewModel.VolumePercent)],
                    twoWay.Select(entry => entry.SourceProperty).OrderBy(value => value).ToArray());
                Assert.All(twoWay, entry => Assert.True(entry.HasPublicSetter));
                var sourceList = window.SourceItems;
                Assert.True(VirtualizingPanel.GetIsVirtualizing(sourceList));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(sourceList));
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(sourceList));
                Assert.False(ScrollViewer.GetIsDeferredScrollingEnabled(window.SourceScroller));
                Assert.Equal(PanningMode.VerticalOnly, ScrollViewer.GetPanningMode(window.SourceScroller));
                Assert.Equal(ScrollBarVisibility.Disabled, window.SourceScroller.HorizontalScrollBarVisibility);
                Assert.Empty(Descendants(sourceList).OfType<ListBoxItem>());
                var firstContainer = Assert.IsType<ContentPresenter>(sourceList.ItemContainerGenerator.ContainerFromIndex(0));
                var firstCard = Assert.Single(Descendants(firstContainer).OfType<AudioSourceCard>());
                var gapPoint = firstCard.TranslatePoint(new Point(firstCard.ActualWidth / 2, firstCard.ActualHeight - 3), sourceList);
                var gapHit = sourceList.InputHitTest(gapPoint);
                Assert.IsNotType<Button>(gapHit);

                var sliders = Descendants(window).OfType<Slider>().ToArray();
                var volume = Assert.Single(sliders.Where(slider => slider.Minimum == 0 && slider.Maximum == 100));
                var balance = Assert.Single(sliders.Where(slider => slider.Minimum == -100 && slider.Maximum == 100));
                volume.Value = 42;
                balance.Value = -25;
                await fakeAudio.WaitForControlsAsync(0.42f, -0.25f);
                Assert.All(viewModel.Sources.Where(item => item.Snapshot.Kind == AudioSourceKind.WindowsSession),
                    item => Assert.Equal(100, item.VolumeMaximum));
                var outputSelector = Assert.Single(Descendants(window).OfType<ComboBox>()
                    .Where(comboBox => System.Windows.Automation.AutomationProperties.GetName(comboBox) == LocalizationService.Current["Card.OutputDevice"]));
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

                fakeAudio.PublishSources(UiSmokeVerifier.CreateDiagnosticSources().ToArray());
                await WaitUntilAsync(() => viewModel.Sources.Count == 3);
                var identitiesBeforeLevels = viewModel.Sources.ToDictionary(item => item.Id);
                var levelTarget = viewModel.Sources[1];
                fakeAudio.PublishLevels(new AudioSourceLevel(levelTarget.Id, 0.81f, DateTimeOffset.UtcNow));
                await WaitUntilAsync(() => Math.Abs(levelTarget.PeakPercent - 81) < 0.01);
                Assert.Equal(identitiesBeforeLevels.Keys, viewModel.Sources.Select(item => item.Id));
                Assert.All(viewModel.Sources, item => Assert.Same(identitiesBeforeLevels[item.Id], item));
                await AssertHiddenPopupLifecycleAndStableHeaderAsync(app, window, viewModel);
                await AssertLiveDragPreviewAndCleanupAsync(app, window, viewModel);
                var adjusted = viewModel.Sources[^1];
                var originalIndex = viewModel.Sources.IndexOf(adjusted);
                var stableOrder = viewModel.Sources.Select(item => item.Id).ToArray();
                adjusted.VolumePercent = Math.Max(0, adjusted.VolumePercent - 1);
                Assert.Equal(originalIndex, viewModel.Sources.IndexOf(adjusted));
                await Task.Delay(450);
                Assert.Equal(originalIndex, viewModel.Sources.IndexOf(adjusted));
                viewModel.FlushPendingPresentationForDiagnostics();
                Assert.Equal(stableOrder, viewModel.Sources.Select(item => item.Id).ToArray());

                var nextAdjusted = viewModel.Sources[^1];
                nextAdjusted.BalancePercent = nextAdjusted.BalancePercent > 0 ? -10 : 10;
                viewModel.FlushPendingPresentationForDiagnostics();
                Assert.Equal(stableOrder, viewModel.Sources.Select(item => item.Id).ToArray());
                adjusted.UpdatePeak(0.99f, DateTimeOffset.UtcNow);
                viewModel.FlushPendingPresentationForDiagnostics();
                Assert.Equal(stableOrder, viewModel.Sources.Select(item => item.Id).ToArray());
                nextAdjusted.ToggleMuteCommand.Execute(null);
                var routableSource = viewModel.Sources.First(item => item.Snapshot.Kind == AudioSourceKind.WindowsSession && item.SupportsOutputRouting);
                await routableSource.UserSelectOutputDeviceAsync(new OutputDeviceInfo("realtek-speakers", "Realtek Speakers"));
                var equalizedSource = viewModel.Sources.First(item => item.SupportsEqualizer);
                equalizedSource.SelectedEqualizerPresetId = "vocal";
                await Task.Delay(450);
                viewModel.FlushPendingPresentationForDiagnostics();
                Assert.Equal(stableOrder, viewModel.Sources.Select(item => item.Id).ToArray());

                var dragged = viewModel.Sources[^1];
                viewModel.MoveSourceBefore(dragged, viewModel.Sources[0]);
                var manualOrder = viewModel.Sources.Select(item => item.Id).ToArray();
                Assert.True(viewModel.IsManualSortMode);
                dragged.VolumePercent = Math.Max(0, dragged.VolumePercent - 1);
                viewModel.FlushPendingPresentationForDiagnostics();
                Assert.Equal(manualOrder, viewModel.Sources.Select(item => item.Id).ToArray());

                var hiddenId = viewModel.Sources[^1].Id;
                var restoreCallsBeforeHide = fakeAudio.RestoreCalls;
                viewModel.Sources[^1].HideCommand.Execute(null);
                Assert.DoesNotContain(viewModel.Sources, item => item.Id == hiddenId);
                var hiddenEntry = Assert.Single(viewModel.HiddenSources.Where(item => item.Id == hiddenId));
                Assert.True(hiddenEntry.CanRestore);
                Assert.Equal(restoreCallsBeforeHide, fakeAudio.RestoreCalls);
                hiddenEntry.RestoreCommand.Execute(null);
                Assert.Contains(viewModel.Sources, item => item.Id == hiddenId);
                Assert.Equal(restoreCallsBeforeHide, fakeAudio.RestoreCalls);
                viewModel.ResetSourceOrderCommand.Execute(null);
                Assert.True(viewModel.IsManualSortMode);
                Assert.False(viewModel.IsRecentSortMode);

                viewModel.Sources.First(item => item.SupportsEqualizer).IsEqualizerExpanded = true;
                await AssertResponsiveLayoutsAsync(app, window, viewModel);
                fakeAudio.PublishSources(source);
                await WaitUntilAsync(() => viewModel.Sources.Count == 1);
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
                Assert.Equal(LocalizationService.Current.Format("Source.RouteApplied", "USB DAC"), browserViewModel.OutputStatus);
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
                Assert.Equal(LocalizationService.Current.Format("Source.RouteApplied", "Test Device"), windowsViewModel.OutputStatus);
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
            await viewModel.PrepareForExitAsync();
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

            for (var index = 0; index < 2; index++)
            {
                var initialDefault = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(
                    (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!));
                Assert.True(initialDefault.FollowSystemDefault);
                Assert.Equal(string.Empty, initialDefault.OutputDeviceId);
                await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.update","browser":"chrome","tabId":{{initialDefault.TabId}},"routingState":"PendingAuthorization","outputDeviceId":"","followSystemDefault":true,"resolvedOutputDeviceId":"test-device","resolvedOutputDeviceName":"Test Device","correlationId":"{{initialDefault.CorrelationId}}","generation":{{initialDefault.Generation}}} """);
            }

            var tabA = viewModel.Sources.Single(item => item.Id == AudioSourceId.ForBrowserTab("chrome", 11));
            var tabB = viewModel.Sources.Single(item => item.Id == AudioSourceId.ForBrowserTab("chrome", 12));
            var route = tabA.UserSelectOutputDeviceAsync(new OutputDeviceInfo("realtek-speakers", "Realtek Speakers"));
            var command = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(
                (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!));
            Assert.Equal(11, command.TabId);
            Assert.True(command.Generation >= 2);
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

    private static async Task AssertResponsiveLayoutsAsync(App app, MainWindow window, MainViewModel viewModel)
    {
        var layoutHeights = new Dictionary<(double Width, double Height), (double Window, double Viewport)>();
        foreach (var (width, height) in new[] { (880d, 600d), (1240d, 820d), (1600d, 900d), (1920d, 1080d) })
        {
            window.Width = width;
            window.Height = height;
            await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var sourceList = window.SourceItems;
            sourceList.InvalidateMeasure();
            await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var scrollViewer = window.SourceScroller;
            layoutHeights[(width, height)] = (window.ActualHeight, scrollViewer.ViewportHeight);
            Assert.Equal(ScrollBarVisibility.Disabled, scrollViewer.HorizontalScrollBarVisibility);
            var realizedContainers = viewModel.Sources.Select(source => sourceList.ItemContainerGenerator.ContainerFromItem(source))
                .OfType<FrameworkElement>()
                .ToArray();
            var realizedWidths = realizedContainers
                .Select(container => $"{container.ActualWidth:F1}/{container.DesiredSize.Width:F1}")
                .ToArray();
            var visibleCards = realizedContainers.Count(container =>
            {
                var bounds = container.TransformToAncestor(scrollViewer)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                return bounds.Bottom > 0 && bounds.Top < scrollViewer.ViewportHeight;
            });
            Console.WriteLine($"LAYOUT {width:F0}x{height:F0}: ItemsActualHeight={sourceList.ActualHeight:F1}; " +
                              $"ViewportHeight={scrollViewer.ViewportHeight:F1}; VisibleCards={visibleCards}; " +
                              $"FontSize={window.FontSize:F1}; ScaleTransform=False");
            Assert.True(scrollViewer.ExtentWidth <= sourceList.ActualWidth + 2.5,
                $"Source cards overflow horizontally at {width}x{height}: extent={scrollViewer.ExtentWidth}, viewport={scrollViewer.ViewportWidth}, " +
                $"list={sourceList.ActualWidth:F1}, items=[{string.Join(", ", realizedWidths)}].");
            Assert.All(viewModel.Sources.Select(source => sourceList.ItemContainerGenerator.ContainerFromItem(source))
                    .OfType<FrameworkElement>(),
                container => Assert.True(container.ActualWidth <= scrollViewer.ViewportWidth + 0.5,
                    $"A source item is wider than the horizontal viewport at {width}x{height}: item={container.ActualWidth}, viewport={scrollViewer.ViewportWidth}."));

            foreach (var source in viewModel.Sources)
            {
                (sourceList.ItemContainerGenerator.ContainerFromItem(source) as FrameworkElement)?.BringIntoView();
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                var container = Assert.IsType<ContentPresenter>(sourceList.ItemContainerGenerator.ContainerFromItem(source));
                var card = Assert.Single(Descendants(container).OfType<AudioSourceCard>());
                Assert.True(card.ActualWidth <= sourceList.ActualWidth + 1);
                var output = Assert.Single(Descendants(card).OfType<ComboBox>()
                    .Where(comboBox => System.Windows.Automation.AutomationProperties.GetName(comboBox) == LocalizationService.Current["Card.OutputDevice"]));
                Assert.True(output.ActualWidth >= 180, $"Output selector is too narrow at {width}x{height}.");
                AssertInside(card, output, width, height);
                foreach (var button in Descendants(card).OfType<Button>().Where(button => button.IsVisible))
                    AssertInside(card, button, width, height);

                var labels = Descendants(card).OfType<TextBlock>().ToArray();
                var volumeLabel = labels.First(text => text.Text == LocalizationService.Current["Card.Volume"]);
                var volumeValue = labels.First(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(AudioSourceViewModel.VolumePercent));
                Assert.False(Bounds(volumeLabel, card).IntersectsWith(Bounds(volumeValue, card)),
                    $"Volume label/value overlap at {width}x{height}.");
            }
            Assert.DoesNotContain(Descendants(window), element => element is ScaleTransform);
        }
        var defaultLayout = layoutHeights[(1240, 820)];
        var largeLayout = layoutHeights[(1920, 1080)];
        Assert.True(largeLayout.Viewport >= defaultLayout.Viewport - 1,
            $"A larger requested window must not reduce the viewport: default={defaultLayout.Viewport}, large={largeLayout.Viewport}.");
        if (largeLayout.Window > defaultLayout.Window + 1)
            Assert.True(largeLayout.Viewport > defaultLayout.Viewport,
                $"A taller realized window must show more content: default={defaultLayout}, large={largeLayout}.");
        else
            Console.WriteLine($"LAYOUT work area capped both requested heights: default={defaultLayout}, large={largeLayout}.");
    }

    private static async Task AssertLiveDragPreviewAndCleanupAsync(App app, MainWindow window, MainViewModel viewModel)
    {
        var mixer = window.MixerPage;
        var list = window.SourceItems;
        var scroller = window.SourceScroller;
        var originalOrder = viewModel.Sources.ToArray();
        Assert.True(originalOrder.Length >= 3);
        originalOrder[1].IsEqualizerExpanded = true;
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var dragged = originalOrder[0];
        var draggedContainer = Assert.IsType<ContentPresenter>(list.ItemContainerGenerator.ContainerFromItem(dragged));
        var draggedCard = Assert.Single(Descendants(draggedContainer).OfType<AudioSourceCard>());
        var nextContainer = Assert.IsType<ContentPresenter>(list.ItemContainerGenerator.ContainerFromItem(originalOrder[1]));
        var nextMidpoint = nextContainer.TranslatePoint(new Point(), scroller).Y + nextContainer.ActualHeight / 2;
        var originalHeight = draggedCard.DragVisual.ActualHeight;
        var fadeStarts = mixer.InsertionFadeStartCount;

        Assert.True(mixer.BeginSourceDrag(dragged, draggedCard.DragVisual,
            new Point(Math.Max(1, draggedCard.DragVisual.ActualWidth - 42), 28)));
        Assert.True(mixer.HasActiveDragPreview);
        Assert.True(mixer.IsInsertionLineVisible);
        Assert.Equal(fadeStarts + 1, mixer.InsertionFadeStartCount);
        Assert.InRange(Math.Abs(mixer.ActiveDragPreviewSize.Width - draggedCard.DragVisual.ActualWidth), 0, 1);
        Assert.InRange(Math.Abs(mixer.ActiveDragPreviewSize.Height - originalHeight), 0, 1);
        Assert.Equal(originalHeight, draggedCard.DragVisual.ActualHeight, 1);
        Assert.Equal(0.28, dragged.DragPlaceholderOpacity, 3);

        mixer.ProcessDragPointForDiagnostics(new Point(scroller.ViewportWidth / 2, nextMidpoint + 12));
        Assert.Equal(dragged, viewModel.Sources[1]);
        Assert.True(mixer.FlipAnimationStartCount > 0);
        var animationStarts = mixer.FlipAnimationStartCount;
        mixer.ProcessDragPointForDiagnostics(new Point(scroller.ViewportWidth / 2, nextMidpoint + 12));
        Assert.Equal(animationStarts, mixer.FlipAnimationStartCount);
        Assert.Equal(fadeStarts + 1, mixer.InsertionFadeStartCount);
        Assert.Equal(originalHeight, draggedCard.DragVisual.ActualHeight, 1);

        await Task.Delay(210);
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Equal(0, mixer.ActiveFlipTransformCount);

        mixer.CancelSourceDrag();
        Assert.Equal(originalOrder, viewModel.Sources.ToArray());
        Assert.False(mixer.HasActiveDragPreview);
        Assert.False(mixer.IsInsertionLineVisible);
        Assert.Equal(0, mixer.ActiveFlipTransformCount);
        Assert.Equal(1, dragged.DragPlaceholderOpacity, 3);
        Assert.All(Descendants(list).OfType<ContentPresenter>(), container =>
            Assert.True(container.RenderTransform is null or { Value.IsIdentity: true }));
        Assert.All(originalOrder, source => Assert.Contains(source, viewModel.Sources));

        scroller.ScrollToTop();
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        draggedContainer = Assert.IsType<ContentPresenter>(list.ItemContainerGenerator.ContainerFromItem(dragged));
        draggedCard = Assert.Single(Descendants(draggedContainer).OfType<AudioSourceCard>());
        nextContainer = Assert.IsType<ContentPresenter>(list.ItemContainerGenerator.ContainerFromItem(originalOrder[1]));
        nextMidpoint = nextContainer.TranslatePoint(new Point(), scroller).Y + nextContainer.ActualHeight / 2;
        Assert.True(mixer.BeginSourceDrag(dragged, draggedCard.DragVisual, new Point(draggedCard.ActualWidth - 42, 28)));
        mixer.ProcessDragPointForDiagnostics(new Point(scroller.ViewportWidth / 2, nextMidpoint + 12));
        mixer.CommitSourceDragForDiagnostics();
        Assert.Equal(dragged, viewModel.Sources[1]);
        Assert.False(mixer.HasActiveDragPreview);
        Assert.False(mixer.IsInsertionLineVisible);
        Assert.Equal(0, mixer.ActiveFlipTransformCount);
        Assert.Equal(1, dragged.DragPlaceholderOpacity, 3);
    }

    private static async Task AssertHiddenPopupLifecycleAndStableHeaderAsync(App app, MainWindow window, MainViewModel viewModel)
    {
        var mixer = window.MixerPage;
        var originalSources = viewModel.Sources.Take(2).ToArray();
        var audioState = originalSources.ToDictionary(source => source.Id,
            source => (source.VolumePercent, source.BalancePercent, source.Snapshot.Muted, source.SelectedOutputDeviceId,
                source.SelectedEqualizerPresetId));
        SetBrowserStatusForDiagnostics(viewModel, "扩展连接暂不可用");
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var before = HeaderCoordinates(mixer, window);
        SetBrowserStatusForDiagnostics(viewModel, "扩展连接暂不可用，请检查浏览器增强连接状态");
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        AssertHeaderCoordinatesStable(before, HeaderCoordinates(mixer, window));
        var browserStatus = viewModel.BrowserStatus;
        var windowsBeforePopup = Application.Current.Windows.Cast<Window>().ToArray();

        foreach (var source in originalSources) source.HideCommand.Execute(null);
        Assert.Equal(2, viewModel.HiddenSources.Count);
        Assert.Equal(Visibility.Visible, mixer.HiddenButton.Visibility);
        Assert.Equal(browserStatus, viewModel.BrowserStatus);

        viewModel.IsHiddenSourcesPopupOpen = true;
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Assert.True(mixer.HiddenPopupIsOpen);
        Assert.True(mixer.HiddenPopupChildIsVisible);
        Assert.Equal(windowsBeforePopup, Application.Current.Windows.Cast<Window>().ToArray());

        var restoredId = viewModel.HiddenSources[0].Id;
        viewModel.HiddenSources[0].RestoreCommand.Execute(null);
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Assert.False(mixer.HiddenPopupIsOpen);
        Assert.False(mixer.HiddenPopupChildIsVisible);
        Assert.False(mixer.HiddenPopupChildHasVisibleRoot);
        Assert.Contains(viewModel.Sources, source => source.Id == restoredId);
        Assert.Single(viewModel.HiddenSources);
        Assert.Equal(browserStatus, viewModel.BrowserStatus);
        AssertHeaderCoordinatesStable(before, HeaderCoordinates(mixer, window));

        viewModel.IsHiddenSourcesPopupOpen = true;
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Assert.True(mixer.HiddenPopupIsOpen);
        window.SelectSettingsPage();
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Assert.False(mixer.HiddenPopupIsOpen);
        Assert.False(mixer.HiddenPopupChildIsVisible);
        Assert.False(mixer.HiddenPopupChildHasVisibleRoot);
        window.SelectMixerPage();
        viewModel.IsHiddenSourcesPopupOpen = true;
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Assert.True(mixer.HiddenPopupIsOpen);
        viewModel.RestoreAllHiddenCommand.Execute(null);
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Assert.Empty(viewModel.HiddenSources);
        Assert.Equal(Visibility.Hidden, mixer.HiddenButton.Visibility);
        Assert.False(mixer.HiddenPopupIsOpen);
        Assert.False(mixer.HiddenPopupChildIsVisible);
        Assert.False(mixer.HiddenPopupChildHasVisibleRoot);
        Assert.Equal(windowsBeforePopup, Application.Current.Windows.Cast<Window>().ToArray());
        Assert.Equal(browserStatus, viewModel.BrowserStatus);
        Assert.True(viewModel.SettingsForDiagnostics.HideBrowserAggregateSessions);
        Assert.Empty(viewModel.SettingsForDiagnostics.VisibleBrowserAggregates!);
        Assert.Empty(viewModel.SettingsForDiagnostics.ManuallyHiddenSources!);
        AssertHeaderCoordinatesStable(before, HeaderCoordinates(mixer, window));

        foreach (var original in originalSources)
        {
            var restored = Assert.Single(viewModel.Sources.Where(source => source.Id == original.Id));
            Assert.Same(original, restored);
            Assert.Equal(audioState[original.Id],
                (restored.VolumePercent, restored.BalancePercent, restored.Snapshot.Muted, restored.SelectedOutputDeviceId,
                    restored.SelectedEqualizerPresetId));
        }
    }

    private static (Point Sort, Point Hidden, Point Status, Point Scroller) HeaderCoordinates(MixerView mixer, UIElement root)
        => (mixer.SortButton.TranslatePoint(new Point(), root), mixer.HiddenButton.TranslatePoint(new Point(), root),
            mixer.BrowserStatusElement.TranslatePoint(new Point(), root), mixer.SourceScroller.TranslatePoint(new Point(), root));

    private static void AssertHeaderCoordinatesStable(
        (Point Sort, Point Hidden, Point Status, Point Scroller) before,
        (Point Sort, Point Hidden, Point Status, Point Scroller) after)
    {
        AssertPointStable(before.Sort, after.Sort, "sort button");
        AssertPointStable(before.Hidden, after.Hidden, "hidden button");
        AssertPointStable(before.Status, after.Status, "browser status");
        AssertPointStable(before.Scroller, after.Scroller, "source scroller");
    }

    private static void AssertPointStable(Point before, Point after, string name)
    {
        Assert.InRange(Math.Abs(before.X - after.X), 0, 1);
        Assert.InRange(Math.Abs(before.Y - after.Y), 0, 1);
        Console.WriteLine($"HEADER {name}: before={before}, after={after}");
    }

    private static void SetBrowserStatusForDiagnostics(MainViewModel viewModel, string value)
        => typeof(MainViewModel).GetProperty(nameof(MainViewModel.BrowserStatus), BindingFlags.Instance | BindingFlags.Public)!
            .SetMethod!.Invoke(viewModel, [value]);

    private static void AssertInside(FrameworkElement ancestor, FrameworkElement child, double width, double height)
    {
        var bounds = Bounds(child, ancestor);
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= ancestor.ActualWidth + 0.5 &&
                    bounds.Top >= -0.5 && bounds.Bottom <= ancestor.ActualHeight + 0.5,
            $"{child.GetType().Name} is clipped at {width}x{height}: {bounds} within {ancestor.ActualWidth}x{ancestor.ActualHeight}.");
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static IReadOnlyList<SourceBinding> AuditSourceXaml()
    {
        var entries = new List<SourceBinding>();
        var sources = new (string Path, Type RootType)[]
        {
            (Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"), typeof(MainViewModel)),
            (Path.Combine(AppContext.BaseDirectory, "SourceXaml", "MixerView.xaml"), typeof(MainViewModel)),
            (Path.Combine(AppContext.BaseDirectory, "SourceXaml", "SettingsView.xaml"), typeof(MainViewModel)),
            (Path.Combine(AppContext.BaseDirectory, "SourceXaml", "BrowserSetupView.xaml"), typeof(MainViewModel)),
            (Path.Combine(AppContext.BaseDirectory, "SourceXaml", "AudioSourceCard.xaml"), typeof(AudioSourceViewModel)),
            (Path.Combine(AppContext.BaseDirectory, "SourceXaml", "EqualizerPanel.xaml"), typeof(AudioSourceViewModel))
        };

        foreach (var (path, rootType) in sources)
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                var insideTemplate = element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "DataTemplate");
                var insideBandTemplate = Path.GetFileName(path) == "EqualizerPanel.xaml" && insideTemplate;
                foreach (var attribute in element.Attributes().Where(attribute => attribute.Value.StartsWith("{Binding", StringComparison.Ordinal)))
                {
                    var expression = attribute.Value["{Binding".Length..].TrimEnd('}').Trim();
                    if (expression.Contains("RelativeSource=", StringComparison.Ordinal) ||
                        expression.Contains("ElementName=", StringComparison.Ordinal)) continue;
                    var sourcePath = expression.Split(',', 2)[0].Trim();
                    var sourceProperty = sourcePath.Split('.', 2)[0];
                    var sourceType = insideBandTemplate
                        ? typeof(EqualizerBandViewModel)
                        : Path.GetFileName(path) == "MixerView.xaml" && insideTemplate &&
                          typeof(HiddenSourceViewModel).GetProperty(sourceProperty, BindingFlags.Instance | BindingFlags.Public) is not null
                            ? typeof(HiddenSourceViewModel)
                            : rootType;
                    var modeMatch = Regex.Match(expression, @"(?:^|,)\s*Mode\s*=\s*(OneWay|TwoWay|OneWayToSource|OneTime)(?:\s*,|\s*$)");
                    Assert.True(modeMatch.Success,
                        $"Binding mode is not explicit at {Path.GetFileName(path)}:{element.Name.LocalName}.{attribute.Name.LocalName}: {attribute.Value}");
                    var property = sourceType.GetProperty(sourceProperty, BindingFlags.Instance | BindingFlags.Public);
                    Assert.NotNull(property);
                    var mode = Enum.Parse<BindingMode>(modeMatch.Groups[1].Value);
                    entries.Add(new SourceBinding(element.Name.LocalName, attribute.Name.LocalName, sourceType.Name,
                        sourceProperty, mode, property.SetMethod?.IsPublic == true));
                }
            }
        }
        return entries;
    }

    private static void AssertEveryBindingDeclaresMode()
    {
        var sourceFiles = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "SourceXaml"), "*.xaml",
                SearchOption.AllDirectories)
            .Append(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));

        foreach (var path in sourceFiles)
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Root!.DescendantsAndSelf())
            {
                foreach (var attribute in element.Attributes()
                             .Where(attribute => attribute.Value.StartsWith("{Binding", StringComparison.Ordinal)))
                {
                    Assert.Matches(@"(?:^|,)\s*Mode\s*=\s*(OneWay|TwoWay|OneWayToSource|OneTime)(?:\s*,|\s*})", attribute.Value);
                }
            }
        }
    }

    private sealed record SourceBinding(
        string Target,
        string TargetProperty,
        string Source,
        string SourceProperty,
        BindingMode DeclaredMode,
        bool HasPublicSetter);

    private sealed class FakeAudioService(params AudioSourceSnapshot[] initialSources) : IAudioSourceDiscovery, IAudioSourceLevelDiscovery, IAudioSourceController,
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
        public event EventHandler<IReadOnlyList<AudioSourceLevel>>? SourceLevelsChanged;
        public event EventHandler<OutputDeviceInfo>? DefaultDeviceChanged { add { } remove { } }
        public event EventHandler<IReadOnlyList<OutputDeviceInfo>>? OutputDevicesChanged;
        public event EventHandler<AudioRouteResult>? RoutingStateChanged { add { } remove { } }
        public Task<OutputDeviceInfo> InitializeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OutputDeviceInfo("test-device", "Test Device", IsDefaultMultimedia: true, ChannelCount: 2));
        public Task<IReadOnlyList<AudioSourceSnapshot>> GetSourcesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AudioSourceSnapshot>>(_sources);
        public Task<IReadOnlyList<OutputDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OutputDeviceInfo>>([OutputDeviceInfo.SystemDefault,
                new OutputDeviceInfo("test-device", "Test Device", IsDefaultMultimedia: true, ChannelCount: 2, SampleRate: 48000),
                new OutputDeviceInfo("realtek-speakers", "Realtek Speakers", ChannelCount: 2, SampleRate: 48000)]);
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PublishLevels(params AudioSourceLevel[] levels) => SourceLevelsChanged?.Invoke(this, levels);
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
