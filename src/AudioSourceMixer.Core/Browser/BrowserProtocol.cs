using System.Text.Json;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Browser;

public sealed record BrowserOutputEndpoint
{
    public required string EndpointId { get; init; }
    public required string FriendlyName { get; init; }
    public bool IsSystemDefault { get; init; }
    public bool IsDefaultMultimedia { get; init; }
    public bool IsAvailable { get; init; } = true;
}

public sealed record BrowserMessage
{
    public int ProtocolVersion { get; init; } = BrowserProtocol.Version;
    public required string Type { get; init; }
    public string? Browser { get; init; }
    public string? ExtensionVersion { get; init; }
    public long? TabId { get; init; }
    public string? Title { get; init; }
    public string? Origin { get; init; }
    public string? CaptureState { get; init; }
    public string? SourceId { get; init; }
    public float? Volume { get; init; }
    public float? Balance { get; init; }
    public bool? Muted { get; init; }
    public float? Peak { get; init; }
    public string? OutputDeviceId { get; init; }
    public string? OutputDeviceName { get; init; }
    public bool? FollowSystemDefault { get; init; }
    public string? ResolvedOutputDeviceId { get; init; }
    public string? ResolvedOutputDeviceName { get; init; }
    public string? OutputStatus { get; init; }
    public IReadOnlyList<BrowserOutputEndpoint>? OutputDevices { get; init; }
    public string? CorrelationId { get; init; }
    public long? Generation { get; init; }
    public string? RequestSource { get; init; }
    public bool? ForceAuthorization { get; init; }
    public string? BrowserDeviceId { get; init; }
    public string? BrowserDeviceLabel { get; init; }
    public string? BrowserGroupId { get; init; }
    public string? EffectiveSinkId { get; init; }
    public string? EffectiveSinkLabel { get; init; }
    public string? RoutingState { get; init; }
    public double? SetSinkDurationMs { get; init; }
    public bool? SetSinkIdSupported { get; init; }
    public string? Error { get; init; }
    public AudioEffectSettings? Equalizer { get; init; }
}

public static class BrowserProtocol
{
    public const int Version = 3;
    public const int LegacyVersion = 1;
    public const int RoutingVersion = 2;
    public const int MaximumMessageBytes = 64 * 1024;
    public const long MaximumJavaScriptSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedTypes =
    [
        "bridge.hello", "bridge.status", "bridge.openOptions", "bridge.clearMappings",
        "tab.register", "tab.update", "tab.unregister",
        "tab.setAudio", "tab.stop", "tab.commandResult"
    ];

    public static BrowserMessage Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException($"Native message size must be between 1 and {MaximumMessageBytes} bytes.");

        BrowserMessage message;
        try { message = JsonSerializer.Deserialize<BrowserMessage>(utf8Json, Options) ?? throw new InvalidDataException("Message is empty."); }
        catch (JsonException exception) { throw new InvalidDataException("Message is not valid JSON.", exception); }

        Validate(message);
        return message;
    }

    public static byte[] Serialize(BrowserMessage message)
    {
        Validate(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Serialized native message is too large.");
        return payload;
    }

    public static void Validate(BrowserMessage message)
    {
        if (message.ProtocolVersion is not (LegacyVersion or RoutingVersion or Version))
            throw new InvalidDataException($"Unsupported protocol version {message.ProtocolVersion}; supported versions are 1, 2 and 3.");
        if (!AllowedTypes.Contains(message.Type)) throw new InvalidDataException($"Unsupported message type '{message.Type}'.");

        if (message.Type.StartsWith("tab.", StringComparison.Ordinal) && message.Type is not "tab.commandResult")
        {
            if (message.TabId is < 0) throw new InvalidDataException("tabId must be non-negative.");
            if (message.TabId is null) throw new InvalidDataException("tabId is required.");
        }

        if (message.Browser is not null && message.Browser is not ("chrome" or "edge"))
            throw new InvalidDataException("browser must be chrome or edge.");
        if (message.ExtensionVersion?.Length > 64) throw new InvalidDataException("extensionVersion is too long.");
        try
        {
            if (message.Volume is { } volume)
            {
                if (message.ProtocolVersion == LegacyVersion) AudioMath.EnsureSessionVolume(volume);
                else AudioMath.EnsureUserGain(volume);
            }
            if (message.Balance is { } balance) _ = AudioMath.BalanceToGains(balance);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Audio control value is outside its permitted range.", exception);
        }
        if (message.Peak is < 0 or > 1) throw new InvalidDataException("peak must be between 0 and 1.");
        if (message.Title?.Length > 512) throw new InvalidDataException("title is too long.");
        if (message.Origin?.Length > 512) throw new InvalidDataException("origin is too long.");
        if (message.OutputDeviceId?.Length > 1024) throw new InvalidDataException("outputDeviceId is too long.");
        if (message.OutputDeviceName?.Length > 512) throw new InvalidDataException("outputDeviceName is too long.");
        if (message.ResolvedOutputDeviceId?.Length > 1024) throw new InvalidDataException("resolvedOutputDeviceId is too long.");
        if (message.ResolvedOutputDeviceName?.Length > 512) throw new InvalidDataException("resolvedOutputDeviceName is too long.");
        if (message.OutputStatus?.Length > 512) throw new InvalidDataException("outputStatus is too long.");
        if (message.CorrelationId?.Length > 128) throw new InvalidDataException("correlationId is too long.");
        if (message.Generation is < 0 or > MaximumJavaScriptSafeInteger)
            throw new InvalidDataException("generation must be a non-negative JavaScript safe integer.");
        if (message.RequestSource is not null && message.RequestSource is not ("User" or "DeviceReconnect" or "ProfileRestore"))
            throw new InvalidDataException("requestSource is invalid.");
        if (message.BrowserDeviceId?.Length > 1024 || message.EffectiveSinkId?.Length > 1024)
            throw new InvalidDataException("browser output device identifier is too long.");
        if (message.BrowserDeviceLabel?.Length > 512 || message.EffectiveSinkLabel?.Length > 512 ||
            message.BrowserGroupId?.Length > 1024) throw new InvalidDataException("browser output device metadata is too long.");
        if (message.RoutingState is not null && message.RoutingState is not ("Default" or "SystemDefault" or
            "PendingAuthorization" or "PendingStreamRestart" or "Partial" or "Applied" or "Fallback" or
            "Disconnected" or "Failed"))
            throw new InvalidDataException("routingState is invalid.");
        if (message.SetSinkDurationMs is < 0 or > 120_000) throw new InvalidDataException("setSinkDurationMs is invalid.");
        if (message.OutputDevices is { Count: > 64 }) throw new InvalidDataException("Too many output devices.");
        if (message.OutputDevices is not null)
        {
            foreach (var device in message.OutputDevices)
            {
                if (string.IsNullOrWhiteSpace(device.FriendlyName) || device.FriendlyName.Length > 512 || device.EndpointId.Length > 1024)
                    throw new InvalidDataException("Output device metadata is invalid.");
            }
        }
        if (message.Equalizer is not null)
        {
            if (message.ProtocolVersion < Version) throw new InvalidDataException("Equalizer requires browser protocol 3.");
            try { EqualizerCatalog.Validate(message.Equalizer); }
            catch (ArgumentException exception)
            { throw new InvalidDataException("Equalizer settings are invalid.", exception); }
        }
    }

    public static AudioSourceId GetSourceId(BrowserMessage message)
    {
        if (message.Browser is null || message.TabId is null) throw new InvalidDataException("browser and tabId are required.");
        return AudioSourceId.ForBrowserTab(message.Browser, message.TabId.Value);
    }
}
