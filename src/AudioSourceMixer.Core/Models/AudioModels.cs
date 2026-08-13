using System.Security.Cryptography;
using System.Text;

namespace AudioSourceMixer.Core.Models;

public enum AudioSourceKind
{
    WindowsSession,
    ChromeTab,
    EdgeTab
}

public enum AudioPlaybackState
{
    Inactive,
    Active,
    Expired,
    Unavailable
}

public enum AudioProcessingMode
{
    Native,
    Advanced,
    Unavailable
}

public enum AudioRoutingState
{
    Unavailable,
    SystemDefault,
    PendingStreamRestart,
    Partial,
    Applied,
    Disconnected,
    PendingAuthorization,
    Failed
}

public enum AudioRouteRequestSource
{
    User,
    ProfileRestore,
    DeviceReconnect,
    ExitRestore
}

public readonly record struct AudioSourceId(string Value)
{
    public static AudioSourceId ForWindowsSession(string deviceId, string sessionInstanceId)
        => new($"win:{Hash(deviceId)}:{Hash(sessionInstanceId)}");

    public static AudioSourceId ForBrowserTab(string browser, long tabId)
    {
        if (!string.Equals(browser, "chrome", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(browser, "edge", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Browser must be chrome or edge.", nameof(browser));
        }

        if (tabId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tabId));
        }

        return new AudioSourceId($"{browser.ToLowerInvariant()}:{tabId}");
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    public override string ToString() => Value;
}

public sealed record AudioSourceCapabilities(
    bool SupportsMasterVolume,
    bool SupportsMute,
    bool SupportsStereoBalance,
    int ChannelCount,
    bool IsBrowserEnhanced,
    bool SupportsRealtimeMeter,
    bool CanRestoreOriginalState,
    string? Limitation = null,
    bool SupportsExtendedGain = false,
    bool SupportsOutputRouting = false,
    bool SupportsDeviceHotSwitch = false);

public sealed record AudioSourceSnapshot(
    AudioSourceId Id,
    AudioSourceKind Kind,
    string DisplayName,
    string SourceDescription,
    uint ProcessId,
    string? ExecutablePath,
    string DeviceId,
    string SessionIdentifier,
    string SessionInstanceIdentifier,
    AudioPlaybackState State,
    float Volume,
    bool Muted,
    float Balance,
    float Peak,
    IReadOnlyList<float> ChannelVolumes,
    AudioSourceCapabilities Capabilities,
    DateTimeOffset ObservedAt,
    string OutputDeviceId = "",
    string? OutputDeviceName = null,
    AudioProcessingMode ProcessingMode = AudioProcessingMode.Native,
    string RequestedOutputDeviceId = "",
    string? RequestedOutputDeviceName = null,
    string EffectiveOutputDeviceId = "",
    string? EffectiveOutputDeviceName = null,
    AudioRoutingState RoutingState = AudioRoutingState.SystemDefault,
    string? RoutingError = null,
    DateTimeOffset? ProcessStartTimeUtc = null);

public sealed record OutputDeviceInfo(
    string Id,
    string Name,
    string? Description = null,
    uint State = 1,
    bool IsSystemDefault = false,
    bool IsDefaultConsole = false,
    bool IsDefaultMultimedia = false,
    bool IsDefaultCommunications = false,
    int? ChannelCount = null,
    int? SampleRate = null,
    bool IsAvailable = true)
{
    public static OutputDeviceInfo SystemDefault { get; } = new(
        "", "系统默认", "跟随 Windows 默认输出设备", IsSystemDefault: true,
        IsDefaultConsole: true, IsDefaultMultimedia: true, IsDefaultCommunications: true);
}

public sealed record AudioSourceProfile(
    string StableKey,
    float Volume,
    float Balance,
    bool Muted,
    bool AutoApply = true,
    DateTimeOffset UpdatedAt = default,
    string? OutputDeviceId = null,
    string? OutputDeviceName = null,
    AudioSourceKind SourceKind = AudioSourceKind.WindowsSession);

public sealed record BrowserTabSource(
    AudioSourceId Id,
    string Browser,
    long TabId,
    string Title,
    string Origin,
    string CaptureState,
    float Volume,
    float Balance,
    bool Muted,
    float Peak,
    int ProtocolVersion = 1,
    string OutputDeviceId = "",
    string? OutputDeviceName = null,
    string? OutputStatus = null,
    string EffectiveOutputDeviceId = "",
    string? EffectiveOutputDeviceName = null,
    AudioRoutingState RoutingState = AudioRoutingState.SystemDefault,
    string? RoutingError = null,
    string? CorrelationId = null,
    string? BrowserDeviceId = null,
    string? EffectiveBrowserSinkId = null);

public sealed record AudioSessionIdentity(
    string DeviceId,
    string SessionIdentifier,
    string SessionInstanceIdentifier,
    uint ProcessId,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartTimeUtc)
{
    public string StableProfileKey
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(ExecutablePath)
                ? SessionIdentifier
                : Path.GetFullPath(ExecutablePath).ToUpperInvariant();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        }
    }

    public bool IsSafeRestoreMatch(AudioSessionIdentity candidate)
    {
        if (!string.Equals(DeviceId, candidate.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(SessionInstanceIdentifier, candidate.SessionInstanceIdentifier, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ExecutablePath) &&
            !string.Equals(ExecutablePath, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ProcessStartTimeUtc is null || candidate.ProcessStartTimeUtc is null ||
               ProcessStartTimeUtc == candidate.ProcessStartTimeUtc;
    }
}

public readonly record struct AudioApplicationInstanceKey(
    string StableProfileKey,
    uint ProcessId,
    DateTimeOffset? ProcessStartTimeUtc)
{
    public static AudioApplicationInstanceKey For(AudioSessionIdentity identity)
        => new(identity.StableProfileKey, identity.ProcessId, identity.ProcessStartTimeUtc);

    public static AudioApplicationInstanceKey For(AudioSourceSnapshot snapshot)
    {
        if (snapshot.Kind == AudioSourceKind.WindowsSession)
            return new(Persistence.ProfileKeys.For(snapshot), snapshot.ProcessId, snapshot.ProcessStartTimeUtc);
        var separator = snapshot.Id.Value.LastIndexOf(':');
        var instance = separator >= 0 && uint.TryParse(snapshot.Id.Value[(separator + 1)..], out var tabId)
            ? tabId
            : BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Id.Value)));
        return new(Persistence.ProfileKeys.For(snapshot), instance, null);
    }

    public override string ToString()
        => $"{StableProfileKey}:{ProcessId}:{ProcessStartTimeUtc?.UtcTicks ?? 0}";
}

public sealed record AudioRollbackEntry(
    AudioSourceId SourceId,
    AudioSessionIdentity Identity,
    float MasterVolume,
    bool Muted,
    IReadOnlyList<float> ChannelVolumes,
    DateTimeOffset CapturedAt,
    IReadOnlyList<AudioPersistedRouteState>? OriginalRoutes = null,
    string? RequestedOutputDeviceId = null);

public sealed record AudioPersistedRouteState(string Role, string? EndpointId, int HResult = 0);

public sealed record AudioRouteResult(
    AudioSourceId SourceId,
    uint ProcessId,
    string RequestedOutputDeviceId,
    string EffectiveOutputDeviceId,
    AudioRoutingState State,
    string? Error = null,
    string? CorrelationId = null,
    long Generation = 0,
    AudioRouteRequestSource RequestSource = AudioRouteRequestSource.User,
    string? PersistedOutputDeviceId = null,
    IReadOnlyList<string>? ObservedOutputDeviceIds = null,
    bool BackendCalled = false);
