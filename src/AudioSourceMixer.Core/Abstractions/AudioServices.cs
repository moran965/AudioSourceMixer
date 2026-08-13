using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Abstractions;

public interface IAudioSourceDiscovery
{
    event EventHandler<IReadOnlyList<AudioSourceSnapshot>>? SourcesChanged;
    event EventHandler<OutputDeviceInfo>? DefaultDeviceChanged;
    Task<OutputDeviceInfo> InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioSourceSnapshot>> GetSourcesAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public interface IAudioSourceController
{
    Task SetVolumeAsync(AudioSourceId sourceId, float volume, CancellationToken cancellationToken = default);
    Task SetMuteAsync(AudioSourceId sourceId, bool muted, CancellationToken cancellationToken = default);
    Task SetBalanceAsync(AudioSourceId sourceId, float balance, CancellationToken cancellationToken = default);
    Task RestoreAsync(AudioSourceId sourceId, CancellationToken cancellationToken = default);
    Task RestoreAllAsync(CancellationToken cancellationToken = default);
}

public interface IAudioOutputDeviceService
{
    event EventHandler<IReadOnlyList<OutputDeviceInfo>>? OutputDevicesChanged;
    Task<IReadOnlyList<OutputDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);
}

public interface IAudioRoutingController
{
    event EventHandler<AudioRouteResult>? RoutingStateChanged;
    Task<AudioRouteResult> SetOutputDeviceAsync(AudioSourceId sourceId, string endpointId,
        AudioRouteRequestSource requestSource = AudioRouteRequestSource.User,
        CancellationToken cancellationToken = default);
    Task<string?> GetEffectiveOutputDeviceAsync(AudioSourceId sourceId,
        CancellationToken cancellationToken = default);
    Task CancelPendingRoutesAsync(CancellationToken cancellationToken = default);
}

public interface IAudioProfileStore
{
    Task<IReadOnlyDictionary<string, AudioSourceProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AudioSourceProfile profile, CancellationToken cancellationToken = default);
    Task RemoveAsync(string stableKey, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IRollbackJournal
{
    Task<IReadOnlyList<AudioRollbackEntry>> LoadAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(AudioRollbackEntry entry, CancellationToken cancellationToken = default);
    Task RemoveAsync(AudioSourceId sourceId, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
