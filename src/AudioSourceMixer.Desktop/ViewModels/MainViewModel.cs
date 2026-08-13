using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AudioSourceMixer.Core.Abstractions;
using AudioSourceMixer.Core.Browser;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;
using AudioSourceMixer.Desktop.Services;
using AudioSourceMixer.WindowsAudio;

namespace AudioSourceMixer.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAudioSourceDiscovery _discovery;
    private readonly IAudioSourceController _audio;
    private readonly IAudioOutputDeviceService _outputDeviceService;
    private readonly BrowserBridgeServer _bridge;
    private readonly IAudioProfileStore _profiles;
    private readonly JsonApplicationSettingsStore _settingsStore;
    private readonly RollingFileLogger _logger;
    private readonly IStartupRegistrationService _startup;
    private readonly Dictionary<AudioSourceId, AudioSourceViewModel> _byId = [];
    private IReadOnlyList<AudioSourceSnapshot> _windowsSources = [];
    private IReadOnlyList<BrowserTabSource> _browserTabs = [];
    private readonly ConcurrentDictionary<string, AudioSourceProfile> _savedProfiles = new(StringComparer.Ordinal);
    private readonly HashSet<AudioApplicationInstanceKey> _profilesApplied = [];
    private readonly HashSet<AudioSourceId> _profileValuesApplied = [];
    private readonly ConcurrentDictionary<AudioApplicationInstanceKey, ApplicationRouteIntent> _applicationRoutes = [];
    private readonly ConcurrentDictionary<AudioApplicationInstanceKey, DateTimeOffset> _applicationLastSeen = [];
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private readonly object _settingsSaveSync = new();
    private Task _settingsSaveTask = Task.CompletedTask;
    private bool _restoring;
    private bool _settingsLoaded;
    private string _deviceName = "正在初始化…";
    private string _browserStatus = "等待扩展连接";
    private ApplicationSettings _settings = new();

    public MainViewModel(IAudioSourceDiscovery discovery, IAudioSourceController audio, IAudioOutputDeviceService outputDeviceService,
        BrowserBridgeServer bridge, IAudioProfileStore profiles,
        JsonApplicationSettingsStore settingsStore, RollingFileLogger logger, IStartupRegistrationService? startup = null)
    {
        _discovery = discovery; _audio = audio; _outputDeviceService = outputDeviceService;
        _bridge = bridge; _profiles = profiles; _settingsStore = settingsStore; _logger = logger;
        _startup = startup ?? new StartupRegistrationService();
        RefreshCommand = new AsyncRelayCommand(() => _discovery.RefreshAsync(), Error);
        RestoreAllCommand = new AsyncRelayCommand(RestoreAllAsync, Error);
        ClearProfilesCommand = new AsyncRelayCommand(ConfirmClearProfilesAsync, Error);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        OpenInstallDirectoryCommand = new RelayCommand(OpenInstallDirectory);
        ManageChromeOutputsCommand = new AsyncRelayCommand(() => ManageBrowserOutputsAsync("chrome"), Error);
        ManageEdgeOutputsCommand = new AsyncRelayCommand(() => ManageBrowserOutputsAsync("edge"), Error);
        ClearChromeMappingsCommand = new AsyncRelayCommand(() => ClearBrowserMappingsAsync("chrome"), Error);
        ClearEdgeMappingsCommand = new AsyncRelayCommand(() => ClearBrowserMappingsAsync("edge"), Error);
        ResetAllCommand = new AsyncRelayCommand(ResetAllAsync, Error);
    }

    public ObservableCollection<AudioSourceViewModel> Sources { get; } = [];
    public ObservableCollection<OutputDeviceInfo> OutputDevices { get; } = [];
    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }
    public string BrowserStatus { get => _browserStatus; private set { if (Set(ref _browserStatus, value)) Raise(nameof(BrowserStatusVisibility)); } }
    public Visibility BrowserStatusVisibility => _bridge.IsConnected || _browserTabs.Count > 0 ||
        (!_browserStatus.Equals("等待扩展连接", StringComparison.Ordinal) && !_browserStatus.StartsWith("已连接", StringComparison.Ordinal))
        ? Visibility.Visible : Visibility.Collapsed;
    public ICommand RefreshCommand { get; }
    public ICommand RestoreAllCommand { get; }
    public ICommand ClearProfilesCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenInstallDirectoryCommand { get; }
    public ICommand ManageChromeOutputsCommand { get; }
    public ICommand ManageEdgeOutputsCommand { get; }
    public ICommand ClearChromeMappingsCommand { get; }
    public ICommand ClearEdgeMappingsCommand { get; }
    public ICommand ResetAllCommand { get; }
    public bool StartupAvailable => _startup.IsAvailable;
    public bool StartupEnabled
    {
        get => _startup.IsEnabled;
        set
        {
            try { _startup.SetEnabled(value, StartMinimizedToTray); }
            catch (Exception exception) { BrowserStatus = exception.Message; Error(exception); }
            Raise();
        }
    }
    public bool StartMinimizedToTray
    {
        get => _settings.StartMinimizedToTray;
        set
        {
            _settings = _settings with { StartMinimizedToTray = value };
            Raise(); SaveSettings();
            if (_startup.IsEnabled) _startup.SetEnabled(true, value);
        }
    }
    public bool AutoApplyProfilesEnabled => RememberProfiles;
    public string ChromeConnectionStatus => BrowserConnectionText("chrome");
    public string EdgeConnectionStatus => BrowserConnectionText("edge");
    public string VersionText => $"版本 {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";
    public string DeploymentText => StartupAvailable ? "安装版" : "便携版";
    public bool CloseToTray { get => _settings.CloseToTray; set { _settings = _settings with { CloseToTray = value }; Raise(); SaveSettings(); } }
    public bool ShowOperationTips { get => _settings.ShowOperationTips; set { _settings = _settings with { ShowOperationTips = value }; Raise(); SaveSettings(); } }
    public bool AutoApplyProfiles { get => _settings.AutoApplyProfiles; set { _settings = _settings with { AutoApplyProfiles = value }; Raise(); SaveSettings(); Reconcile(); } }
    // Turning this off keeps existing profiles on disk but ignores them for auto-apply and future saves.
    public bool RememberProfiles { get => _settings.RememberProfiles; set { _settings = _settings with { RememberProfiles = value }; Raise(); Raise(nameof(AutoApplyProfilesEnabled)); SaveSettings(); Reconcile(); } }
    public bool ShowInactiveSessions
    {
        get => _settings.ShowInactiveSessions;
        set { _settings = _settings with { ShowInactiveSessions = value }; Raise(); SaveSettings(); Reconcile(); }
    }

    public async Task InitializeAsync(AudioSourceSnapshot? diagnosticSource = null)
    {
        await LoadSettingsAsync();
        foreach (var (key, profile) in await _profiles.LoadAsync()) _savedProfiles[key] = profile;
        _discovery.SourcesChanged += AudioSourcesChanged;
        _discovery.DefaultDeviceChanged += DefaultDeviceChanged;
        _outputDeviceService.OutputDevicesChanged += OutputDevicesChanged;
        _bridge.TabsChanged += BrowserTabsChanged;
        var device = await _discovery.InitializeAsync();
        DeviceName = device.Name;
        ReplaceOutputDevices(await _outputDeviceService.GetOutputDevicesAsync());
        _windowsSources = await _discovery.GetSourcesAsync();
        if (diagnosticSource is not null) _windowsSources = _windowsSources.Append(diagnosticSource).ToArray();
        Reconcile();
    }

    public async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_settingsLoaded) return;
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        _settingsLoaded = true;
        Raise(nameof(CloseToTray));
        Raise(nameof(AutoApplyProfiles));
        Raise(nameof(RememberProfiles));
        Raise(nameof(ShowInactiveSessions));
        Raise(nameof(StartMinimizedToTray));
        Raise(nameof(ShowOperationTips));
        Raise(nameof(StartupEnabled));
        Raise(nameof(StartupAvailable));
    }

    public async Task RestoreAllAsync()
    {
        if (_restoring) return;
        _restoring = true;
        try
        {
            await Task.WhenAll(Sources.Select(source => source.CancelPendingChangesAsync()));
            await CancelApplicationRoutesAsync();
            if (_audio is IAudioRoutingController routing) await routing.CancelPendingRoutesAsync();
            await _audio.RestoreAllAsync();
            foreach (var tab in _browserTabs)
                await _bridge.SetAudioAsync(tab.Id, 1, 0, false, "", null, OutputDevices.ToArray(),
                    effects: EqualizerCatalog.Off);
            await _profiles.ClearAsync();
            _savedProfiles.Clear();
            _profilesApplied.Clear();
            _profileValuesApplied.Clear();
            _applicationRoutes.Clear();
            _applicationLastSeen.Clear();
            foreach (var source in Sources) source.ResetDisplayToDefaults();
        }
        finally { _restoring = false; }
    }

    private void DefaultDeviceChanged(object? sender, OutputDeviceInfo device) => Dispatch(() => DeviceName = device.Name);
    private void OutputDevicesChanged(object? sender, IReadOnlyList<OutputDeviceInfo> devices)
        => Dispatch(() => ReplaceOutputDevices(devices));
    private void AudioSourcesChanged(object? sender, IReadOnlyList<AudioSourceSnapshot> sources)
        => Dispatch(() => { _windowsSources = sources; Reconcile(); });
    private void BrowserTabsChanged(object? sender, IReadOnlyList<BrowserTabSource> tabs)
        => Dispatch(() => { _browserTabs = tabs; BrowserStatus = _bridge.IsConnected ? $"已连接 · {tabs.Count} 个标签页" : "等待扩展连接";
            Raise(nameof(ChromeConnectionStatus)); Raise(nameof(EdgeConnectionStatus)); Raise(nameof(BrowserStatusVisibility)); Reconcile(); });

    private void Reconcile()
    {
        var all = _windowsSources.Concat(_browserTabs.Select(ToSnapshot))
            .Where(source => _settings.ShowInactiveSessions || source.State == AudioPlaybackState.Active)
            .OrderByDescending(source => source.State == AudioPlaybackState.Active)
            .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var liveIds = all.Select(source => source.Id).ToHashSet();
        foreach (var stale in _byId.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            var vm = _byId[stale]; Sources.Remove(vm); vm.Dispose(); _byId.Remove(stale); _profileValuesApplied.Remove(stale);
        }
        foreach (var source in all)
        {
            if (_byId.TryGetValue(source.Id, out var existing)) existing.Update(source);
            else
            {
                var vm = new AudioSourceViewModel(source, _audio, _bridge, _profiles, () => _settings, _logger, OutputDevices,
                    () => _restoring, SaveProfileAsync, RestoreProfileAsync, RouteOutputDeviceAsync);
                _byId[source.Id] = vm; Sources.Add(vm);
            }
        }
        var liveApplications = all.Select(AudioApplicationInstanceKey.For).ToHashSet();
        TrackAndPruneApplications(liveApplications);
        if (_restoring || !_settings.RememberProfiles || !_settings.AutoApplyProfiles) return;
        foreach (var source in all)
        {
            var browserTab = source.Kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab;
            // An origin profile is only an initial default for a newly discovered browser tab. Mark the
            // tab evaluated even when no profile exists yet, so saving tab A cannot mutate an already-live
            // same-origin tab B on a later level/output update.
            if (browserTab && !_profileValuesApplied.Add(source.Id)) continue;
            if (!_savedProfiles.TryGetValue(ProfileKeys.For(source), out var profile) || !profile.AutoApply ||
                (!browserTab && !_profileValuesApplied.Add(source.Id))) continue;
            var application = AudioApplicationInstanceKey.For(source);
            var userIntent = _applicationRoutes.TryGetValue(application, out var intent) && intent.HasUserIntent;
            var effectiveProfile = userIntent
                ? profile with { OutputDeviceId = intent!.EndpointId, OutputDeviceName = intent.EndpointName }
                : profile;
            var applyRoute = !userIntent && _profilesApplied.Add(application);
            _ = ApplyProfileSafelyAsync(_byId[source.Id], effectiveProfile, applyRoute, true);
        }
    }

    private void TrackAndPruneApplications(IReadOnlySet<AudioApplicationInstanceKey> liveApplications)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var application in liveApplications) _applicationLastSeen[application] = now;
        foreach (var application in _applicationLastSeen.Keys.Where(application => !liveApplications.Contains(application)).ToArray())
        {
            if (!_applicationLastSeen.TryGetValue(application, out var lastSeen) ||
                now - lastSeen < TimeSpan.FromSeconds(5) || IsProcessStillAlive(application)) continue;
            if (_applicationRoutes.TryGetValue(application, out var intent) && !intent.Operation.IsCompleted) continue;
            _applicationLastSeen.TryRemove(application, out _);
            _profilesApplied.Remove(application);
            if (_applicationRoutes.TryRemove(application, out var removed)) removed.Dispose();
        }
    }

    private static bool IsProcessStillAlive(AudioApplicationInstanceKey application)
    {
        if (application.ProcessStartTimeUtc is null || application.ProcessId == 0) return false;
        try
        {
            using var process = Process.GetProcessById(checked((int)application.ProcessId));
            if (process.HasExited) return false;
            try
            {
                return Math.Abs((process.StartTime.ToUniversalTime() - application.ProcessStartTimeUtc.Value.UtcDateTime).TotalSeconds) < 2;
            }
            catch { return true; }
        }
        catch { return false; }
    }

    private static AudioSourceSnapshot ToSnapshot(BrowserTabSource tab)
    {
        var kind = tab.Browser == "edge" ? AudioSourceKind.EdgeTab : AudioSourceKind.ChromeTab;
        var browser = tab.Browser == "edge" ? "Edge" : "Chrome";
        var routingSupported = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion;
        var equalizerSupported = tab.ProtocolVersion >= BrowserProtocol.Version;
        var limitation = routingSupported
            ? tab.OutputStatus is not null && (tab.OutputStatus.Contains("失败", StringComparison.Ordinal) || tab.OutputStatus.Contains("不可用", StringComparison.Ordinal))
                ? tab.OutputStatus
                : equalizerSupported ? null : "扩展仍可控制音量和输出，但音效不可用；请在扩展页重新加载当前版本。"
            : "扩展正在使用旧协议 1；请在扩展页重新加载当前版本以启用 200% 增益和输出设备选择。";
        return new AudioSourceSnapshot(tab.Id, kind, $"{browser} · {tab.Title}", tab.Origin, 0, null, "browser",
            tab.Origin, tab.Id.Value, tab.CaptureState == "active" ? AudioPlaybackState.Active : AudioPlaybackState.Inactive,
            tab.Volume, tab.Muted, tab.Balance, tab.Peak, [1, 1],
            new AudioSourceCapabilities(true, true, true, 2, true, true, true, limitation,
                SupportsExtendedGain: routingSupported, SupportsOutputRouting: routingSupported, SupportsDeviceHotSwitch: routingSupported,
                SupportsEqualizer: equalizerSupported),
            DateTimeOffset.UtcNow, tab.OutputDeviceId, tab.OutputDeviceName,
            routingSupported ? AudioProcessingMode.Advanced : AudioProcessingMode.Unavailable,
            RequestedOutputDeviceId: tab.OutputDeviceId,
            RequestedOutputDeviceName: tab.OutputDeviceName,
            EffectiveOutputDeviceId: tab.EffectiveOutputDeviceId,
            EffectiveOutputDeviceName: tab.EffectiveOutputDeviceName,
            RoutingState: tab.RoutingState,
            RoutingError: tab.RoutingError,
            Effects: tab.Effects);
    }

    private void ReplaceOutputDevices(IReadOnlyList<OutputDeviceInfo> devices)
    {
        var normalized = devices.Count == 0 || !devices[0].IsSystemDefault
            ? new[] { OutputDeviceInfo.SystemDefault }.Concat(devices).ToArray()
            : devices;
        SynchronizeOutputDevices(OutputDevices, normalized);
        foreach (var source in Sources) source.UpdateOutputDevices(OutputDevices);
    }

    private static void SynchronizeOutputDevices(ObservableCollection<OutputDeviceInfo> target,
        IReadOnlyList<OutputDeviceInfo> desired)
    {
        for (var index = target.Count - 1; index >= 0; index--)
            if (desired.All(device => device.Id != target[index].Id)) target.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var existingIndex = -1;
            for (var candidate = 0; candidate < target.Count; candidate++)
                if (target[candidate].Id == desired[index].Id) { existingIndex = candidate; break; }
            if (existingIndex < 0) target.Insert(index, desired[index]);
            else
            {
                if (target[existingIndex] != desired[index]) target[existingIndex] = desired[index];
                if (existingIndex != index) target.Move(existingIndex, index);
            }
        }
    }

    private async Task ClearProfilesAsync()
    {
        await Task.WhenAll(Sources.Select(source => source.CancelPendingChangesAsync()));
        await CancelApplicationRoutesAsync();
        await _profiles.ClearAsync();
        _savedProfiles.Clear();
        _profilesApplied.Clear();
        _profileValuesApplied.Clear();
    }

    private async Task ConfirmClearProfilesAsync()
    {
        if (System.Windows.MessageBox.Show("清除全部已保存的应用音量、平衡和输出设备配置？", "Audio Source Mixer",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        await ClearProfilesAsync();
    }

    private async Task SaveProfileAsync(AudioSourceProfile profile, CancellationToken cancellationToken)
    {
        if (_restoring || !_settings.RememberProfiles) return;
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_restoring || !_settings.RememberProfiles) return;
            await _profiles.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
            _savedProfiles[profile.StableKey] = profile;
        }
        finally { _profileGate.Release(); }
    }

    private async Task RestoreProfileAsync(AudioSourceViewModel requested, CancellationToken cancellationToken)
    {
        if (_restoring) return;
        var siblings = requested.Snapshot.Kind == AudioSourceKind.WindowsSession
            ? Sources.Where(source => source.StableProfileKey == requested.StableProfileKey).ToArray()
            : [requested];
        await Task.WhenAll(siblings.Select(source => source.CancelPendingChangesAsync()));
        if (requested.Snapshot.Kind == AudioSourceKind.WindowsSession)
            foreach (var application in siblings.GroupBy(source => source.ApplicationInstanceKey))
            {
                if (_applicationRoutes.TryRemove(application.Key, out var intent))
                {
                    intent.Cancel();
                    try { await intent.Operation; } catch (OperationCanceledException) { }
                    intent.Dispose();
                }
                await _audio.RestoreAsync(application.First().Id, cancellationToken);
            }
        else
            await _bridge.SetAudioAsync(requested.Id, 1, 0, false, "", null, OutputDevices.ToArray(), cancellationToken,
                effects: EqualizerCatalog.Off);
        await _profiles.RemoveAsync(requested.StableProfileKey, cancellationToken);
        _savedProfiles.TryRemove(requested.StableProfileKey, out _);
        foreach (var sibling in siblings)
        {
            _profilesApplied.Remove(sibling.ApplicationInstanceKey);
            _profileValuesApplied.Remove(sibling.Id);
            Dispatch(sibling.ResetDisplayToDefaults);
        }
    }

    private async Task ApplyProfileSafelyAsync(AudioSourceViewModel source, AudioSourceProfile profile, bool applyRoute,
        bool applyOutputPreference)
    {
        try
        {
            await source.ApplyProfileAsync(profile, false, applyOutputPreference).ConfigureAwait(false);
            if (applyRoute && source.Snapshot.Kind == AudioSourceKind.WindowsSession && profile.OutputDeviceId is not null)
            {
                var device = new OutputDeviceInfo(profile.OutputDeviceId,
                    profile.OutputDeviceName ?? OutputDeviceInfo.SystemDefault.Name);
                await RouteOutputDeviceAsync(source, device, AudioRouteRequestSource.ProfileRestore, null).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_restoring) { }
        catch (Exception exception) { Error(exception); }
    }

    private Task RouteOutputDeviceAsync(AudioSourceViewModel source, OutputDeviceInfo device)
        => RouteOutputDeviceAsync(source, device, AudioRouteRequestSource.User,
            source.CreateProfileForOutput(device));

    private Task RouteOutputDeviceAsync(AudioSourceViewModel source, OutputDeviceInfo device,
        AudioRouteRequestSource requestSource, AudioSourceProfile? profile)
    {
        if (_restoring || source.Snapshot.Kind != AudioSourceKind.WindowsSession) return Task.CompletedTask;
        if (_audio is not IAudioRoutingController routing)
            return Task.FromException(new NotSupportedException("Windows per-app routing controller is unavailable."));

        var application = source.ApplicationInstanceKey;
        var intent = _applicationRoutes.GetOrAdd(application, _ => new ApplicationRouteIntent());
        CancellationTokenSource cancellation;
        CancellationTokenSource? previous;
        int generation;
        lock (intent.Sync)
        {
            if (requestSource != AudioRouteRequestSource.User && intent.HasUserIntent)
            {
                _logger.Info($"Suppressed {requestSource} route for {application}; a user route intent is authoritative.");
                return Task.CompletedTask;
            }
            if (!intent.Operation.IsCompleted && RoutePriority(requestSource) < RoutePriority(intent.RequestSource))
            {
                _logger.Info($"Suppressed lower-priority {requestSource} route for {application}; active source={intent.RequestSource}.");
                return Task.CompletedTask;
            }
            previous = intent.Cancellation;
            cancellation = new CancellationTokenSource();
            intent.Cancellation = cancellation;
            intent.Generation++;
            generation = intent.Generation;
            intent.RequestSource = requestSource;
            intent.EndpointId = device.Id;
            intent.EndpointName = device.IsSystemDefault ? null : device.Name;
            if (requestSource == AudioRouteRequestSource.User) intent.HasUserIntent = true;
            _profilesApplied.Add(application);
            foreach (var sibling in Sources.Where(item => item.ApplicationInstanceKey == application))
                sibling.SetPreferredOutputDevice(intent.EndpointId, intent.EndpointName);
            intent.Operation = ExecuteApplicationRouteAsync(routing, source.Id, application, intent,
                generation, device, requestSource, profile, cancellation);
        }
        previous?.Cancel();
        return intent.Operation;
    }

    private async Task ExecuteApplicationRouteAsync(IAudioRoutingController routing, AudioSourceId sourceId,
        AudioApplicationInstanceKey application, ApplicationRouteIntent intent, int generation,
        OutputDeviceInfo device, AudioRouteRequestSource requestSource, AudioSourceProfile? profile,
        CancellationTokenSource cancellation)
    {
        try
        {
            _logger.Info($"Application route requested. Application={application}; Source={sourceId}; Generation={generation}; RequestSource={requestSource}; Endpoint={device.Id}.");
            var result = await routing.SetOutputDeviceAsync(sourceId, device.Id, requestSource, cancellation.Token)
                .ConfigureAwait(false);
            if (result.State == AudioRoutingState.Failed)
                throw new InvalidOperationException(result.Error ?? "The audio route backend reported failure.");
            lock (intent.Sync)
                if (intent.Generation != generation) return;
            if (requestSource == AudioRouteRequestSource.User && profile is not null)
                await SaveProfileAsync(profile, cancellation.Token).ConfigureAwait(false);
            _logger.Info($"Application route completed. Application={application}; Generation={generation}; RequestSource={requestSource}; State={result.State}; Requested={result.RequestedOutputDeviceId}; Effective={result.EffectiveOutputDeviceId}.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _logger.Info($"Application route superseded. Application={application}; Generation={generation}; RequestSource={requestSource}.");
        }
        catch (Exception exception) { Error(exception); }
        finally
        {
            lock (intent.Sync)
                if (intent.Generation == generation && ReferenceEquals(intent.Cancellation, cancellation))
                    intent.Cancellation = null;
            cancellation.Dispose();
        }
    }

    private static int RoutePriority(AudioRouteRequestSource source) => source switch
    {
        AudioRouteRequestSource.User => 3,
        AudioRouteRequestSource.DeviceReconnect => 2,
        AudioRouteRequestSource.ProfileRestore => 1,
        _ => 0
    };

    private async Task CancelApplicationRoutesAsync()
    {
        var intents = _applicationRoutes.Values.ToArray();
        foreach (var intent in intents) intent.Cancel();
        foreach (var intent in intents)
        {
            try { await intent.Operation.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Error(exception); }
            intent.Dispose();
        }
        _applicationRoutes.Clear();
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.LogsDirectory) { UseShellExecute = true });
    }

    private void OpenInstallDirectory()
        => Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true });

    private string BrowserConnectionText(string browser)
    {
        var status = _bridge.GetConnectionStatuses().FirstOrDefault(item => item.Browser == browser);
        var name = browser == "edge" ? "Edge" : "Chrome";
        return status is null ? $"{name}：未连接" : $"{name}：已连接 · 扩展 {status.ExtensionVersion ?? "版本未知"}";
    }

    private async Task ManageBrowserOutputsAsync(string browser)
    {
        try { await _bridge.OpenOutputManagerAsync(browser); }
        catch (IOException exception) { BrowserStatus = exception.Message; throw; }
    }

    private async Task ClearBrowserMappingsAsync(string browser)
    {
        var name = browser == "edge" ? "Edge" : "Chrome";
        if (System.Windows.MessageBox.Show($"清除当前 {name} 配置中的全部输出设备映射？", "Audio Source Mixer",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        await _bridge.ClearOutputMappingsAsync(browser);
        BrowserStatus = $"已要求 {name} 清除输出设备映射";
    }

    private async Task ResetAllAsync()
    {
        if (System.Windows.MessageBox.Show("恢复全部音频控制和应用设置为默认值？已保存的应用配置也会清除。", "Audio Source Mixer",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        _startup.SetEnabled(false, true);
        _settings = new ApplicationSettings();
        Raise(nameof(CloseToTray)); Raise(nameof(AutoApplyProfiles)); Raise(nameof(RememberProfiles));
        Raise(nameof(ShowInactiveSessions)); Raise(nameof(StartMinimizedToTray)); Raise(nameof(StartupEnabled));
        Raise(nameof(ShowOperationTips));
        Raise(nameof(AutoApplyProfilesEnabled));
        SaveSettings();
        await RestoreAllAsync();
    }

    private void SaveSettings()
    {
        var snapshot = _settings;
        lock (_settingsSaveSync)
            _settingsSaveTask = SaveSettingsAfterAsync(_settingsSaveTask, snapshot);
    }

    public bool TryConsumeTrayHint()
    {
        if (!_settings.ShowOperationTips || _settings.TrayHintShown) return false;
        _settings = _settings with { TrayHintShown = true };
        SaveSettings();
        return true;
    }

    private async Task SaveSettingsAfterAsync(Task previous, ApplicationSettings snapshot)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception exception) { Error(exception); }
        await _settingsStore.SaveAsync(snapshot).ConfigureAwait(false);
    }

    public Task FlushSettingsAsync()
    {
        lock (_settingsSaveSync) return _settingsSaveTask;
    }

    public async Task PrepareForExitAsync()
    {
        var sources = Sources.ToArray();
        await FlushSettingsAsync().ConfigureAwait(false);
        await CancelApplicationRoutesAsync().ConfigureAwait(false);
        await Task.WhenAll(sources.Select(source => source.CancelPendingChangesAsync())).ConfigureAwait(false);
    }

    public void LogWindowCloseDecision(bool allowClose)
        => _logger.Info($"Window close requested. AllowClose={allowClose}; CloseToTray={CloseToTray}; Decision={(allowClose ? "Close" : CloseToTray ? "HideToTray" : "RestoreAndExit")}.");
    private void Error(Exception exception) => _logger.Error("View model operation failed.", exception);
    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _discovery.SourcesChanged -= AudioSourcesChanged;
        _discovery.DefaultDeviceChanged -= DefaultDeviceChanged;
        _outputDeviceService.OutputDevicesChanged -= OutputDevicesChanged;
        _bridge.TabsChanged -= BrowserTabsChanged;
        foreach (var intent in _applicationRoutes.Values) { intent.Cancel(); intent.Dispose(); }
        _applicationRoutes.Clear();
        foreach (var source in Sources) source.Dispose();
    }

    private sealed class ApplicationRouteIntent : IDisposable
    {
        public object Sync { get; } = new();
        public int Generation { get; set; }
        public AudioRouteRequestSource RequestSource { get; set; } = AudioRouteRequestSource.ProfileRestore;
        public bool HasUserIntent { get; set; }
        public string EndpointId { get; set; } = string.Empty;
        public string? EndpointName { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public Task Operation { get; set; } = Task.CompletedTask;
        public void Cancel() { lock (Sync) Cancellation?.Cancel(); }
        public void Dispose() { lock (Sync) { Cancellation?.Dispose(); Cancellation = null; } }
    }
}
