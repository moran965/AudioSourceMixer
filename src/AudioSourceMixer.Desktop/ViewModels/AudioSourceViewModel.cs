using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using AudioSourceMixer.Core.Abstractions;
using AudioSourceMixer.Core.Browser;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;
using AudioSourceMixer.Desktop.Services;
using System.Windows.Media;

namespace AudioSourceMixer.Desktop.ViewModels;

public sealed class AudioSourceViewModel : ObservableObject, IDisposable
{
    private readonly IAudioSourceController _audio;
    private readonly IAudioRoutingController? _routingAudio;
    private readonly BrowserBridgeServer _bridge;
    private readonly IAudioProfileStore _profiles;
    private readonly Func<ApplicationSettings> _settings;
    private readonly Func<bool> _isRestoring;
    private readonly Func<AudioSourceProfile, CancellationToken, Task>? _saveProfile;
    private readonly Func<AudioSourceViewModel, CancellationToken, Task>? _restoreProfile;
    private readonly Func<AudioSourceViewModel, OutputDeviceInfo, Task>? _routeOutputDevice;
    private readonly Action<AudioSourceViewModel>? _hideSource;
    private readonly Action<AudioSourceViewModel>? _moveToTop;
    private readonly Action<AudioSourceViewModel>? _moveUp;
    private readonly Action<AudioSourceViewModel>? _moveDown;
    private readonly Action<AudioSourceViewModel>? _moveToBottom;
    private readonly Func<OutputDeviceInfo?> _systemDefaultOutputDevice;
    private readonly RollingFileLogger _logger;
    private readonly AsyncDebouncer _volumeDebouncer = new(TimeSpan.FromMilliseconds(60));
    private readonly AsyncDebouncer _balanceDebouncer = new(TimeSpan.FromMilliseconds(60));
    private readonly AsyncDebouncer _equalizerDebouncer = new(TimeSpan.FromMilliseconds(60));
    private CancellationTokenSource? _routeCancellation;
    private Task _routeTask = Task.CompletedTask;
    private AudioSourceSnapshot _snapshot;
    private double _volumePercent;
    private double _balancePercent;
    private bool _muted;
    private bool _updating;
    private OutputDeviceInfo? _selectedOutputDevice;
    private string _preferredOutputDeviceId = "";
    private string? _preferredOutputDeviceName;
    private DateTimeOffset _lastUserChange;
    private AudioEffectSettings _effects;
    private bool _equalizerExpanded;
    private ImageSource _iconSource;
    private string? _iconCacheKey;
    private bool _isDragPlaceholder;

