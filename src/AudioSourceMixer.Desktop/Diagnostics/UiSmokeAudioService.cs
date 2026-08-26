using AudioSourceMixer.Core.Abstractions;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Desktop.Diagnostics;

/// <summary>
/// Deterministic audio boundary for UI diagnostics. A hosted CI runner may not expose any
/// Windows audio endpoint, but the smoke test must still exercise the real window, bindings,
/// templates, layout, and shutdown path without depending on machine hardware.
/// </summary>
internal sealed class UiSmokeAudioService(IReadOnlyList<AudioSourceSnapshot> sources) :
    IAudioSourceDiscovery, IAudioSourceLevelDiscovery, IAudioSourceController,
    IAudioOutputDeviceService, IAudioRoutingController
{
    private static readonly IReadOnlyList<OutputDeviceInfo> Devices =
    [
        OutputDeviceInfo.SystemDefault,
        new("diagnostic-device", "Diagnostic Device", IsDefaultMultimedia: true, ChannelCount: 2, SampleRate: 48000),
        new("diagnostic-original-device", "Original Diagnostic Device", ChannelCount: 2, SampleRate: 48000),
        new("diagnostic-long-device", "Long-name Diagnostic Device", ChannelCount: 2, SampleRate: 48000)
    ];

    public event EventHandler<IReadOnlyList<AudioSourceSnapshot>>? SourcesChanged { add { } remove { } }
    public event EventHandler<IReadOnlyList<AudioSourceLevel>>? SourceLevelsChanged { add { } remove { } }
    public event EventHandler<OutputDeviceInfo>? DefaultDeviceChanged { add { } remove { } }
    public event EventHandler<IReadOnlyList<OutputDeviceInfo>>? OutputDevicesChanged { add { } remove { } }
    public event EventHandler<AudioRouteResult>? RoutingStateChanged { add { } remove { } }

    public Task<OutputDeviceInfo> InitializeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Devices[1]);

    public Task<IReadOnlyList<AudioSourceSnapshot>> GetSourcesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(sources);

    public Task<IReadOnlyList<OutputDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Devices);

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetVolumeAsync(AudioSourceId sourceId, float volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetMuteAsync(AudioSourceId sourceId, bool muted, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetBalanceAsync(AudioSourceId sourceId, float balance, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RestoreAsync(AudioSourceId sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RestoreAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CancelPendingRoutesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string?> GetEffectiveOutputDeviceAsync(AudioSourceId sourceId, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("diagnostic-device");

    public Task<AudioRouteResult> SetOutputDeviceAsync(
        AudioSourceId sourceId,
        string endpointId,
        AudioRouteRequestSource requestSource = AudioRouteRequestSource.User,
        CancellationToken cancellationToken = default)
    {
        var source = sources.First(item => item.Id == sourceId);
        var effective = string.IsNullOrWhiteSpace(endpointId) ? "diagnostic-device" : endpointId;
        var state = string.IsNullOrWhiteSpace(endpointId) ? AudioRoutingState.SystemDefault : AudioRoutingState.Applied;
        return Task.FromResult(new AudioRouteResult(sourceId, source.ProcessId, endpointId, effective, state,
            RequestSource: requestSource, PersistedOutputDeviceId: endpointId, BackendCalled: false));
    }
}
