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
using AudioSourceMixer.Desktop.Localization;
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
    private readonly IBrowserOnboardingService _browserOnboarding;
    private readonly LocalizationService _localization = LocalizationService.Current;
    private readonly Dictionary<AudioSourceId, AudioSourceViewModel> _byId = [];
    private readonly HashSet<string> _runtimeHiddenSourceIds = new(StringComparer.Ordinal);
    private readonly List<string> _runtimeManualSourceOrder = [];
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
    private string _deviceName = LocalizationService.Current["Dynamic.Initializing"];
    private string _browserStatus = LocalizationService.Current["Dynamic.WaitingExtension"];
    private bool _browserStatusSignificant;
    private string _selectedPage = "mixer";
    private bool _browserSetupRequested;
    private ApplicationSettings _settings = new();
    private bool _manualOrderInitialized;
    private bool _isHiddenSourcesPopupOpen;
    private bool _isSourceOrderPreviewActive;
    private bool _diagnosticSourcesActive;

    public MainViewModel(IAudioSourceDiscovery discovery, IAudioSourceController audio, IAudioOutputDeviceService outputDeviceService,
        BrowserBridgeServer bridge, IAudioProfileStore profiles,
        JsonApplicationSettingsStore settingsStore, RollingFileLogger logger, IStartupRegistrationService? startup = null)
    {
        _discovery = discovery; _audio = audio; _outputDeviceService = outputDeviceService;
        _bridge = bridge; _profiles = profiles; _settingsStore = settingsStore; _logger = logger;
        _startup = startup ?? new StartupRegistrationService();
        _browserOnboarding = new BrowserOnboardingService();
        _localization.CultureChanged += LocalizationChanged;
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
        NavigateMixerCommand = new RelayCommand(() => SelectPage("mixer"));
        NavigateBrowserSetupCommand = new RelayCommand(() => SelectPage("browser"));
        NavigateSettingsCommand = new RelayCommand(() => SelectPage("settings"));
        BeginBrowserSetupCommand = new RelayCommand(BeginBrowserSetup);
        DeferBrowserSetupCommand = new RelayCommand(() => CompleteBrowserOnboarding("later"));
        DisableBrowserGuidePromptCommand = new RelayCommand(() => CompleteBrowserOnboarding("never"));
        OpenEdgeExtensionsCommand = new RelayCommand(() => RunOnboardingAction(() => _browserOnboarding.OpenExtensionsPage("edge")));
        OpenChromeExtensionsCommand = new RelayCommand(() => RunOnboardingAction(() => _browserOnboarding.OpenExtensionsPage("chrome")));
        OpenExtensionDirectoryCommand = new RelayCommand(() => RunOnboardingAction(_browserOnboarding.OpenExtensionDirectory));
        CopyExtensionDirectoryCommand = new RelayCommand(() => RunOnboardingAction(_browserOnboarding.CopyExtensionDirectory));
        RecheckBrowserSetupCommand = new RelayCommand(RefreshBrowserGuideStatus);
        ResetSourceOrderCommand = new RelayCommand(ResetSourceOrder);
        RestoreAllHiddenCommand = new RelayCommand(RestoreAllHiddenSources);
    }

    public ObservableCollection<AudioSourceViewModel> Sources { get; } = [];
    public ObservableCollection<HiddenSourceViewModel> HiddenSources { get; } = [];
    public ObservableCollection<OutputDeviceInfo> OutputDevices { get; } = [];
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
        [new(LocalizationService.ChineseLanguage, "简体中文"), new(LocalizationService.EnglishLanguage, "English")];
    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }
    public string BrowserStatus { get => _browserStatus; private set { if (Set(ref _browserStatus, value)) Raise(nameof(BrowserStatusVisibility)); } }
    public Visibility BrowserStatusVisibility => _bridge.IsConnected || _browserTabs.Count > 0 || _browserStatusSignificant
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
    public ICommand NavigateMixerCommand { get; }
    public ICommand NavigateBrowserSetupCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand BeginBrowserSetupCommand { get; }
    public ICommand DeferBrowserSetupCommand { get; }
    public ICommand DisableBrowserGuidePromptCommand { get; }
    public ICommand OpenEdgeExtensionsCommand { get; }
    public ICommand OpenChromeExtensionsCommand { get; }
    public ICommand OpenExtensionDirectoryCommand { get; }
    public ICommand CopyExtensionDirectoryCommand { get; }
    public ICommand RecheckBrowserSetupCommand { get; }
    public ICommand ResetSourceOrderCommand { get; }
    public ICommand RestoreAllHiddenCommand { get; }
    public string SourceSortModeLabel => _localization["Dynamic.ManualOrder"];
    public bool IsRecentSortMode => false;
    public bool IsManualSortMode => true;
    public string HiddenSourcesLabel => _localization.Format("Dynamic.HiddenCount", HiddenSources.Count);
    public Visibility HiddenSourcesVisibility => HiddenSources.Count == 0 ? Visibility.Hidden : Visibility.Visible;
    public bool IsHiddenSourcesPopupOpen
    {
        get => _isHiddenSourcesPopupOpen;
        set
        {
            var allowed = value && HiddenSources.Count > 0 && IsMixerPageSelected;
            Set(ref _isHiddenSourcesPopupOpen, allowed);
        }
    }
    public bool IsMixerPageSelected => _selectedPage == "mixer";
    public bool IsBrowserSetupPageSelected => _selectedPage == "browser";
    public bool IsSettingsPageSelected => _selectedPage == "settings";
    public Visibility MixerPageVisibility => IsMixerPageSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BrowserSetupPageVisibility => IsBrowserSetupPageSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsPageVisibility => IsSettingsPageSelected ? Visibility.Visible : Visibility.Collapsed;
    public string ExtensionDirectoryText => _browserOnboarding.ExtensionDirectory;
    public string EdgeGuideStatus => BrowserGuideStatus("edge");
    public string ChromeGuideStatus => BrowserGuideStatus("chrome");
    public string NativeHostRegistrationStatus => _browserOnboarding.NativeHostRegistrationStatus;
    public bool IsFirstRunBrowserWelcome => string.IsNullOrWhiteSpace(_settings.OnboardingCompletedVersion) && !_settings.BrowserGuideDismissed;
    public bool StartupAvailable => _startup.IsAvailable;
    public bool StartupEnabled
    {
        get => _startup.IsEnabled;
        set
        {
            try { _startup.SetEnabled(value, StartMinimizedToTray); }
            catch (Exception exception) { SetBrowserStatus(exception.Message, true); Error(exception); }
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
    public string VersionText => _localization.Format("Dynamic.Version", typeof(MainViewModel).Assembly.GetName().Version?.ToString(3));
    public string DeploymentText => _localization[StartupAvailable ? "Dynamic.Installed" : "Dynamic.DevelopmentRun"];
    public string SelectedLanguage
    {
        get => _settings.Language;
        set
        {
            var normalized = LocalizationService.NormalizeLanguage(value);
            if (string.Equals(_settings.Language, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _settings = _settings with { Language = normalized, SchemaVersion = 8 };
            _localization.SetLanguage(normalized);
            Raise();
            SaveSettings();
        }
    }
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
    public bool HideBrowserAggregateSessions
    {
        get => _settings.HideBrowserAggregateSessions;
        set
        {
            if (value == _settings.HideBrowserAggregateSessions) return;
            _settings = _settings with
            {
                HideBrowserAggregateSessions = value,
                VisibleBrowserAggregates = []
            };
            Raise(); SaveSettings(); Reconcile();
        }
    }

    public void RequestBrowserSetup()
    {
        _browserSetupRequested = true;
        SelectPage("browser");
    }

    internal void SelectMixerForDiagnostics() => SelectPage("mixer");
    internal void SelectBrowserSetupForDiagnostics() => SelectPage("browser");
    internal void SelectSettingsForDiagnostics() => SelectPage("settings");
    internal ApplicationSettings SettingsForDiagnostics => _settings;

    public async Task InitializeAsync(IReadOnlyList<AudioSourceSnapshot>? diagnosticSources = null)
    {
        await LoadSettingsAsync();
        _diagnosticSourcesActive = diagnosticSources is { Count: > 0 };
        foreach (var (key, profile) in await _profiles.LoadAsync()) _savedProfiles[key] = profile;
        _discovery.SourcesChanged += AudioSourcesChanged;
        if (_discovery is IAudioSourceLevelDiscovery levelDiscovery)
            levelDiscovery.SourceLevelsChanged += SourceLevelsChanged;
        _discovery.DefaultDeviceChanged += DefaultDeviceChanged;
        _outputDeviceService.OutputDevicesChanged += OutputDevicesChanged;
        _bridge.TabsChanged += BrowserTabsChanged;
        _bridge.SourceLevelsChanged += SourceLevelsChanged;
        var device = await _discovery.InitializeAsync();
        DeviceName = device.Name;
        ReplaceOutputDevices(await _outputDeviceService.GetOutputDevicesAsync());
        _windowsSources = await _discovery.GetSourcesAsync();
        if (diagnosticSources is { Count: > 0 })
        {
            // Diagnostic UI must be deterministic and must not expose the user's real session names in screenshots.
            _windowsSources = diagnosticSources.ToArray();
            DeviceName = _localization["Dynamic.DiagnosticDefaultDevice"];
            SelectPage("mixer");
        }
        Reconcile();
    }

    public async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_settingsLoaded) return;
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        _localization.SetLanguage(_settings.Language);
        _runtimeManualSourceOrder.Clear();
        _runtimeManualSourceOrder.AddRange(_settings.ManualSourceOrder ?? []);
        _settings = _settings with { SourceSortMode = SourceSortModes.Manual, VisibleBrowserAggregates = [], SchemaVersion = 8 };
        _manualOrderInitialized = false;
        _settingsLoaded = true;
        Raise(nameof(CloseToTray));
        Raise(nameof(AutoApplyProfiles));
        Raise(nameof(RememberProfiles));
        Raise(nameof(ShowInactiveSessions));
        Raise(nameof(HideBrowserAggregateSessions));
        Raise(nameof(SourceSortModeLabel));
            Raise(nameof(IsRecentSortMode));
            Raise(nameof(IsManualSortMode));
        Raise(nameof(StartMinimizedToTray));
        Raise(nameof(ShowOperationTips));
        Raise(nameof(StartupEnabled));
        Raise(nameof(StartupAvailable));
        Raise(nameof(IsFirstRunBrowserWelcome));
        Raise(nameof(SelectedLanguage));
        if (_browserSetupRequested || IsFirstRunBrowserWelcome) SelectPage("browser");
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
                await ResetBrowserSourceAsync(tab.Id, EqualizerCatalog.Off);
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

    private void DefaultDeviceChanged(object? sender, OutputDeviceInfo device) => Dispatch(() =>
    {
        DeviceName = device.Name;
        foreach (var source in Sources.Where(item => item.Snapshot.Kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab))
            _ = source.RebindSystemDefaultAsync(device).ContinueWith(task =>
            {
                if (task.Exception is { } exception) Error(exception.GetBaseException());
            }, TaskScheduler.Default);
    });
    private void OutputDevicesChanged(object? sender, IReadOnlyList<OutputDeviceInfo> devices)
        => Dispatch(() => ReplaceOutputDevices(devices));
    private void AudioSourcesChanged(object? sender, IReadOnlyList<AudioSourceSnapshot> sources)
    {
        if (_diagnosticSourcesActive) return;
        Dispatch(() => { _windowsSources = sources; Reconcile(); });
    }
    private void SourceLevelsChanged(object? sender, IReadOnlyList<AudioSourceLevel> levels)
        => Dispatch(() =>
        {
            foreach (var level in levels)
                if (_byId.TryGetValue(level.Id, out var source)) source.UpdatePeak(level.Peak, level.ObservedAt);
        });
    private void BrowserTabsChanged(object? sender, IReadOnlyList<BrowserTabSource> tabs)
    {
        if (_diagnosticSourcesActive) return;
        Dispatch(() => { _browserTabs = tabs; SetBrowserStatus(_bridge.IsConnected
                ? _localization.Format("Dynamic.ConnectedTabs", tabs.Count)
                : _localization["Dynamic.WaitingExtension"], false);
            Raise(nameof(ChromeConnectionStatus)); Raise(nameof(EdgeConnectionStatus));
            Raise(nameof(ChromeGuideStatus)); Raise(nameof(EdgeGuideStatus));
            Raise(nameof(BrowserStatusVisibility)); Reconcile(); });
    }

    private void Reconcile(bool updateExistingSnapshots = true)
    {
        var discovered = _windowsSources.Concat(_browserTabs.Select(ToSnapshot)).ToArray();
        EnsureRuntimeManualOrder(discovered);
        var presentation = SourcePresentationPolicy.Apply(discovered, _settings,
            _runtimeHiddenSourceIds, _runtimeManualSourceOrder);
        var visible = presentation.Visible;
        var liveIds = discovered.Select(source => source.Id).ToHashSet();
        foreach (var stale in _byId.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        {
            var vm = _byId[stale]; Sources.Remove(vm); vm.Dispose(); _byId.Remove(stale); _profileValuesApplied.Remove(stale);
        }
        foreach (var source in discovered)
        {
            if (_byId.TryGetValue(source.Id, out var existing))
            {
                if (updateExistingSnapshots) existing.Update(source);
            }
            else
            {
                var vm = new AudioSourceViewModel(source, _audio, _bridge, _profiles, () => _settings, _logger, OutputDevices,
                    () => _restoring, SaveProfileAsync, RestoreProfileAsync, RouteOutputDeviceAsync,
                    HideSource, MoveSourceToTop, MoveSourceUp, MoveSourceDown, MoveSourceToBottom,
                    ResolvePhysicalSystemDefault);
                _byId[source.Id] = vm;
                if (source.Kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab &&
                    string.IsNullOrEmpty(source.RequestedOutputDeviceId))
                    _ = vm.RebindSystemDefaultAsync(ResolvePhysicalSystemDefault() ?? OutputDeviceInfo.SystemDefault)
                        .ContinueWith(task =>
                        {
                            if (task.Exception is { } exception) Error(exception.GetBaseException());
                        }, TaskScheduler.Default);
            }
        }
        var visibleIds = visible.Select(source => source.Id).ToHashSet();
        foreach (var source in Sources.Where(item => !visibleIds.Contains(item.Id)).ToArray()) Sources.Remove(source);
        foreach (var source in visible)
            if (!Sources.Contains(_byId[source.Id])) Sources.Add(_byId[source.Id]);
        if (!_isSourceOrderPreviewActive)
            SourceCollectionReconciler.Reorder(Sources, visible.Select(source => source.Id).ToArray(), source => source.Id);
        SynchronizeHiddenSources(presentation.Hidden);
        if (!_isSourceOrderPreviewActive) PersistCurrentManualOrderIfChanged();

        var liveApplications = discovered.Select(AudioApplicationInstanceKey.For).ToHashSet();
        TrackAndPruneApplications(liveApplications);
        if (_restoring || !_settings.RememberProfiles || !_settings.AutoApplyProfiles) return;
        foreach (var source in visible)
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

    private void EnsureRuntimeManualOrder(IReadOnlyList<AudioSourceSnapshot> sources)
    {
        var known = _runtimeManualSourceOrder.ToHashSet(StringComparer.Ordinal);
        var unseen = sources.Where(source => !known.Contains(source.Id.Value));
        if (!_manualOrderInitialized)
            unseen = unseen.OrderBy(source => source.Kind)
                .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(source => source.Id.Value, StringComparer.Ordinal);
        _runtimeManualSourceOrder.AddRange(unseen.Select(source => source.Id.Value));
        _manualOrderInitialized = true;
    }

    private void SynchronizeHiddenSources(IReadOnlyList<HiddenSourceDescriptor> hidden)
    {
        if (hidden.Count == 0) IsHiddenSourcesPopupOpen = false;
        HiddenSources.Clear();
        foreach (var descriptor in hidden)
            HiddenSources.Add(new HiddenSourceViewModel(descriptor, RestoreHiddenSource));
        Raise(nameof(HiddenSourcesLabel));
        Raise(nameof(HiddenSourcesVisibility));
    }

    internal void FlushPendingPresentationForDiagnostics()
        => Reconcile();

    private void HideSource(AudioSourceViewModel source)
    {
        _runtimeHiddenSourceIds.Add(source.Id.Value);
        if (source.Snapshot.Kind == AudioSourceKind.WindowsSession)
        {
            var hidden = (_settings.ManuallyHiddenSources ?? [])
                .Where(item => !string.Equals(item.SourceId, source.Id.Value, StringComparison.Ordinal))
                .Append(new HiddenSourceSetting(source.Id.Value, source.Snapshot.Kind, DateTimeOffset.UtcNow))
                .OrderByDescending(item => item.LastSeenUtc).Take(256).ToArray();
            _settings = _settings with { ManuallyHiddenSources = hidden };
            SaveSettings();
        }
        Reconcile(updateExistingSnapshots: false);
    }

    private void RestoreHiddenSource(HiddenSourceDescriptor descriptor)
    {
        IsHiddenSourcesPopupOpen = false;
        var id = descriptor.Source.Id;
        _runtimeHiddenSourceIds.Remove(id.Value);
        var hidden = (_settings.ManuallyHiddenSources ?? [])
            .Where(item => !string.Equals(item.SourceId, id.Value, StringComparison.Ordinal)).ToArray();
        if (hidden.Length != (_settings.ManuallyHiddenSources?.Count ?? 0))
        {
            _settings = _settings with { ManuallyHiddenSources = hidden, VisibleBrowserAggregates = [] };
            SaveSettings();
        }
        Reconcile(updateExistingSnapshots: false);
    }

    private void RestoreAllHiddenSources()
    {
        IsHiddenSourcesPopupOpen = false;
        _runtimeHiddenSourceIds.Clear();
        _settings = _settings with { ManuallyHiddenSources = [], VisibleBrowserAggregates = [] };
        SaveSettings();
        Reconcile(updateExistingSnapshots: false);
    }

    private void ResetSourceOrder()
    {
        var ordered = _windowsSources.Concat(_browserTabs.Select(ToSnapshot))
            .OrderBy(source => source.Kind)
            .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(source => source.Id.Value, StringComparer.Ordinal)
            .Select(source => source.Id.Value).ToArray();
        _runtimeManualSourceOrder.Clear();
        _runtimeManualSourceOrder.AddRange(ordered);
        _settings = _settings with
        {
            SourceSortMode = SourceSortModes.Manual,
            ManualSourceOrder = ordered.Where(IsPersistablePresentationId).ToArray()
        };
        RaiseSortProperties();
        SaveSettings();
        Reconcile(updateExistingSnapshots: false);
    }

    private void RaiseSortProperties()
    {
        Raise(nameof(SourceSortModeLabel));
        Raise(nameof(IsRecentSortMode));
        Raise(nameof(IsManualSortMode));
    }

    private void MoveSourceToTop(AudioSourceViewModel source) => MoveSource(source, 0);
    private void MoveSourceUp(AudioSourceViewModel source) => MoveSource(source, Math.Max(0, Sources.IndexOf(source) - 1));
    private void MoveSourceDown(AudioSourceViewModel source) => MoveSource(source, Math.Min(Sources.Count - 1, Sources.IndexOf(source) + 1));
    private void MoveSourceToBottom(AudioSourceViewModel source) => MoveSource(source, Sources.Count - 1);

    private void MoveSource(AudioSourceViewModel source, int targetIndex)
    {
        var current = Sources.IndexOf(source);
        if (current < 0 || Sources.Count == 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, Sources.Count - 1);
        if (current != targetIndex) Sources.Move(current, targetIndex);
        PersistCurrentManualOrderIfChanged(forceSave: true);
    }

    internal void MoveSourceBefore(AudioSourceViewModel source, AudioSourceViewModel target)
    {
        var targetIndex = Sources.IndexOf(target);
        if (targetIndex < 0) return;
        MoveSource(source, targetIndex);
    }

    internal void MoveSourceToInsertionIndex(AudioSourceViewModel source, int insertionIndex)
    {
        var currentIndex = Sources.IndexOf(source);
        if (currentIndex < 0) return;
        insertionIndex = Math.Clamp(insertionIndex, 0, Sources.Count);
        var finalIndex = currentIndex < insertionIndex ? insertionIndex - 1 : insertionIndex;
        MoveSource(source, Math.Clamp(finalIndex, 0, Sources.Count - 1));
    }

    internal void BeginSourceOrderPreview()
    {
        IsHiddenSourcesPopupOpen = false;
        _isSourceOrderPreviewActive = true;
        _logger.Info($"Session drag preview started. Sources={Sources.Count}.");
    }

    internal void CommitSourceOrderPreview(bool changed)
    {
        if (!_isSourceOrderPreviewActive) return;
        _isSourceOrderPreviewActive = false;
        if (changed) PersistCurrentManualOrderIfChanged(forceSave: true);
        _logger.Info($"Session drag preview committed. Changed={changed}; Sources={Sources.Count}.");
    }

    internal void CancelSourceOrderPreview()
    {
        if (!_isSourceOrderPreviewActive) return;
        _isSourceOrderPreviewActive = false;
        _logger.Info($"Session drag preview cancelled. Sources={Sources.Count}.");
    }

    private void PersistCurrentManualOrderIfChanged(bool forceSave = false)
    {
        var visible = Sources.Select(source => source.Id.Value).ToList();
        var hidden = _runtimeManualSourceOrder.Where(id => !visible.Contains(id, StringComparer.Ordinal));
        var desired = visible.Concat(hidden).Distinct(StringComparer.Ordinal).Take(256).ToArray();
        _runtimeManualSourceOrder.Clear();
        _runtimeManualSourceOrder.AddRange(desired);
        var persisted = desired.Where(IsPersistablePresentationId).ToArray();
        if (!forceSave && persisted.SequenceEqual(_settings.ManualSourceOrder ?? [], StringComparer.Ordinal)) return;
        _settings = _settings with { ManualSourceOrder = persisted };
        SaveSettings();
    }

    private static bool IsPersistablePresentationId(string id)
        => id.StartsWith("win:", StringComparison.Ordinal);

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

    private AudioSourceSnapshot ToSnapshot(BrowserTabSource tab)
    {
        var kind = tab.Browser == "edge" ? AudioSourceKind.EdgeTab : AudioSourceKind.ChromeTab;
        var browser = tab.Browser == "edge" ? "Edge" : "Chrome";
        var domain = Uri.TryCreate(tab.Origin, UriKind.Absolute, out var originUri)
            ? originUri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)
            : tab.Origin;
        var title = string.IsNullOrWhiteSpace(tab.Title) ? domain : tab.Title.Trim();
        var routingSupported = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion;
        var equalizerSupported = tab.ProtocolVersion >= BrowserProtocol.Version;
        var limitation = routingSupported
            ? equalizerSupported ? null : _localization["Dynamic.BrowserEqUnavailable"]
            : _localization["Dynamic.BrowserProtocolOld"];
        return new AudioSourceSnapshot(tab.Id, kind, $"[{browser}] {title}", $"{domain} · {_localization["Common.BrowserEnhanced"]}", 0, null, "browser",
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
            Effects: tab.Effects,
            IconPath: _browserOnboarding.Detect(tab.Browser).ExecutablePath,
            FollowSystemDefault: tab.FollowSystemDefault,
            ResolvedOutputDeviceId: tab.ResolvedOutputDeviceId,
            ResolvedOutputDeviceName: tab.ResolvedOutputDeviceName);
    }

    private OutputDeviceInfo? ResolvePhysicalSystemDefault()
        => OutputDevices.FirstOrDefault(device => !device.IsSystemDefault && device.IsDefaultMultimedia && device.IsAvailable);

    private Task ResetBrowserSourceAsync(AudioSourceId id, AudioEffectSettings effects)
    {
        var resolved = ResolvePhysicalSystemDefault()
            ?? throw new InvalidOperationException(_localization["Dynamic.ResolveDefaultFailed"]);
        return _bridge.SetAudioAsync(id, 1, 0, false, "", null, OutputDevices.ToArray(),
            effects: effects, followSystemDefault: true,
            resolvedOutputDeviceId: resolved.Id, resolvedOutputDeviceName: resolved.Name);
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
        if (System.Windows.MessageBox.Show(_localization["Dynamic.ClearProfilesConfirm"], _localization["Common.ProductName"],
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
        {
            var resolved = ResolvePhysicalSystemDefault()
                ?? throw new InvalidOperationException(_localization["Dynamic.ResolveDefaultFailed"]);
            await _bridge.SetAudioAsync(requested.Id, 1, 0, false, "", null, OutputDevices.ToArray(), cancellationToken,
                effects: EqualizerCatalog.Off, followSystemDefault: true,
                resolvedOutputDeviceId: resolved.Id, resolvedOutputDeviceName: resolved.Name);
        }
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
        return status is null
            ? _localization.Format("Dynamic.ConnectionMissing", name)
            : _localization.Format("Dynamic.ConnectionReady", name, status.ExtensionVersion ?? _localization["Common.VersionUnknown"]);
    }

    private string BrowserGuideStatus(string browser)
    {
        var installation = _browserOnboarding.Detect(browser);
        if (!installation.IsInstalled) return _localization.Format("Dynamic.BrowserMissing", installation.DisplayName);
        var status = _bridge.GetConnectionStatuses().FirstOrDefault(item => item.Browser == browser);
        if (status is null) return _localization.Format("Dynamic.BrowserInstalledDisconnected", installation.DisplayName);
        var desktopVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);
        return !string.IsNullOrWhiteSpace(status.ExtensionVersion) && status.ExtensionVersion != desktopVersion
            ? _localization.Format("Dynamic.BrowserVersionMismatch", installation.DisplayName, status.ExtensionVersion, desktopVersion)
            : _localization.Format("Dynamic.ConnectionReady", installation.DisplayName,
                status.ExtensionVersion ?? _localization["Common.VersionUnknown"]);
    }

    private void SelectPage(string page)
    {
        if (page is not ("mixer" or "browser" or "settings")) throw new ArgumentOutOfRangeException(nameof(page));
        if (page != "mixer") IsHiddenSourcesPopupOpen = false;
        if (_selectedPage == page) return;
        _selectedPage = page;
        Raise(nameof(IsMixerPageSelected)); Raise(nameof(IsBrowserSetupPageSelected)); Raise(nameof(IsSettingsPageSelected));
        Raise(nameof(MixerPageVisibility)); Raise(nameof(BrowserSetupPageVisibility)); Raise(nameof(SettingsPageVisibility));
    }

    private void BeginBrowserSetup()
    {
        _settings = _settings with
        {
            BrowserOnboardingChoice = "setup-now",
            OnboardingCompletedVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            BrowserGuideDismissed = true,
            SchemaVersion = 8
        };
        Raise(nameof(IsFirstRunBrowserWelcome));
        SaveSettings();
        SelectPage("browser");
    }

    private void CompleteBrowserOnboarding(string choice)
    {
        _settings = _settings with
        {
            BrowserOnboardingChoice = choice,
            OnboardingCompletedVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            BrowserGuideDismissed = true,
            SchemaVersion = 8
        };
        Raise(nameof(IsFirstRunBrowserWelcome));
        SaveSettings();
        SelectPage("mixer");
    }

    private void RunOnboardingAction(Action action)
    {
        try { action(); }
        catch (Exception exception) { SetBrowserStatus(exception.Message, true); Error(exception); }
        finally { RefreshBrowserGuideStatus(); }
    }

    private void RefreshBrowserGuideStatus()
    {
        Raise(nameof(EdgeGuideStatus)); Raise(nameof(ChromeGuideStatus));
        Raise(nameof(NativeHostRegistrationStatus)); Raise(nameof(ExtensionDirectoryText));
    }

    private void SetBrowserStatus(string value, bool significant)
    {
        _browserStatusSignificant = significant;
        BrowserStatus = value;
        Raise(nameof(BrowserStatusVisibility));
    }

    private void LocalizationChanged(object? sender, EventArgs eventArgs) => Dispatch(() =>
    {
        if (!_browserStatusSignificant)
            SetBrowserStatus(_bridge.IsConnected
                ? _localization.Format("Dynamic.ConnectedTabs", _browserTabs.Count)
                : _localization["Dynamic.WaitingExtension"], false);
        if (_diagnosticSourcesActive) DeviceName = _localization["Dynamic.DiagnosticDefaultDevice"];
        Raise(nameof(SourceSortModeLabel));
        Raise(nameof(HiddenSourcesLabel));
        Raise(nameof(VersionText));
        Raise(nameof(DeploymentText));
        Raise(nameof(ChromeConnectionStatus));
        Raise(nameof(EdgeConnectionStatus));
        RefreshBrowserGuideStatus();
        foreach (var source in Sources) source.RefreshLocalization();
        foreach (var source in HiddenSources) source.RefreshLocalization();
    });

    private async Task ManageBrowserOutputsAsync(string browser)
    {
        try { await _bridge.OpenOutputManagerAsync(browser); }
        catch (IOException exception) { SetBrowserStatus(exception.Message, true); throw; }
    }

    private async Task ClearBrowserMappingsAsync(string browser)
    {
        var name = browser == "edge" ? "Edge" : "Chrome";
        if (System.Windows.MessageBox.Show(_localization.Format("Dynamic.ClearMappingsConfirm", name), _localization["Common.ProductName"],
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        await _bridge.ClearOutputMappingsAsync(browser);
        SetBrowserStatus(_localization.Format("Dynamic.ClearMappingsRequested", name), true);
    }

    private async Task ResetAllAsync()
    {
        if (System.Windows.MessageBox.Show(_localization["Dynamic.ResetAllConfirm"], _localization["Common.ProductName"],
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        _startup.SetEnabled(false, true);
        _runtimeHiddenSourceIds.Clear();
        _runtimeManualSourceOrder.Clear();
        _manualOrderInitialized = false;
        _settings = new ApplicationSettings(Language: SelectedLanguage);
        Raise(nameof(CloseToTray)); Raise(nameof(AutoApplyProfiles)); Raise(nameof(RememberProfiles));
        Raise(nameof(ShowInactiveSessions)); Raise(nameof(StartMinimizedToTray)); Raise(nameof(StartupEnabled));
        Raise(nameof(ShowOperationTips));
        Raise(nameof(HideBrowserAggregateSessions));
        Raise(nameof(AutoApplyProfilesEnabled));
        Raise(nameof(IsFirstRunBrowserWelcome));
        Raise(nameof(SelectedLanguage));
        RaiseSortProperties();
        SaveSettings();
        SelectPage("browser");
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
        _localization.CultureChanged -= LocalizationChanged;
        _discovery.SourcesChanged -= AudioSourcesChanged;
        if (_discovery is IAudioSourceLevelDiscovery levelDiscovery)
            levelDiscovery.SourceLevelsChanged -= SourceLevelsChanged;
        _discovery.DefaultDeviceChanged -= DefaultDeviceChanged;
        _outputDeviceService.OutputDevicesChanged -= OutputDevicesChanged;
        _bridge.TabsChanged -= BrowserTabsChanged;
        _bridge.SourceLevelsChanged -= SourceLevelsChanged;
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