    public AudioSourceViewModel(AudioSourceSnapshot snapshot, IAudioSourceController audio, BrowserBridgeServer bridge,
        IAudioProfileStore profiles, Func<ApplicationSettings> settings, RollingFileLogger logger,
        IEnumerable<OutputDeviceInfo>? outputDevices = null,
        Func<bool>? isRestoring = null,
        Func<AudioSourceProfile, CancellationToken, Task>? saveProfile = null,
        Func<AudioSourceViewModel, CancellationToken, Task>? restoreProfile = null,
        Func<AudioSourceViewModel, OutputDeviceInfo, Task>? routeOutputDevice = null,
        Action<AudioSourceViewModel>? hideSource = null,
        Action<AudioSourceViewModel>? moveToTop = null,
        Action<AudioSourceViewModel>? moveUp = null,
        Action<AudioSourceViewModel>? moveDown = null,
        Action<AudioSourceViewModel>? moveToBottom = null,
        Func<OutputDeviceInfo?>? systemDefaultOutputDevice = null)
    {
        _snapshot = snapshot;
        _audio = audio;
        _routingAudio = audio as IAudioRoutingController;
        _bridge = bridge;
        _profiles = profiles;
        _settings = settings;
        _logger = logger;
        _isRestoring = isRestoring ?? (() => false);
        _saveProfile = saveProfile;
        _restoreProfile = restoreProfile;
        _routeOutputDevice = routeOutputDevice;
        _hideSource = hideSource;
        _moveToTop = moveToTop;
        _moveUp = moveUp;
        _moveDown = moveDown;
        _moveToBottom = moveToBottom;
        _systemDefaultOutputDevice = systemDefaultOutputDevice ?? (() => OutputDevices.FirstOrDefault(device => device.IsDefaultMultimedia && !device.IsSystemDefault));
        _volumePercent = snapshot.Volume * 100;
        _balancePercent = snapshot.Balance * 100;
        _muted = snapshot.Muted;
        _effects = EqualizerCatalog.Normalize(snapshot.Effects);
        _iconSource = ProcessIconProvider.Fallback(snapshot.Kind);
        _preferredOutputDeviceId = string.IsNullOrWhiteSpace(snapshot.RequestedOutputDeviceId) && snapshot.Kind != AudioSourceKind.WindowsSession
            ? snapshot.OutputDeviceId : snapshot.RequestedOutputDeviceId;
        _preferredOutputDeviceName = snapshot.RequestedOutputDeviceName ?? snapshot.OutputDeviceName;
        UpdateOutputDevices(outputDevices ?? [OutputDeviceInfo.SystemDefault]);
        foreach (var definition in EqualizerCatalog.Bands)
            EqualizerBands.Add(new EqualizerBandViewModel(definition, EqualizerBandChanged));
        SynchronizeEqualizerBands();
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync, Error);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, Error);
        StopCommand = new AsyncRelayCommand(() => _bridge.StopAsync(Id), Error);
        ReauthorizeOutputCommand = new AsyncRelayCommand(ReauthorizeOutputAsync, Error);
        ResetEqualizerCommand = new RelayCommand(ResetEqualizer);
        HideCommand = new RelayCommand(() => _hideSource?.Invoke(this));
        MoveToTopCommand = new RelayCommand(() => _moveToTop?.Invoke(this));
        MoveUpCommand = new RelayCommand(() => _moveUp?.Invoke(this));
        MoveDownCommand = new RelayCommand(() => _moveDown?.Invoke(this));
        MoveToBottomCommand = new RelayCommand(() => _moveToBottom?.Invoke(this));
        BeginIconLoad(snapshot);
    }

    public AudioSourceId Id => _snapshot.Id;
    public string StableProfileKey => ProfileKeys.For(_snapshot);
    public AudioApplicationInstanceKey ApplicationInstanceKey => AudioApplicationInstanceKey.For(_snapshot);
    public string DisplayName => _snapshot.DisplayName;
    public string SourceDescription => _snapshot.SourceDescription;
    public ImageSource IconSource => _iconSource;
    public string? Limitation => _snapshot.Capabilities.Limitation;
    public bool SupportsBalance => _snapshot.Capabilities.SupportsStereoBalance;
    public bool SupportsExtendedGain => _snapshot.Capabilities.SupportsExtendedGain;
    public bool SupportsOutputRouting => _snapshot.Capabilities.SupportsOutputRouting;
    public bool SupportsEqualizer => _snapshot.Capabilities.SupportsEqualizer;
    public string SourceTypeLabel => _snapshot.Kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab
        ? "浏览器增强" : "Windows 应用";
    public double VolumeMaximum => SupportsExtendedGain ? 200 : 100;
    public double PeakPercent => Math.Clamp(_snapshot.Peak, 0, 1) * 100;
    public double DragPlaceholderOpacity => _isDragPlaceholder ? 0.28 : 1;
    public Visibility StopVisibility => _snapshot.Kind == AudioSourceKind.WindowsSession ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EnhancedStatusVisibility => _snapshot.Kind == AudioSourceKind.WindowsSession ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ReauthorizeOutputVisibility => _snapshot.Kind != AudioSourceKind.WindowsSession &&
        _snapshot.RoutingState == AudioRoutingState.PendingAuthorization ? Visibility.Visible : Visibility.Collapsed;
    public string MuteLabel => _muted ? "取消静音" : "静音";
    public string BalanceText => _balancePercent switch { <= -99 => "仅左", < -5 => "偏左", >= 99 => "仅右", > 5 => "偏右", _ => "居中" };
    public string GainWarning => SupportsExtendedGain && _volumePercent > 100 ? "超过 100% 可能造成失真" : string.Empty;
    public string OutputStatus => SupportsOutputRouting
        ? _snapshot.RoutingState switch
        {
            AudioRoutingState.PendingAuthorization => _snapshot.FollowSystemDefault
                ? $"需要授权当前系统默认设备：{_snapshot.ResolvedOutputDeviceName ?? "未知设备"}"
                : $"等待浏览器授权：{_preferredOutputDeviceName ?? _preferredOutputDeviceId}",
            AudioRoutingState.PendingStreamRestart => $"策略已设置；暂停/恢复播放或重开应用：{_preferredOutputDeviceName ?? "系统默认"}",
            AudioRoutingState.Partial => $"部分音频流已迁移：{_snapshot.RoutingError}",
            AudioRoutingState.Applied when _snapshot.FollowSystemDefault =>
                $"选择：系统默认；当前解析：{_snapshot.ResolvedOutputDeviceName ?? "未知"}；实际生效：{_snapshot.EffectiveOutputDeviceName ?? "未知"}",
            AudioRoutingState.Applied => $"已生效：{_snapshot.EffectiveOutputDeviceName ?? _snapshot.EffectiveOutputDeviceId}",
            AudioRoutingState.SystemDefault => $"系统默认；实际：{_snapshot.EffectiveOutputDeviceName ?? _snapshot.OutputDeviceName ?? "未知"}",
            AudioRoutingState.Disconnected => $"目标设备已断开，策略保留：{_preferredOutputDeviceName ?? _preferredOutputDeviceId}",
            AudioRoutingState.Failed => $"路由失败：{_snapshot.RoutingError}",
            _ => $"请求：{_preferredOutputDeviceName ?? "系统默认"}；实际：{_snapshot.EffectiveOutputDeviceName ?? "未知"}"
        }
        : _snapshot.Kind == AudioSourceKind.WindowsSession
            ? $"当前端点：{_snapshot.OutputDeviceName ?? "未知"}；按应用路由不可用"
            : "输出设备映射不可用";
    public string UserStatusMessage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Limitation)) return Limitation!;
            if (_snapshot.State is AudioPlaybackState.Expired or AudioPlaybackState.Unavailable)
                return "该来源已失效，声音不可控制；请重新开始播放。";
            return _snapshot.RoutingState switch
            {
                AudioRoutingState.PendingAuthorization => $"需要授权“{_preferredOutputDeviceName ?? "所选设备"}”；当前声音仍使用原输出，请打开授权页试听并确认。",
                AudioRoutingState.PendingStreamRestart => "输出偏好已保存，但当前声音可能仍在原设备；请暂停后继续播放，或重开应用。",
                AudioRoutingState.Partial => "只有部分声音流切换成功；当前仍有声音，请暂停后继续播放或重开应用。",
                AudioRoutingState.Disconnected => $"“{_preferredOutputDeviceName ?? "所选设备"}”已断开；当前声音可能继续使用原设备，重新连接后会再次检查。",
                AudioRoutingState.Failed => $"输出设备切换失败；当前声音保持原路径。请重试或选择系统默认。{(string.IsNullOrWhiteSpace(_snapshot.RoutingError) ? "" : $" {_snapshot.RoutingError}")}",
                _ => string.Empty
            };
        }
    }

    internal void SetDragPlaceholder(bool value)
    {
        if (_isDragPlaceholder == value) return;
        _isDragPlaceholder = value;
        Raise(nameof(DragPlaceholderOpacity));
    }
    public Visibility UserStatusVisibility => string.IsNullOrWhiteSpace(UserStatusMessage) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GainWarningVisibility => string.IsNullOrWhiteSpace(GainWarning) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EqualizerVisibility => SupportsEqualizer ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = [];
    public IReadOnlyList<EqualizerPresetOption> EqualizerPresets { get; } = EqualizerCatalog.Presets
        .Select(preset => new EqualizerPresetOption(preset.Id, preset.Name)).ToArray();
    public string EqualizerSummary => !_effects.Enabled ? "音效 · 关闭" : $"音效 · {PresetName(_effects.PresetId)}";
    public string EqualizerHeadroomText => !_effects.Enabled ? "音效已旁路，不改变声音" :
        $"防削波余量：{EqualizerCatalog.EffectiveHeadroomDb(_effects):F1} dB（自动保护，不改变主音量数值）";
    public bool IsEqualizerExpanded { get => _equalizerExpanded; set => Set(ref _equalizerExpanded, value); }
    public bool IsEqualizerEnabled
    {
        get => _effects.Enabled;
        set
        {
            if (_updating || _isRestoring() || !SupportsEqualizer || value == _effects.Enabled) return;
            _effects = value ? EqualizerCatalog.CreatePreset(EqualizerCatalog.FlatPresetId, _effects.PreampDb) : EqualizerCatalog.Off;
            SynchronizeEqualizerBands();
            EqualizerChanged();
        }
    }
    public string SelectedEqualizerPresetId
    {
        get => _effects.PresetId;
        set
        {
            if (_updating || _isRestoring() || !SupportsEqualizer || string.IsNullOrWhiteSpace(value) || value == _effects.PresetId) return;
            _effects = value == EqualizerCatalog.CustomPresetId
                ? _effects with { Enabled = true, PresetId = EqualizerCatalog.CustomPresetId }
                : EqualizerCatalog.CreatePreset(value, _effects.PreampDb);
            SynchronizeEqualizerBands();
            EqualizerChanged();
        }
    }
    public double EqualizerPreampDb
    {
        get => _effects.PreampDb;
        set
        {
            if (_updating || _isRestoring() || !SupportsEqualizer) return;
            var clamped = (float)Math.Clamp(value, EqualizerCatalog.MinimumPreampDb, EqualizerCatalog.MaximumPreampDb);
            if (Math.Abs(clamped - _effects.PreampDb) < 0.001) return;
            _effects = _effects with { Enabled = true, PresetId = EqualizerCatalog.CustomPresetId, PreampDb = clamped };
            EqualizerChanged();
        }
    }

    public ObservableCollection<OutputDeviceInfo> OutputDevices { get; } = [];
    public OutputDeviceInfo? SelectedOutputDevice => _selectedOutputDevice;
    public string SelectedOutputDeviceId => _preferredOutputDeviceId;
    public ICommand ToggleMuteCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ReauthorizeOutputCommand { get; }
    public ICommand ResetEqualizerCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand MoveToTopCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand MoveToBottomCommand { get; }
    internal AudioSourceSnapshot Snapshot => _snapshot;
    internal AudioEffectSettings Effects => _effects;

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (_updating || _isRestoring() || !Set(ref _volumePercent, Math.Clamp(value, 0, VolumeMaximum))) return;
            _lastUserChange = DateTimeOffset.UtcNow;
            Raise(nameof(GainWarning));
            Raise(nameof(GainWarningVisibility));
            ScheduleAudioChange(_volumeDebouncer);
        }
    }

    public double BalancePercent
    {
        get => _balancePercent;
        set
        {
            if (_updating || _isRestoring() || !SupportsBalance || !Set(ref _balancePercent, Math.Clamp(value, -100, 100))) return;
            _lastUserChange = DateTimeOffset.UtcNow;
            Raise(nameof(BalanceText));
            ScheduleAudioChange(_balanceDebouncer);
        }
    }

    public void Update(AudioSourceSnapshot snapshot)
    {
        _snapshot = snapshot;
        BeginIconLoad(snapshot);
        _updating = true;
        try
        {
            if (DateTimeOffset.UtcNow - _lastUserChange > TimeSpan.FromMilliseconds(400))
            {
                Set(ref _volumePercent, Math.Clamp(snapshot.Volume * 100, 0, VolumeMaximum), nameof(VolumePercent));
                Set(ref _balancePercent, snapshot.Balance * 100, nameof(BalancePercent));
                Set(ref _muted, snapshot.Muted);
                _preferredOutputDeviceId = snapshot.RequestedOutputDeviceId;
                _preferredOutputDeviceName = snapshot.RequestedOutputDeviceName;
                if (SupportsEqualizer) _effects = EqualizerCatalog.Normalize(snapshot.Effects);
                EnsurePreferredOutputDevice();
                SynchronizeEqualizerBands();
            }
            RaiseAllDisplayProperties();
        }
        finally { _updating = false; }
    }

    public void UpdatePeak(float peak, DateTimeOffset observedAt)
    {
        var normalized = float.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
        if (Math.Abs(_snapshot.Peak - normalized) < 0.0001f) return;
        _snapshot = _snapshot with { Peak = normalized, ObservedAt = observedAt };
        Raise(nameof(PeakPercent));
    }

    public Task ApplyProfileAsync(AudioSourceProfile profile, bool applyRoute = true, bool applyOutputPreference = true)
    {
        var cancellation = BeginRouteOperation();
        var task = ApplyProfileCoreAsync(profile, applyRoute, applyOutputPreference, cancellation);
        Volatile.Write(ref _routeTask, task);
        return task;
    }

    private async Task ApplyProfileCoreAsync(AudioSourceProfile profile, bool applyRoute, bool applyOutputPreference,
        CancellationTokenSource cancellation)
    {
        try
        {
            _updating = true;
            try
            {
                _volumePercent = Math.Clamp(profile.Volume * 100, 0, VolumeMaximum);
                _balancePercent = profile.Balance * 100;
                _muted = profile.Muted;
                _effects = SupportsEqualizer ? EqualizerCatalog.Normalize(profile.Effects) : EqualizerCatalog.Off;
                if (SupportsOutputRouting && applyOutputPreference)
                {
                    _preferredOutputDeviceId = profile.OutputDeviceId ?? "";
                    _preferredOutputDeviceName = profile.OutputDeviceName;
                    UpdateOutputDevices(OutputDevices.ToArray());
                }
                SynchronizeEqualizerBands();
            }
            finally { _updating = false; }
            RaiseAllDisplayProperties();
            if (applyRoute && _snapshot.Kind == AudioSourceKind.WindowsSession && SupportsOutputRouting && _routingAudio is not null)
            {
                var route = await _routingAudio.SetOutputDeviceAsync(Id, _preferredOutputDeviceId,
                    AudioRouteRequestSource.ProfileRestore, cancellation.Token);
                if (route.State == AudioRoutingState.Failed) throw new InvalidOperationException(route.Error);
            }
            await ApplyAudioAsync(cancellation.Token);
        }
        finally { EndRouteOperation(cancellation); }
    }

    public Task UserSelectOutputDeviceAsync(OutputDeviceInfo device)
    {
        if (_updating || _isRestoring() || !SupportsOutputRouting || device.Id == _preferredOutputDeviceId)
            return Task.CompletedTask;
        _preferredOutputDeviceId = device.Id;
        _preferredOutputDeviceName = device.IsSystemDefault ? null : device.Name;
        Set(ref _selectedOutputDevice, device, nameof(SelectedOutputDevice));
        Raise(nameof(SelectedOutputDeviceId));
        _lastUserChange = DateTimeOffset.UtcNow;
        Raise(nameof(OutputStatus));
        if (_snapshot.Kind == AudioSourceKind.WindowsSession && _routeOutputDevice is not null)
            return _routeOutputDevice(this, device);
        var current = BeginRouteOperation();
        var task = UserSelectOutputDeviceCoreAsync(current);
        Volatile.Write(ref _routeTask, task);
        return task;
    }

    private async Task UserSelectOutputDeviceCoreAsync(CancellationTokenSource current)
    {
        try
        {
            if (_snapshot.Kind == AudioSourceKind.WindowsSession)
            {
                if (_routingAudio is null) throw new NotSupportedException("Windows per-app routing controller is unavailable.");
                var route = await _routingAudio.SetOutputDeviceAsync(Id, _preferredOutputDeviceId,
                    AudioRouteRequestSource.User, current.Token);
                if (route.State == AudioRoutingState.Failed) throw new InvalidOperationException(route.Error);
            }
            else
            {
                await SetBrowserAudioAsync((float)(_volumePercent / 100), (float)(_balancePercent / 100),
                    _muted, current.Token, AudioRouteRequestSource.User, false, _effects);
            }
            await SaveProfileAsync(current.Token);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception exception) { Error(exception); }
        finally { EndRouteOperation(current); }
    }

    internal AudioSourceProfile CreateProfileForOutput(OutputDeviceInfo device)
        => new(StableProfileKey, (float)(_volumePercent / 100), (float)(_balancePercent / 100), _muted,
            OutputDeviceId: device.Id, OutputDeviceName: device.IsSystemDefault ? null : device.Name,
            SourceKind: _snapshot.Kind, Effects: SupportsEqualizer ? _effects : null);

    internal void SetPreferredOutputDevice(string endpointId, string? endpointName)
    {
        _updating = true;
        try
        {
            _preferredOutputDeviceId = endpointId;
            _preferredOutputDeviceName = endpointName;
            EnsurePreferredOutputDevice();
        }
        finally { _updating = false; }
        Raise(nameof(SelectedOutputDeviceId));
        Raise(nameof(SelectedOutputDevice));
        Raise(nameof(OutputStatus));
    }

    public async Task CancelPendingChangesAsync()
    {
        var route = Interlocked.Exchange(ref _routeCancellation, null);
        route?.Cancel();
        var routeTask = Volatile.Read(ref _routeTask);
        try { await routeTask; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Error(exception); }
        await Task.WhenAll(_volumeDebouncer.CancelPendingAsync(), _balanceDebouncer.CancelPendingAsync(),
            _equalizerDebouncer.CancelPendingAsync());
    }

    public void ResetDisplayToDefaults()
    {
        _updating = true;
        try
        {
            _volumePercent = 100;
            _balancePercent = 0;
            _muted = false;
            _preferredOutputDeviceId = "";
            _preferredOutputDeviceName = null;
            _effects = EqualizerCatalog.Off;
            UpdateOutputDevices(OutputDevices.ToArray());
            SynchronizeEqualizerBands();
        }
        finally { _updating = false; }
        RaiseAllDisplayProperties();
    }

    private async Task ToggleMuteAsync()
    {
        if (_isRestoring()) return;
        _muted = !_muted;
        _lastUserChange = DateTimeOffset.UtcNow;
        Raise(nameof(MuteLabel));
        await ApplyAudioAsync(CancellationToken.None);
        await SaveProfileAsync(CancellationToken.None);
    }

    private async Task RestoreAsync()
    {
        await CancelPendingChangesAsync();
        if (_restoreProfile is not null)
        {
            await _restoreProfile(this, CancellationToken.None);
            return;
        }
        if (_snapshot.Kind == AudioSourceKind.WindowsSession) await _audio.RestoreAsync(Id);
        else await SetBrowserAudioAsync(1, 0, false, CancellationToken.None,
            AudioRouteRequestSource.User, false, EqualizerCatalog.Off, forceSystemDefault: true);
        await _profiles.RemoveAsync(StableProfileKey);
        ResetDisplayToDefaults();
    }

    private Task ReauthorizeOutputAsync()
    {
        if (_snapshot.Kind == AudioSourceKind.WindowsSession)
            return Task.CompletedTask;
        return SetBrowserAudioAsync((float)(_volumePercent / 100), (float)(_balancePercent / 100), _muted,
            CancellationToken.None, AudioRouteRequestSource.User, true, _effects);
    }

    private async Task ApplyAudioAsync(CancellationToken token)
    {
        var volume = (float)(_volumePercent / 100);
        var balance = (float)(_balancePercent / 100);
        if (_snapshot.Kind == AudioSourceKind.WindowsSession)
        {
            await _audio.SetVolumeAsync(Id, AudioSourceMixer.Core.AudioMath.EnsureSessionVolume(volume), token);
            await _audio.SetMuteAsync(Id, _muted, token);
            if (SupportsBalance) await _audio.SetBalanceAsync(Id, balance, token);
        }
        else
        {
            await SetBrowserAudioAsync(AudioSourceMixer.Core.AudioMath.EnsureUserGain(volume), balance, _muted,
                token, AudioRouteRequestSource.ProfileRestore, false, _effects);
        }
    }

    private async Task SaveProfileAsync(CancellationToken token)
    {
        if (_isRestoring() || !_settings().RememberProfiles) return;
        var profile = new AudioSourceProfile(StableProfileKey, (float)(_volumePercent / 100),
            (float)(_balancePercent / 100), _muted, OutputDeviceId: _preferredOutputDeviceId,
            OutputDeviceName: _preferredOutputDeviceName, SourceKind: _snapshot.Kind,
            Effects: SupportsEqualizer ? _effects : null);
        if (_saveProfile is not null) await _saveProfile(profile, token);
        else await _profiles.SaveAsync(profile, token);
    }

    private void ScheduleAudioChange(AsyncDebouncer debouncer)
    {
        debouncer.Schedule(async token =>
        {
            try
            {
                await ApplyAudioAsync(token);
                await SaveProfileAsync(token);
            }
            catch (KeyNotFoundException exception) when (_snapshot.Kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab)
            {
                _logger.Info($"Ignored a delayed browser control update after the source disappeared. Source={Id}; Detail={exception.Message}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Error(exception);
            }
        });
    }

    public void UpdateOutputDevices(IEnumerable<OutputDeviceInfo> devices)
    {
        var available = devices.ToList();
        if (available.All(device => !device.IsSystemDefault)) available.Insert(0, OutputDeviceInfo.SystemDefault);
        if (!string.IsNullOrEmpty(_preferredOutputDeviceId) && available.All(device => device.Id != _preferredOutputDeviceId))
            available.Add(new OutputDeviceInfo(_preferredOutputDeviceId,
                $"{_preferredOutputDeviceName ?? "所选设备"} (不可用)", IsAvailable: false));
        _updating = true;
        try
        {
            SynchronizeDevices(OutputDevices, available);
            _selectedOutputDevice = OutputDevices.First(device => device.Id == _preferredOutputDeviceId);
        }
        finally { _updating = false; }
        Raise(nameof(SelectedOutputDevice));
        Raise(nameof(SelectedOutputDeviceId));
        Raise(nameof(OutputStatus));
    }

    private void EnsurePreferredOutputDevice()
    {
        var selected = OutputDevices.FirstOrDefault(device => device.Id == _preferredOutputDeviceId);
        if (selected is null)
        {
            selected = new OutputDeviceInfo(_preferredOutputDeviceId,
                $"{_preferredOutputDeviceName ?? "Selected device"} (unavailable)", IsAvailable: false);
            OutputDevices.Add(selected);
        }
        _selectedOutputDevice = selected;
        Raise(nameof(SelectedOutputDeviceId));
        Raise(nameof(SelectedOutputDevice));
    }

    private static void SynchronizeDevices(ObservableCollection<OutputDeviceInfo> target,
        IReadOnlyList<OutputDeviceInfo> desired)
    {
        for (var index = target.Count - 1; index >= 0; index--)
            if (desired.All(device => device.Id != target[index].Id)) target.RemoveAt(index);

        for (var index = 0; index < desired.Count; index++)
        {
            var wanted = desired[index];
            var existingIndex = -1;
            for (var candidate = 0; candidate < target.Count; candidate++)
                if (target[candidate].Id == wanted.Id) { existingIndex = candidate; break; }
            if (existingIndex < 0) target.Insert(index, wanted);
            else
            {
                if (target[existingIndex] != wanted) target[existingIndex] = wanted;
                if (existingIndex != index) target.Move(existingIndex, index);
            }
        }
    }

    private void RaiseAllDisplayProperties()
    {
        Raise(nameof(DisplayName)); Raise(nameof(SourceDescription)); Raise(nameof(Limitation)); Raise(nameof(SupportsBalance));
        Raise(nameof(SupportsExtendedGain)); Raise(nameof(SupportsOutputRouting)); Raise(nameof(VolumeMaximum));
        Raise(nameof(SupportsEqualizer)); Raise(nameof(EqualizerVisibility));
        Raise(nameof(PeakPercent)); Raise(nameof(BalanceText));
        Raise(nameof(MuteLabel)); Raise(nameof(GainWarning)); Raise(nameof(GainWarningVisibility)); Raise(nameof(OutputStatus));
        Raise(nameof(UserStatusMessage)); Raise(nameof(UserStatusVisibility)); Raise(nameof(EnhancedStatusVisibility));
        Raise(nameof(ReauthorizeOutputVisibility));
        Raise(nameof(VolumePercent)); Raise(nameof(BalancePercent)); Raise(nameof(SelectedOutputDevice));
        Raise(nameof(SelectedOutputDeviceId));
        RaiseEqualizerProperties();
    }

    private void EqualizerBandChanged(EqualizerBandViewModel band)
    {
        if (_updating || _isRestoring() || !SupportsEqualizer) return;
        var bands = _effects.Bands.Select((item, index) => index == EqualizerBands.IndexOf(band)
            ? item with { GainDb = (float)band.GainDb }
            : item).ToArray();
        _effects = _effects with { Enabled = true, PresetId = EqualizerCatalog.CustomPresetId, Bands = bands };
        EqualizerChanged();
    }

    private void EqualizerChanged()
    {
        _effects = _effects with { UpdatedAt = DateTimeOffset.UtcNow };
        _lastUserChange = DateTimeOffset.UtcNow;
        RaiseEqualizerProperties();
        ScheduleAudioChange(_equalizerDebouncer);
    }

    private void ResetEqualizer()
    {
        if (!SupportsEqualizer || _updating || _isRestoring()) return;
        _effects = EqualizerCatalog.Off;
        SynchronizeEqualizerBands();
        EqualizerChanged();
    }

    private void SynchronizeEqualizerBands()
    {
        if (EqualizerBands.Count != EqualizerCatalog.Bands.Count) return;
        for (var index = 0; index < EqualizerBands.Count; index++)
            EqualizerBands[index].Synchronize(_effects.Bands[index].GainDb);
    }

    private void RaiseEqualizerProperties()
    {
        Raise(nameof(IsEqualizerEnabled)); Raise(nameof(SelectedEqualizerPresetId));
        Raise(nameof(EqualizerPreampDb)); Raise(nameof(EqualizerSummary)); Raise(nameof(EqualizerHeadroomText));
    }

    private static string PresetName(string presetId)
        => EqualizerCatalog.Presets.FirstOrDefault(preset => preset.Id == presetId)?.Name ?? "自定义";

    private void Error(Exception exception) => _logger.Error($"Audio source command failed for {Id}.", exception);

    private void BeginIconLoad(AudioSourceSnapshot snapshot)
    {
        var key = $"{snapshot.Kind}:{snapshot.IconPath}:{snapshot.ExecutablePath}";
        if (string.Equals(key, _iconCacheKey, StringComparison.Ordinal)) return;
        _iconCacheKey = key;
        _ = LoadIconAsync(snapshot, key);
    }

    private async Task LoadIconAsync(AudioSourceSnapshot snapshot, string key)
    {
        var icon = await ProcessIconProvider.GetAsync(snapshot).ConfigureAwait(false);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(key, _iconCacheKey, StringComparison.Ordinal)) return;
            _iconSource = icon;
            Raise(nameof(IconSource));
        });
    }

    internal Task RebindSystemDefaultAsync(OutputDeviceInfo device)
    {
        if (_snapshot.Kind == AudioSourceKind.WindowsSession || !string.IsNullOrEmpty(_preferredOutputDeviceId))
            return Task.CompletedTask;
        return SetBrowserAudioAsync((float)(_volumePercent / 100), (float)(_balancePercent / 100), _muted,
            CancellationToken.None, AudioRouteRequestSource.DeviceReconnect, false, _effects, resolvedDefault: device);
    }

    private Task SetBrowserAudioAsync(float volume, float balance, bool muted, CancellationToken cancellationToken,
        AudioRouteRequestSource requestSource, bool forceAuthorization, AudioEffectSettings effects,
        bool forceSystemDefault = false, OutputDeviceInfo? resolvedDefault = null)
    {
        var followSystemDefault = forceSystemDefault || string.IsNullOrEmpty(_preferredOutputDeviceId);
        resolvedDefault ??= followSystemDefault ? _systemDefaultOutputDevice() : null;
        if (followSystemDefault && (resolvedDefault is null || string.IsNullOrWhiteSpace(resolvedDefault.Id)))
            return Task.FromException(new InvalidOperationException("无法解析当前 Windows 默认多媒体输出设备。"));
        return _bridge.SetAudioAsync(Id, volume, balance, muted,
            followSystemDefault ? string.Empty : _preferredOutputDeviceId,
            followSystemDefault ? null : _preferredOutputDeviceName,
            OutputDevices.ToArray(), cancellationToken, requestSource, forceAuthorization, effects,
            followSystemDefault, resolvedDefault?.Id, resolvedDefault?.Name);
    }

    private CancellationTokenSource BeginRouteOperation()
    {
        var current = new CancellationTokenSource();
        Interlocked.Exchange(ref _routeCancellation, current)?.Cancel();
        return current;
    }

    private void EndRouteOperation(CancellationTokenSource current)
    {
        Interlocked.CompareExchange(ref _routeCancellation, null, current);
        current.Dispose();
    }

    public void Dispose()
    {
        _routeCancellation?.Cancel();
        _routeCancellation?.Dispose();
        _volumeDebouncer.Dispose();
        _balanceDebouncer.Dispose();
        _equalizerDebouncer.Dispose();
    }
}
