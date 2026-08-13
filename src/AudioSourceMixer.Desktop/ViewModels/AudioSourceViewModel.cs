using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using AudioSourceMixer.Core.Abstractions;
using AudioSourceMixer.Core.Browser;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;

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
    private readonly RollingFileLogger _logger;
    private readonly AsyncDebouncer _volumeDebouncer = new(TimeSpan.FromMilliseconds(60));
    private readonly AsyncDebouncer _balanceDebouncer = new(TimeSpan.FromMilliseconds(60));
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

    public AudioSourceViewModel(AudioSourceSnapshot snapshot, IAudioSourceController audio, BrowserBridgeServer bridge,
        IAudioProfileStore profiles, Func<ApplicationSettings> settings, RollingFileLogger logger,
        IEnumerable<OutputDeviceInfo>? outputDevices = null,
        Func<bool>? isRestoring = null,
        Func<AudioSourceProfile, CancellationToken, Task>? saveProfile = null,
        Func<AudioSourceViewModel, CancellationToken, Task>? restoreProfile = null,
        Func<AudioSourceViewModel, OutputDeviceInfo, Task>? routeOutputDevice = null)
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
        _volumePercent = snapshot.Volume * 100;
        _balancePercent = snapshot.Balance * 100;
        _muted = snapshot.Muted;
        _preferredOutputDeviceId = string.IsNullOrWhiteSpace(snapshot.RequestedOutputDeviceId) && snapshot.Kind != AudioSourceKind.WindowsSession
            ? snapshot.OutputDeviceId : snapshot.RequestedOutputDeviceId;
        _preferredOutputDeviceName = snapshot.RequestedOutputDeviceName ?? snapshot.OutputDeviceName;
        UpdateOutputDevices(outputDevices ?? [OutputDeviceInfo.SystemDefault]);
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync, Error);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, Error);
        StopCommand = new AsyncRelayCommand(() => _bridge.StopAsync(Id), Error);
        ReauthorizeOutputCommand = new AsyncRelayCommand(ReauthorizeOutputAsync, Error);
    }

    public AudioSourceId Id => _snapshot.Id;
    public string StableProfileKey => ProfileKeys.For(_snapshot);
    public AudioApplicationInstanceKey ApplicationInstanceKey => AudioApplicationInstanceKey.For(_snapshot);
    public string DisplayName => _snapshot.DisplayName;
    public string SourceDescription => _snapshot.SourceDescription;
    public string? Limitation => _snapshot.Capabilities.Limitation;
    public bool SupportsBalance => _snapshot.Capabilities.SupportsStereoBalance;
    public bool SupportsExtendedGain => _snapshot.Capabilities.SupportsExtendedGain;
    public bool SupportsOutputRouting => _snapshot.Capabilities.SupportsOutputRouting;
    public double VolumeMaximum => SupportsExtendedGain ? 200 : 100;
    public string VolumeScaleText => SupportsExtendedGain ? "0 ─── 100 基准 ─── 200" : "0 ───────── 100";
    public double PeakPercent => _snapshot.Peak * 100;
    public string StateText => _snapshot.State == AudioPlaybackState.Active ? "正在播放" : _snapshot.State == AudioPlaybackState.Expired ? "已失效" : "空闲";
    public Visibility StopVisibility => _snapshot.Kind == AudioSourceKind.WindowsSession ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EnhancedStatusVisibility => _snapshot.Kind == AudioSourceKind.WindowsSession ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ReauthorizeOutputVisibility => _snapshot.Kind != AudioSourceKind.WindowsSession &&
        _snapshot.RoutingState == AudioRoutingState.PendingAuthorization ? Visibility.Visible : Visibility.Collapsed;
    public string MuteLabel => _muted ? "取消静音" : "静音";
    public string BalanceText => _balancePercent switch { <= -99 => "仅左", < -5 => "偏左", >= 99 => "仅右", > 5 => "偏右", _ => "居中" };
    public string GainWarning => SupportsExtendedGain && _volumePercent > 100 ? "超过 100% 可能造成失真" : string.Empty;
    public string ProcessingModeText => _snapshot.Kind == AudioSourceKind.WindowsSession ? string.Empty :
        _snapshot.ProcessingMode == AudioProcessingMode.Advanced ? "浏览器标签页增强处理" : "增强功能不可用";
    public string RoutingGranularityText => _snapshot.Kind == AudioSourceKind.WindowsSession
        ? "输出设备按应用/进程生效"
        : "输出设备按浏览器标签页生效";
    public string OutputStatus => SupportsOutputRouting
        ? _snapshot.RoutingState switch
        {
            AudioRoutingState.PendingAuthorization => $"等待浏览器授权：{_preferredOutputDeviceName ?? _preferredOutputDeviceId}",
            AudioRoutingState.PendingStreamRestart => $"策略已设置；暂停/恢复播放或重开应用：{_preferredOutputDeviceName ?? "系统默认"}",
            AudioRoutingState.Partial => $"部分音频流已迁移：{_snapshot.RoutingError}",
            AudioRoutingState.Applied => $"已生效：{_snapshot.EffectiveOutputDeviceName ?? _snapshot.EffectiveOutputDeviceId}",
            AudioRoutingState.SystemDefault => $"系统默认；实际：{_snapshot.EffectiveOutputDeviceName ?? _snapshot.OutputDeviceName ?? "未知"}",
            AudioRoutingState.Disconnected => $"目标设备已断开，策略保留：{_preferredOutputDeviceName ?? _preferredOutputDeviceId}",
            AudioRoutingState.Failed => $"路由失败：{_snapshot.RoutingError}",
            _ => $"请求：{_preferredOutputDeviceName ?? "系统默认"}；实际：{_snapshot.EffectiveOutputDeviceName ?? "未知"}"
        }
        : _snapshot.Kind == AudioSourceKind.WindowsSession
            ? $"当前端点：{_snapshot.OutputDeviceName ?? "未知"}；按应用路由不可用"
            : "输出设备映射不可用";

    public ObservableCollection<OutputDeviceInfo> OutputDevices { get; } = [];
    public OutputDeviceInfo? SelectedOutputDevice => _selectedOutputDevice;
    public string SelectedOutputDeviceId => _preferredOutputDeviceId;
    public ICommand ToggleMuteCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ReauthorizeOutputCommand { get; }
    internal AudioSourceSnapshot Snapshot => _snapshot;

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (_updating || _isRestoring() || !Set(ref _volumePercent, Math.Clamp(value, 0, VolumeMaximum))) return;
            _lastUserChange = DateTimeOffset.UtcNow;
            Raise(nameof(GainWarning));
            _volumeDebouncer.Schedule(async token => { await ApplyAudioAsync(token); await SaveProfileAsync(token); });
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
            _balanceDebouncer.Schedule(async token => { await ApplyAudioAsync(token); await SaveProfileAsync(token); });
        }
    }

    public void Update(AudioSourceSnapshot snapshot)
    {
        _snapshot = snapshot;
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
                EnsurePreferredOutputDevice();
            }
            RaiseAllDisplayProperties();
        }
        finally { _updating = false; }
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
                if (SupportsOutputRouting && applyOutputPreference)
                {
                    _preferredOutputDeviceId = profile.OutputDeviceId ?? "";
                    _preferredOutputDeviceName = profile.OutputDeviceName;
                    UpdateOutputDevices(OutputDevices.ToArray());
                }
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
                await _bridge.SetAudioAsync(Id, (float)(_volumePercent / 100), (float)(_balancePercent / 100),
                    _muted, _preferredOutputDeviceId, _preferredOutputDeviceName, OutputDevices.ToArray(), current.Token,
                    AudioRouteRequestSource.User);
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
            SourceKind: _snapshot.Kind);

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
        await Task.WhenAll(_volumeDebouncer.CancelPendingAsync(), _balanceDebouncer.CancelPendingAsync());
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
            UpdateOutputDevices(OutputDevices.ToArray());
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
        else await _bridge.SetAudioAsync(Id, 1, 0, false, "", null, OutputDevices.ToArray());
        await _profiles.RemoveAsync(StableProfileKey);
        ResetDisplayToDefaults();
    }

    private Task ReauthorizeOutputAsync()
    {
        if (_snapshot.Kind == AudioSourceKind.WindowsSession || string.IsNullOrEmpty(_preferredOutputDeviceId))
            return Task.CompletedTask;
        return _bridge.SetAudioAsync(Id, (float)(_volumePercent / 100), (float)(_balancePercent / 100), _muted,
            _preferredOutputDeviceId, _preferredOutputDeviceName, OutputDevices.ToArray(), CancellationToken.None,
            AudioRouteRequestSource.User, forceAuthorization: true);
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
            await _bridge.SetAudioAsync(Id, AudioSourceMixer.Core.AudioMath.EnsureUserGain(volume), balance, _muted,
                _preferredOutputDeviceId, _preferredOutputDeviceName, OutputDevices.ToArray(), token);
        }
    }

    private async Task SaveProfileAsync(CancellationToken token)
    {
        if (_isRestoring() || !_settings().RememberProfiles) return;
        var profile = new AudioSourceProfile(StableProfileKey, (float)(_volumePercent / 100),
            (float)(_balancePercent / 100), _muted, OutputDeviceId: _preferredOutputDeviceId,
            OutputDeviceName: _preferredOutputDeviceName, SourceKind: _snapshot.Kind);
        if (_saveProfile is not null) await _saveProfile(profile, token);
        else await _profiles.SaveAsync(profile, token);
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
        Raise(nameof(VolumeScaleText)); Raise(nameof(PeakPercent)); Raise(nameof(StateText)); Raise(nameof(BalanceText));
        Raise(nameof(MuteLabel)); Raise(nameof(GainWarning)); Raise(nameof(ProcessingModeText)); Raise(nameof(OutputStatus));
        Raise(nameof(RoutingGranularityText)); Raise(nameof(EnhancedStatusVisibility));
        Raise(nameof(ReauthorizeOutputVisibility));
        Raise(nameof(VolumePercent)); Raise(nameof(BalancePercent)); Raise(nameof(SelectedOutputDevice));
        Raise(nameof(SelectedOutputDeviceId));
    }

    private void Error(Exception exception) => _logger.Error($"Audio source command failed for {Id}.", exception);

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
    }
}
