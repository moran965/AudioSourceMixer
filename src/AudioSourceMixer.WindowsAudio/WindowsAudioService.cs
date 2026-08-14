using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioSourceMixer.Core.Abstractions;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.WindowsAudio.Interop;

namespace AudioSourceMixer.WindowsAudio;

public sealed class WindowsAudioService : IAudioSourceDiscovery, IAudioSourceLevelDiscovery, IAudioSourceController, IAudioOutputDeviceService,
    IAudioRoutingController, IAsyncDisposable
{
    private readonly AudioWorker _worker = new();
    private readonly IRollbackJournal _journal;
    private readonly RollingFileLogger _logger;
    private readonly ApplicationRouteCoordinator _routes;
    private WindowsAppRoutingBackend? _routingBackend;
    private readonly Dictionary<AudioSourceId, SessionHandle> _sessions = [];
    private readonly Dictionary<AudioSourceId, AudioRollbackEntry> _rollback = [];
    private readonly Dictionary<string, EndpointContext> _endpoints = new(StringComparer.Ordinal);
    private IReadOnlyList<OutputDeviceInfo> _outputDevices = [OutputDeviceInfo.SystemDefault];
    private readonly Timer _refreshTimer;
    private readonly Timer _levelTimer;
    private readonly Timer _topologyTimer;
    private IMMDeviceEnumerator? _deviceEnumerator;
    private DeviceNotification? _deviceNotification;
    private OutputDeviceInfo? _currentDevice;
    private int _refreshQueued;
    private int _levelRefreshQueued;
    private int _deviceRefreshQueued;
    private bool _initialized;
    private bool _disposed;
    private readonly Dictionary<AudioSourceId, float> _smoothedPeaks = [];
    private readonly HashSet<AudioSourceId> _meterFailures = [];
    private long _lastLevelTimestamp = Stopwatch.GetTimestamp();

    public WindowsAudioService(IRollbackJournal journal, RollingFileLogger logger)
    {
        _journal = journal;
        _logger = logger;
        _routes = new ApplicationRouteCoordinator(message => _logger.Info(message));
        _routes.StateChanged += (_, result) =>
        {
            RoutingStateChanged?.Invoke(this, result);
            PublishSnapshots();
        };
        _refreshTimer = new Timer(_ => QueueRefresh(), null, Timeout.Infinite, Timeout.Infinite);
        _levelTimer = new Timer(_ => QueueLevelRefresh(), null, Timeout.Infinite, Timeout.Infinite);
        _topologyTimer = new Timer(_ => QueueTopologyRefresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<IReadOnlyList<AudioSourceSnapshot>>? SourcesChanged;
    public event EventHandler<IReadOnlyList<AudioSourceLevel>>? SourceLevelsChanged;
    public event EventHandler<OutputDeviceInfo>? DefaultDeviceChanged;
    public event EventHandler<IReadOnlyList<OutputDeviceInfo>>? OutputDevicesChanged;
    public event EventHandler<AudioRouteResult>? RoutingStateChanged;

    public async Task<OutputDeviceInfo> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pending = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        var device = await _worker.InvokeAsync(() =>
        {
            InitializeCore();
            RefreshCore();
            foreach (var entry in pending)
            {
                var restored = false;
                try
                {
                    if (entry.OriginalRoutes is { Count: > 0 } && _routingBackend is not null)
                    {
                        var appSession = _sessions.Values.FirstOrDefault(item => IsSafeApplicationMatch(entry.Identity, item.Identity));
                        if (appSession is not null)
                        {
                            _routingBackend.RestoreRoutes(entry.Identity.ProcessId, FromJournalRoutes(entry.OriginalRoutes));
                            restored = true;
                            _logger.Info($"Recovered per-app route for PID {entry.Identity.ProcessId} from rollback journal.");
                        }
                    }

                    var live = _sessions.Values.FirstOrDefault(item => entry.Identity.IsSafeRestoreMatch(item.Identity));
                    if (live is not null)
                    {
                        live.Restore(entry);
                        restored = true;
                    }
                    if (restored)
                    {
                        _journal.RemoveAsync(entry.SourceId).GetAwaiter().GetResult();
                        _logger.Info($"Recovered rollback entry for {entry.SourceId}.");
                    }
                }
                catch (Exception exception) { _logger.Error($"Failed recovering {entry.SourceId}.", exception); }
            }
            return _currentDevice!;
        }, cancellationToken).ConfigureAwait(false);

        _initialized = true;
        _refreshTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _lastLevelTimestamp = Stopwatch.GetTimestamp();
        _levelTimer.Change(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(75));
        PublishSnapshots();
        return device;
    }

    public Task<IReadOnlyList<AudioSourceSnapshot>> GetSourcesAsync(CancellationToken cancellationToken = default)
        => _worker.InvokeAsync<IReadOnlyList<AudioSourceSnapshot>>(() => _sessions.Values.Select(SafeSnapshot).Where(item => item is not null).Cast<AudioSourceSnapshot>().ToArray(), cancellationToken);

    public Task<IReadOnlyList<OutputDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
        => _worker.InvokeAsync<IReadOnlyList<OutputDeviceInfo>>(() => _outputDevices.ToArray(), cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized || _disposed) return;
        await _worker.InvokeAsync(() =>
        {
            RefreshCore();
            ObserveRoutesCore();
        }, cancellationToken).ConfigureAwait(false);
        PublishSnapshots();
    }

    public Task SetVolumeAsync(AudioSourceId sourceId, float volume, CancellationToken cancellationToken = default)
        => ChangeAsync(sourceId, handle => handle.SetVolume(volume), cancellationToken);

    public Task SetMuteAsync(AudioSourceId sourceId, bool muted, CancellationToken cancellationToken = default)
        => ChangeAsync(sourceId, handle => handle.SetMute(muted), cancellationToken);

    public Task SetBalanceAsync(AudioSourceId sourceId, float balance, CancellationToken cancellationToken = default)
        => ChangeAsync(sourceId, handle => handle.SetBalance(balance), cancellationToken);

    public async Task<AudioRouteResult> SetOutputDeviceAsync(AudioSourceId sourceId, string endpointId,
        AudioRouteRequestSource requestSource = AudioRouteRequestSource.User,
        CancellationToken cancellationToken = default)
    {
        var identity = await _worker.InvokeAsync(() =>
        {
            if (_routingBackend is null) throw new NotSupportedException("Per-app routing backend is unavailable.");
            var handle = GetHandle(sourceId);
            ValidateRoutableProcess(handle);
            return handle.Identity;
        }, cancellationToken).ConfigureAwait(false);
        var request = new ApplicationRouteRequest(AudioApplicationInstanceKey.For(identity), sourceId, endpointId,
            requestSource, Guid.NewGuid().ToString("N"));
        return await _routes.RequestAsync(request, ApplyRoutePolicyAsync, ObserveRouteAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string?> GetEffectiveOutputDeviceAsync(AudioSourceId sourceId,
        CancellationToken cancellationToken = default)
        => _worker.InvokeAsync<string?>(() =>
        {
            var handle = GetHandle(sourceId);
            var route = _routes.GetState(AudioApplicationInstanceKey.For(handle.Identity));
            if (!string.IsNullOrWhiteSpace(route?.EffectiveOutputDeviceId)) return route.EffectiveOutputDeviceId;
            var active = GetActiveObservedEndpointsCore(handle.Identity);
            return active.Count == 1 ? active[0] : handle.Identity.DeviceId;
        }, cancellationToken);

    public Task CancelPendingRoutesAsync(CancellationToken cancellationToken = default)
        => _routes.CancelAllAsync();

    public async Task RestoreAsync(AudioSourceId sourceId, CancellationToken cancellationToken = default)
    {
        var application = await _worker.InvokeAsync(() =>
        {
            if (_sessions.TryGetValue(sourceId, out var handle)) return AudioApplicationInstanceKey.For(handle.Identity);
            if (_rollback.TryGetValue(sourceId, out var entry)) return AudioApplicationInstanceKey.For(entry.Identity);
            throw new KeyNotFoundException($"Audio source {sourceId} has no live session or rollback entry.");
        }, cancellationToken).ConfigureAwait(false);
        _routes.Forget(application);
        await _worker.InvokeAsync(() => RestoreApplicationCore(sourceId), cancellationToken).ConfigureAwait(false);
        PublishSnapshots();
    }

    public async Task RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        await _routes.CancelAllAsync().ConfigureAwait(false);
        await _worker.InvokeAsync(RestoreAllCore, cancellationToken).ConfigureAwait(false);
        PublishSnapshots();
    }

    public Task<IReadOnlyList<AudioCapabilityResult>> ProbeAsync(CancellationToken cancellationToken = default)
        => _worker.InvokeAsync<IReadOnlyList<AudioCapabilityResult>>(() => _sessions.Values.Select(handle =>
        {
            try { return handle.ProbeSetters(); }
            catch (Exception exception)
            {
                var snapshot = SafeSnapshot(handle) ?? throw new InvalidOperationException("The session became invalid during capability probing.", exception);
                return new AudioCapabilityResult(snapshot, false, false, false, snapshot.Capabilities.SupportsRealtimeMeter, exception.Message);
            }
        }).ToArray(), cancellationToken);

    private async Task ChangeAsync(AudioSourceId sourceId, Action<SessionHandle> change, CancellationToken cancellationToken)
    {
        await _worker.InvokeAsync(() =>
        {
            var handle = GetHandle(sourceId);
            EnsureRollbackCore(handle);
            change(handle);
        }, cancellationToken).ConfigureAwait(false);
        PublishSnapshots();
    }

    private SessionHandle GetHandle(AudioSourceId sourceId)
        => _sessions.TryGetValue(sourceId, out var handle) ? handle : throw new KeyNotFoundException($"Audio source {sourceId} is no longer available.");

    private void EnsureRollbackCore(SessionHandle handle, IReadOnlyList<PersistedAudioRoute>? routes = null)
    {
        if (_rollback.TryGetValue(handle.Id, out var existing))
        {
            if (routes is { Count: > 0 } && existing.OriginalRoutes is not { Count: > 0 })
            {
                existing = existing with { OriginalRoutes = ToJournalRoutes(routes) };
                _rollback[handle.Id] = existing;
                _journal.UpsertAsync(existing).GetAwaiter().GetResult();
            }
            return;
        }
        var original = handle.CaptureOriginal() with
        {
            OriginalRoutes = routes is { Count: > 0 } ? ToJournalRoutes(routes) : null
        };
        _journal.UpsertAsync(original).GetAwaiter().GetResult();
        _rollback[handle.Id] = original;
    }

    private void UpdateRollbackForProcessCore(uint processId, string? requestedOutputDeviceId = null)
    {
        foreach (var sourceId in _rollback.Where(item => item.Value.Identity.ProcessId == processId)
                     .Select(item => item.Key).ToArray())
        {
            var updated = _rollback[sourceId] with
            {
                RequestedOutputDeviceId = requestedOutputDeviceId ?? _rollback[sourceId].RequestedOutputDeviceId
            };
            _rollback[sourceId] = updated;
            _journal.UpsertAsync(updated).GetAwaiter().GetResult();
        }
    }

    private static IReadOnlyList<AudioPersistedRouteState> ToJournalRoutes(IReadOnlyList<PersistedAudioRoute> routes)
        => routes.Select(route => new AudioPersistedRouteState(route.Role.ToString(), route.EndpointId, route.HResult)).ToArray();

    private static IReadOnlyList<PersistedAudioRoute> FromJournalRoutes(IReadOnlyList<AudioPersistedRouteState> routes)
        => routes.Select(route => new PersistedAudioRoute(
            Enum.Parse<AudioRouteRole>(route.Role, ignoreCase: true), route.EndpointId, route.HResult)).ToArray();

    private static bool IsSafeApplicationMatch(AudioSessionIdentity original, AudioSessionIdentity candidate)
    {
        if (original.ProcessId == 0 || original.ProcessId != candidate.ProcessId) return false;
        if (!string.IsNullOrWhiteSpace(original.ExecutablePath) &&
            !string.Equals(original.ExecutablePath, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
        return original.ProcessStartTimeUtc is null || candidate.ProcessStartTimeUtc is null ||
               original.ProcessStartTimeUtc == candidate.ProcessStartTimeUtc;
    }

    private void ValidateRoutableProcess(SessionHandle handle)
    {
        if (handle.Identity.ProcessId == 0) throw new NotSupportedException("System Sounds/PID 0 cannot use per-app routing.");
        if (handle.Identity.ProcessId == Environment.ProcessId)
            throw new NotSupportedException("Audio Source Mixer cannot route its own audio session.");
    }

    private string? FindOutputName(string? endpointId)
        => _outputDevices.FirstOrDefault(device => string.Equals(device.Id, endpointId, StringComparison.Ordinal))?.Name;

    private Task<ApplicationRouteObservation> ApplyRoutePolicyAsync(
        ApplicationRouteRequest request, CancellationToken cancellationToken)
        => _worker.InvokeAsync(() =>
        {
            if (_routingBackend is null)
                return new ApplicationRouteObservation(string.Empty, [], false, _currentDevice?.Id ?? string.Empty,
                    Error: "Per-app routing backend is unavailable.");
            var handle = _sessions.Values.FirstOrDefault(item =>
                AudioApplicationInstanceKey.For(item.Identity) == request.Application);
            if (handle is null)
                return new ApplicationRouteObservation(string.Empty, [], false, _currentDevice?.Id ?? string.Empty,
                    Error: "The application instance is no longer available.");

            var requestedAvailable = string.IsNullOrEmpty(request.RequestedOutputDeviceId) ||
                                     _outputDevices.Any(device => device.IsAvailable &&
                                         string.Equals(device.Id, request.RequestedOutputDeviceId, StringComparison.Ordinal));
            var originals = _routingBackend.GetPersistedRoutes(request.Application.ProcessId);
            if (!requestedAvailable)
                return CreateRouteObservationCore(request.Application, originals, backendCalled: false,
                    requestedOverride: request.RequestedOutputDeviceId);

            EnsureRollbackCore(handle, originals);
            var persisted = ReadPersistedTarget(originals);
            var backendCalled = !string.Equals(persisted, request.RequestedOutputDeviceId, StringComparison.Ordinal);
            if (backendCalled)
            {
                var transaction = _routingBackend.SetPersistedRoutes(request.Application.ProcessId,
                    string.IsNullOrEmpty(request.RequestedOutputDeviceId) ? null : request.RequestedOutputDeviceId);
                if (!transaction.Succeeded)
                    return new ApplicationRouteObservation(persisted, GetActiveObservedEndpointsCore(handle.Identity),
                        true, _currentDevice?.Id ?? string.Empty, true,
                        transaction.Error ?? "AudioPolicyConfig transaction failed.");
            }

            var readback = _routingBackend.GetPersistedRoutes(request.Application.ProcessId);
            UpdateRollbackForProcessCore(request.Application.ProcessId,
                requestedOutputDeviceId: request.RequestedOutputDeviceId);
            return CreateRouteObservationCore(request.Application, readback, backendCalled);
        }, cancellationToken);

    private Task<ApplicationRouteObservation> ObserveRouteAsync(
        ApplicationRouteRequest request, CancellationToken cancellationToken)
        => _worker.InvokeAsync(() =>
        {
            RefreshCore();
            if (_routingBackend is null)
                return new ApplicationRouteObservation(string.Empty, [], false, _currentDevice?.Id ?? string.Empty,
                    Error: "Per-app routing backend is unavailable.");
            return CreateRouteObservationCore(request.Application,
                _routingBackend.GetPersistedRoutes(request.Application.ProcessId), backendCalled: false);
        }, cancellationToken);

    private ApplicationRouteObservation CreateRouteObservationCore(AudioApplicationInstanceKey application,
        IReadOnlyList<PersistedAudioRoute> persistedRoutes, bool backendCalled, string? requestedOverride = null)
    {
        var representative = _sessions.Values.FirstOrDefault(item => AudioApplicationInstanceKey.For(item.Identity) == application);
        var active = representative is null ? [] : GetActiveObservedEndpointsCore(representative.Identity);
        var current = _routes.GetState(application);
        var requested = requestedOverride ?? current?.RequestedOutputDeviceId ?? ReadPersistedTarget(persistedRoutes);
        var available = string.IsNullOrEmpty(requested) || _outputDevices.Any(device => device.IsAvailable &&
            string.Equals(device.Id, requested, StringComparison.Ordinal));
        return new ApplicationRouteObservation(ReadPersistedTarget(persistedRoutes), active, available,
            _currentDevice?.Id ?? string.Empty, backendCalled);
    }

    private IReadOnlyList<string> GetActiveObservedEndpointsCore(AudioSessionIdentity identity)
        => _sessions.Values.Where(session => IsSafeApplicationMatch(identity, session.Identity))
            .Select(session => session.Snapshot())
            .Where(snapshot => snapshot.State == AudioPlaybackState.Active)
            .Select(snapshot => snapshot.DeviceId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ReadPersistedTarget(IReadOnlyList<PersistedAudioRoute> routes)
    {
        var endpoints = routes.Select(route => route.EndpointId ?? string.Empty)
            .Distinct(StringComparer.Ordinal).ToArray();
        return endpoints.Length == 1 ? endpoints[0] : "<mixed>";
    }

    private void ObserveRoutesCore()
    {
        if (_routingBackend is null) return;
        foreach (var group in _sessions.Values.Where(session => session.Identity.ProcessId != 0)
                     .GroupBy(session => AudioApplicationInstanceKey.For(session.Identity)))
        {
            var state = _routes.GetState(group.Key);
            if (state is null) continue;
            try
            {
                var observation = CreateRouteObservationCore(group.Key,
                    _routingBackend.GetPersistedRoutes(group.Key.ProcessId), backendCalled: false);
                _routes.Observe(group.Key, group.First().Id, observation);
            }
            catch (Exception exception)
            {
                _logger.Error($"Route observation failed application={group.Key}.", exception);
            }
        }
    }

    private void InitializeCore()
    {
        CleanupCore();
        var type = Type.GetTypeFromCLSID(NativeMethods.MmDeviceEnumeratorClassId, throwOnError: true)!;
        _deviceEnumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        _deviceNotification = new DeviceNotification(QueueTopologyRefreshDebounced);
        ComHelpers.ThrowIfFailed(_deviceEnumerator.RegisterEndpointNotificationCallback(_deviceNotification), "RegisterEndpointNotificationCallback");
        try
        {
            _routingBackend = new WindowsAppRoutingBackend(_logger);
            _logger.Info($"Per-app routing backend activated. ABI={_routingBackend.AbiVariant}; WindowsBuild={_routingBackend.WindowsBuild}.");
        }
        catch (Exception exception)
        {
            _routingBackend = null;
            _logger.Error("Per-app routing backend activation failed.", exception);
        }
        _logger.Info($"Per-app routing runtime probe: Routing={_routingBackend is not null}; ordinary-session gain=0..100% native.");
        RefreshOutputDevicesCore();
        RebuildEndpointContextsCore();
        UpdateCurrentDefaultDeviceCore();
    }

    private void UpdateCurrentDefaultDeviceCore()
    {
        var defaultDevice = _outputDevices.FirstOrDefault(device => !device.IsSystemDefault && device.IsDefaultMultimedia);
        if (defaultDevice is null)
            throw new InvalidOperationException("Windows did not report an active default Multimedia render endpoint.");
        _currentDevice = defaultDevice;
        _logger.Info($"Current default render device: {defaultDevice.Name} ({defaultDevice.Id}).");
    }

    private void RefreshCore()
    {
        if (_endpoints.Count == 0) return;
        var seen = new HashSet<AudioSourceId>();
        foreach (var context in _endpoints.Values)
        {
            IAudioSessionEnumerator? enumerator = null;
            try
            {
                ComHelpers.ThrowIfFailed(context.SessionManager.GetSessionEnumerator(out enumerator),
                    $"GetSessionEnumerator for {context.Id}");
                ComHelpers.ThrowIfFailed(enumerator.GetCount(out var count),
                    $"IAudioSessionEnumerator.GetCount for {context.Id}");
                for (var index = 0; index < count; index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        ComHelpers.ThrowIfFailed(enumerator.GetSession(index, out control),
                            $"GetSession({index}) for {context.Id}");
                        var endpointName = _outputDevices.FirstOrDefault(device => device.Id == context.Id)?.Name ?? context.Id;
                        var candidate = new SessionHandle(control, context.Id, endpointName, QueueRefresh);
                        control = null;
                        if (_sessions.TryGetValue(candidate.Id, out var existing))
                        {
                            candidate.Dispose();
                            seen.Add(existing.Id);
                        }
                        else
                        {
                            _sessions.Add(candidate.Id, candidate);
                            seen.Add(candidate.Id);
                            _logger.Info($"Audio session created on endpoint {context.Id}: {candidate.Id}.");
                        }
                    }
                    catch (Exception exception)
                    {
                        _logger.Error($"Could not inspect audio session index {index} on endpoint {context.Id}.", exception);
                        ComHelpers.Release(control);
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.Error($"Could not enumerate sessions on endpoint {context.Id}.", exception);
            }
            finally { ComHelpers.Release(enumerator); }
        }

        foreach (var stale in _sessions.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _sessions[stale].Dispose();
            _sessions.Remove(stale);
            _logger.Info($"Audio session removed: {stale}.");
        }
    }

    private void QueueRefresh()
    {
        if (!_initialized || _disposed || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (Exception exception) { _logger.Error("Background session refresh failed.", exception); }
            finally { Interlocked.Exchange(ref _refreshQueued, 0); }
        });
    }

    private void QueueLevelRefresh()
    {
        if (!_initialized || _disposed || Interlocked.Exchange(ref _levelRefreshQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var levels = await _worker.InvokeAsync<IReadOnlyList<AudioSourceLevel>>(() =>
                {
                    var nowTimestamp = Stopwatch.GetTimestamp();
                    var elapsed = Stopwatch.GetElapsedTime(_lastLevelTimestamp, nowTimestamp);
                    _lastLevelTimestamp = nowTimestamp;
                    var now = DateTimeOffset.UtcNow;
                    var result = new List<AudioSourceLevel>(_sessions.Count);
                    foreach (var (id, handle) in _sessions)
                    {
                        float raw;
                        try
                        {
                            raw = handle.ReadPeak();
                            _meterFailures.Remove(id);
                        }
                        catch (Exception exception)
                        {
                            raw = 0;
                            if (_meterFailures.Add(id))
                                _logger.Error($"Realtime meter read failed for {id}; the level was reset to zero.", exception);
                        }
                        var smoothed = SmoothPeak(_smoothedPeaks.GetValueOrDefault(id), raw, elapsed);
                        _smoothedPeaks[id] = smoothed;
                        result.Add(new AudioSourceLevel(id, smoothed, now));
                    }
                    foreach (var stale in _smoothedPeaks.Keys.Where(id => !_sessions.ContainsKey(id)).ToArray())
                    {
                        _smoothedPeaks.Remove(stale);
                        _meterFailures.Remove(stale);
                    }
                    return result;
                }).ConfigureAwait(false);
                if (levels.Count > 0) SourceLevelsChanged?.Invoke(this, levels);
            }
            catch (Exception exception) { _logger.Error("High-frequency audio level refresh failed.", exception); }
            finally { Interlocked.Exchange(ref _levelRefreshQueued, 0); }
        });
    }

    internal static float SmoothPeak(float previous, float current, TimeSpan elapsed)
    {
        current = float.IsFinite(current) ? Math.Clamp(current, 0, 1) : 0;
        previous = float.IsFinite(previous) ? Math.Clamp(previous, 0, 1) : 0;
        if (current >= previous) return current;
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0.02, 0.2);
        var next = Math.Max(current, previous - (float)(seconds / 0.35));
        return next < 0.002f && current == 0 ? 0 : Math.Clamp(next, 0, 1);
    }

    private void QueueTopologyRefreshDebounced()
    {
        if (!_initialized || _disposed) return;
        _topologyTimer.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
    }

    private void QueueTopologyRefresh()
    {
        if (!_initialized || _disposed || Interlocked.Exchange(ref _deviceRefreshQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var change = await _worker.InvokeAsync(() =>
                {
                    var previousDefault = _currentDevice;
                    var disconnected = _sessions.Values
                        .Where(session => _routes.GetState(AudioApplicationInstanceKey.For(session.Identity)) is
                            { State: AudioRoutingState.Disconnected } state && !string.IsNullOrEmpty(state.RequestedOutputDeviceId))
                        .GroupBy(session => AudioApplicationInstanceKey.For(session.Identity))
                        .Select(group =>
                        {
                            var state = _routes.GetState(group.Key)!;
                            return (SourceId: group.First().Id, EndpointId: state.RequestedOutputDeviceId);
                        }).ToArray();
                    RefreshOutputDevicesCore();
                    RebuildEndpointContextsCore();
                    UpdateCurrentDefaultDeviceCore();
                    RefreshCore();
                    ObserveRoutesCore();
                    var reconnect = disconnected.Where(item => _outputDevices.Any(device => device.IsAvailable &&
                        string.Equals(device.Id, item.EndpointId, StringComparison.Ordinal))).ToArray();
                    return (Previous: previousDefault, Current: _currentDevice!, Devices: _outputDevices,
                        Reconnect: reconnect);
                }).ConfigureAwait(false);
                foreach (var reconnect in change.Reconnect)
                {
                    try
                    {
                        await SetOutputDeviceAsync(reconnect.SourceId, reconnect.EndpointId,
                            AudioRouteRequestSource.DeviceReconnect).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        _logger.Error($"Device reconnect route reconciliation failed source={reconnect.SourceId}; endpoint={reconnect.EndpointId}.", exception);
                    }
                }
                OutputDevicesChanged?.Invoke(this, change.Devices);
                if (change.Previous is null || !string.Equals(change.Previous.Id, change.Current.Id, StringComparison.Ordinal))
                    DefaultDeviceChanged?.Invoke(this, change.Current);
                PublishSnapshots();
            }
            catch (Exception exception) { _logger.Error("Debounced render topology refresh failed.", exception); }
            finally { Interlocked.Exchange(ref _deviceRefreshQueued, 0); }
        });
    }

    private void RestoreCore(AudioSourceId sourceId)
    {
        if (!_rollback.TryGetValue(sourceId, out var original) && _sessions.TryGetValue(sourceId, out var requestedHandle))
        {
            original = _rollback.Values.FirstOrDefault(entry => IsSafeApplicationMatch(entry.Identity, requestedHandle.Identity));
            if (original is null) return;
            sourceId = original.SourceId;
        }
        if (original is null || !_rollback.Remove(sourceId)) return;
        if (original.OriginalRoutes is { Count: > 0 } && _routingBackend is not null)
        {
            _routingBackend.RestoreRoutes(original.Identity.ProcessId, FromJournalRoutes(original.OriginalRoutes));
        }
        if (_sessions.TryGetValue(sourceId, out var handle) && original.Identity.IsSafeRestoreMatch(handle.Identity))
        {
            handle.Restore(original);
        }
        else
        {
            var migrated = _sessions.Values.FirstOrDefault(candidate =>
                IsSafeApplicationMatch(original.Identity, candidate.Identity) &&
                string.Equals(candidate.Identity.DeviceId, original.Identity.DeviceId, StringComparison.Ordinal) &&
                string.Equals(candidate.Identity.SessionIdentifier, original.Identity.SessionIdentifier, StringComparison.Ordinal))
                ?? _sessions.Values.FirstOrDefault(candidate =>
                    IsSafeApplicationMatch(original.Identity, candidate.Identity) &&
                    string.Equals(candidate.Identity.DeviceId, original.Identity.DeviceId, StringComparison.Ordinal));
            migrated?.RestoreForMigratedApplication(original);
        }
        _routes.Forget(AudioApplicationInstanceKey.For(original.Identity));
        _journal.RemoveAsync(sourceId).GetAwaiter().GetResult();
    }

    private void RestoreApplicationCore(AudioSourceId sourceId)
    {
        AudioSessionIdentity? identity = null;
        if (_sessions.TryGetValue(sourceId, out var handle)) identity = handle.Identity;
        else if (_rollback.TryGetValue(sourceId, out var entry)) identity = entry.Identity;
        if (identity is null) return;

        var entries = _rollback.Values.Where(entry => IsSafeApplicationMatch(identity, entry.Identity)).ToArray();
        var routeOwner = entries.FirstOrDefault(entry => entry.OriginalRoutes is { Count: > 0 });
        foreach (var entry in entries.OrderByDescending(entry => entry.OriginalRoutes is { Count: > 0 }))
        {
            if (routeOwner is not null && entry != routeOwner && entry.CapturedAt >= routeOwner.CapturedAt &&
                !string.Equals(entry.Identity.DeviceId, routeOwner.Identity.DeviceId, StringComparison.Ordinal))
            {
                _rollback.Remove(entry.SourceId);
                _journal.RemoveAsync(entry.SourceId).GetAwaiter().GetResult();
                continue;
            }
            RestoreCore(entry.SourceId);
        }
    }

    private void RestoreAllCore()
    {
        foreach (var sourceId in _rollback.Values
                     .OrderByDescending(entry => entry.OriginalRoutes is { Count: > 0 })
                     .Select(entry => entry.SourceId).ToArray())
        {
            try { RestoreCore(sourceId); }
            catch (Exception exception) { _logger.Error($"Failed restoring {sourceId}.", exception); }
        }
    }

    private void PublishSnapshots()
    {
        if (_disposed) return;
        _ = GetSourcesAsync().ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion) SourcesChanged?.Invoke(this, task.Result);
        }, TaskScheduler.Default);
    }

    private AudioSourceSnapshot? SafeSnapshot(SessionHandle handle)
    {
        try
        {
            var snapshot = handle.Snapshot();
            var eligible = handle.Identity.ProcessId != 0 && handle.Identity.ProcessId != Environment.ProcessId;
            var supportsRouting = eligible && _routingBackend is not null;
            var state = _routes.GetState(AudioApplicationInstanceKey.For(handle.Identity));
            var effectiveId = state?.EffectiveOutputDeviceId;
            if (string.IsNullOrWhiteSpace(effectiveId)) effectiveId = snapshot.DeviceId;
            var effectiveName = FindOutputName(effectiveId);
            if (string.IsNullOrWhiteSpace(effectiveName)) effectiveName = FindOutputName(effectiveId);
            var requestedId = state?.RequestedOutputDeviceId ?? string.Empty;
            var requestedName = FindOutputName(requestedId) ?? OutputDeviceInfo.SystemDefault.Name;
            var routingState = state?.State ?? (supportsRouting ? AudioRoutingState.SystemDefault : AudioRoutingState.Unavailable);
            var limitation = !eligible
                ? handle.Identity.ProcessId == 0
                    ? "系统声音/PID 0 不支持按应用输出路由。"
                    : "Audio Source Mixer 不允许路由自身音频会话。"
                : "普通 Windows 会话使用原生 0–100% 音量；输出路由按应用进程生效。";
            var capabilities = snapshot.Capabilities with
            {
                SupportsExtendedGain = false,
                SupportsOutputRouting = supportsRouting,
                SupportsDeviceHotSwitch = supportsRouting,
                Limitation = limitation
            };
            return snapshot with
            {
                Capabilities = capabilities,
                ProcessingMode = AudioProcessingMode.Native,
                RequestedOutputDeviceId = requestedId,
                RequestedOutputDeviceName = requestedName,
                EffectiveOutputDeviceId = effectiveId,
                EffectiveOutputDeviceName = effectiveName,
                RoutingState = routingState,
                RoutingError = state?.Error,
                ProcessStartTimeUtc = handle.Identity.ProcessStartTimeUtc
            };
        }
        catch (COMException) { return null; }
        catch (InvalidComObjectException) { return null; }
    }

    private static string ReadDeviceId(IMMDevice device)
    {
        ComHelpers.ThrowIfFailed(device.GetId(out var pointer), "IMMDevice.GetId");
        try { return Marshal.PtrToStringUni(pointer) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static string ReadFriendlyName(IMMDevice device)
        => ReadStringProperty(device, NativeMethods.DeviceFriendlyName, "默认输出设备");

    private static string ReadDescription(IMMDevice device)
        => ReadStringProperty(device, NativeMethods.DeviceDescription, string.Empty);

    private static string ReadStringProperty(IMMDevice device, PropertyKey propertyKey, string fallback)
    {
        ComHelpers.ThrowIfFailed(device.OpenPropertyStore(0, out var store), "OpenPropertyStore");
        try
        {
            var key = propertyKey;
            ComHelpers.ThrowIfFailed(store.GetValue(ref key, out var value), "IPropertyStore.GetValue(PKEY_Device_FriendlyName)");
            try { return value.GetString() ?? fallback; }
            finally { NativeMethods.PropVariantClear(ref value); }
        }
        finally { ComHelpers.Release(store); }
    }

    private void RefreshOutputDevicesCore()
    {
        if (_deviceEnumerator is null) return;
        var consoleId = TryReadDefaultDeviceId(ERole.Console);
        var multimediaId = TryReadDefaultDeviceId(ERole.Multimedia);
        var communicationsId = TryReadDefaultDeviceId(ERole.Communications);
        ComHelpers.ThrowIfFailed(_deviceEnumerator.EnumAudioEndpoints(EDataFlow.Render, (uint)DeviceState.Active, out var collection),
            "EnumAudioEndpoints(Render, Active)");
        try
        {
            ComHelpers.ThrowIfFailed(collection.GetCount(out var count), "IMMDeviceCollection.GetCount");
            var devices = new List<OutputDeviceInfo>(checked((int)count + 1)) { OutputDeviceInfo.SystemDefault };
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    ComHelpers.ThrowIfFailed(collection.Item(index, out device), $"IMMDeviceCollection.Item({index})");
                    var id = ReadDeviceId(device);
                    ComHelpers.ThrowIfFailed(device.GetState(out var state), "IMMDevice.GetState");
                    var (channels, sampleRate) = ReadMixFormat(device);
                    devices.Add(new OutputDeviceInfo(id, ReadFriendlyName(device), ReadDescription(device), state,
                        IsDefaultConsole: string.Equals(id, consoleId, StringComparison.Ordinal),
                        IsDefaultMultimedia: string.Equals(id, multimediaId, StringComparison.Ordinal),
                        IsDefaultCommunications: string.Equals(id, communicationsId, StringComparison.Ordinal),
                        ChannelCount: channels, SampleRate: sampleRate));
                }
                catch (Exception exception)
                {
                    _logger.Error($"Could not inspect render endpoint index {index}.", exception);
                }
                finally { ComHelpers.Release(device); }
            }
            _outputDevices = devices.OrderByDescending(device => device.IsSystemDefault).ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            _logger.Info($"Enumerated {_outputDevices.Count - 1} active render endpoints.");
        }
        finally { ComHelpers.Release(collection); }
    }

    private void RebuildEndpointContextsCore()
    {
        if (_deviceEnumerator is null) return;
        ComHelpers.ThrowIfFailed(_deviceEnumerator.EnumAudioEndpoints(EDataFlow.Render, (uint)DeviceState.Active, out var collection),
            "EnumAudioEndpoints(Render, Active) for session contexts");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            ComHelpers.ThrowIfFailed(collection.GetCount(out var count), "IMMDeviceCollection.GetCount for session contexts");
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    ComHelpers.ThrowIfFailed(collection.Item(index, out device), $"IMMDeviceCollection.Item({index}) for session context");
                    var id = ReadDeviceId(device);
                    seen.Add(id);
                    if (_endpoints.ContainsKey(id)) continue;
                    var context = new EndpointContext(device, id, QueueRefresh);
                    device = null;
                    _endpoints.Add(id, context);
                    _logger.Info($"Activated render endpoint context: {id}.");
                }
                catch (Exception exception)
                {
                    _logger.Error($"Could not activate render endpoint context at index {index}.", exception);
                }
                finally { ComHelpers.Release(device); }
            }
        }
        finally { ComHelpers.Release(collection); }

        foreach (var staleId in _endpoints.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            try { _endpoints[staleId].Dispose(); }
            catch (Exception exception) { _logger.Error($"Could not dispose stale endpoint context {staleId}.", exception); }
            finally { _endpoints.Remove(staleId); }
            _logger.Info($"Removed render endpoint context: {staleId}.");
        }
        _logger.Info($"Session discovery now covers {_endpoints.Count} active render endpoint contexts.");
    }

    private string? TryReadDefaultDeviceId(ERole role)
    {
        IMMDevice? device = null;
        try
        {
            if (_deviceEnumerator!.GetDefaultAudioEndpoint(EDataFlow.Render, role, out device) < 0) return null;
            return ReadDeviceId(device);
        }
        finally { ComHelpers.Release(device); }
    }

    private static (int? Channels, int? SampleRate) ReadMixFormat(IMMDevice device)
    {
        object? instance = null;
        IntPtr formatPointer = IntPtr.Zero;
        try
        {
            var interfaceId = NativeMethods.AudioClientInterfaceId;
            ComHelpers.ThrowIfFailed(device.Activate(ref interfaceId, ClsCtx.All, IntPtr.Zero, out instance), "IMMDevice.Activate(IAudioClient)");
            var audioClient = (IAudioClient)instance;
            ComHelpers.ThrowIfFailed(audioClient.GetMixFormat(out formatPointer), "IAudioClient.GetMixFormat");
            var format = Marshal.PtrToStructure<WaveFormatEx>(formatPointer);
            return (format.Channels, checked((int)format.SamplesPerSecond));
        }
        finally
        {
            if (formatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPointer);
            ComHelpers.Release(instance);
        }
    }

    private void CleanupCore()
    {
        foreach (var session in _sessions.Values)
        {
            try { session.Dispose(); } catch { }
        }
        _sessions.Clear();
        _smoothedPeaks.Clear();
        _meterFailures.Clear();
        foreach (var context in _endpoints.Values)
        {
            try { context.Dispose(); } catch { }
        }
        _endpoints.Clear();
        if (_deviceEnumerator is not null && _deviceNotification is not null)
        {
            try { _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceNotification); } catch { }
        }
        _deviceNotification = null;
        try { _routingBackend?.Dispose(); } catch { }
        _routingBackend = null;
        ComHelpers.Release(_deviceEnumerator);
        _deviceEnumerator = null;
        _currentDevice = null;
        _outputDevices = [OutputDeviceInfo.SystemDefault];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _levelTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _topologyTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _refreshTimer.Dispose();
        _levelTimer.Dispose();
        _topologyTimer.Dispose();
        try
        {
            await _worker.InvokeAsync(() =>
            {
                RestoreAllCore();
                CleanupCore();
            }).ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            await _routes.DisposeAsync().ConfigureAwait(false);
            _worker.Dispose();
        }
    }

    private sealed class DeviceNotification(Action topologyChanged) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState) { topologyChanged(); return 0; }
        public int OnDeviceAdded(string deviceId) { topologyChanged(); return 0; }
        public int OnDeviceRemoved(string deviceId) { topologyChanged(); return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.Render) topologyChanged();
            return 0;
        }
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) { topologyChanged(); return 0; }
    }
}
