using System.Security.Cryptography;
using System.Text;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Persistence;

public static class ProfileKeys
{
    public static string For(AudioSourceSnapshot source)
    {
        var identity = source.Kind == AudioSourceKind.WindowsSession
            ? (string.IsNullOrWhiteSpace(source.ExecutablePath) ? source.SessionIdentifier : Path.GetFullPath(source.ExecutablePath).ToUpperInvariant())
            : $"{source.Kind}:{source.SourceDescription}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}

public static class SourceSortModes
{
    public const string Recent = "recent";
    public const string Manual = "manual";
    public static string Normalize(string? value) => Manual;
}

public sealed record HiddenSourceSetting(
    string SourceId,
    AudioSourceKind Kind,
    DateTimeOffset LastSeenUtc);

public sealed record ApplicationSettings(
    bool CloseToTray = true,
    bool AutoApplyProfiles = true,
    bool RememberProfiles = true,
    bool ShowInactiveSessions = true,
    bool StartMinimizedToTray = true,
    bool ShowOperationTips = true,
    bool TrayHintShown = false,
    string BrowserOnboardingChoice = "undecided",
    string? OnboardingCompletedVersion = null,
    bool BrowserGuideDismissed = false,
    string SourceSortMode = SourceSortModes.Manual,
    IReadOnlyList<string>? ManualSourceOrder = null,
    IReadOnlyList<HiddenSourceSetting>? ManuallyHiddenSources = null,
    bool HideBrowserAggregateSessions = true,
    IReadOnlyList<string>? VisibleBrowserAggregates = null,
    int SchemaVersion = 7);

public sealed class JsonApplicationSettingsStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "settings.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new ApplicationSettings();
            ApplicationSettings? loaded;
            await using (var stream = File.OpenRead(_path))
                loaded = await System.Text.Json.JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            if (loaded is null) return new ApplicationSettings();
            if (loaded.SchemaVersion < 4)
            {
                loaded = loaded with
                {
                    BrowserOnboardingChoice = "existing-user",
                    OnboardingCompletedVersion = "0.2.1",
                    BrowserGuideDismissed = true,
                    SchemaVersion = 7
                };
            }
            var normalized = Normalize(loaded);
            if (!string.Equals(System.Text.Json.JsonSerializer.Serialize(loaded),
                    System.Text.Json.JsonSerializer.Serialize(normalized), StringComparison.Ordinal))
                await WriteUnsafeAsync(normalized, cancellationToken).ConfigureAwait(false);
            return normalized;
        }
        catch (System.Text.Json.JsonException) { return new ApplicationSettings(); }
        finally { _gate.Release(); }
    }

    private static ApplicationSettings Normalize(ApplicationSettings settings)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var manualOrder = (settings.ManualSourceOrder ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.StartsWith("win:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(256)
            .ToArray();
        var hidden = (settings.ManuallyHiddenSources ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value.SourceId) && value.Kind == AudioSourceKind.WindowsSession &&
                            value.LastSeenUtc >= cutoff)
            .GroupBy(value => value.SourceId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(value => value.LastSeenUtc).First())
            .Take(256)
            .ToArray();
        return settings with
        {
            SourceSortMode = SourceSortModes.Manual,
            ManualSourceOrder = manualOrder,
            ManuallyHiddenSources = hidden,
            // Schema 7 removes the temporary browser-aggregate visibility exceptions. Retaining the
            // property keeps older JSON readable, while normalizing it to an empty array restores the
            // single settings-page switch as the source of truth.
            VisibleBrowserAggregates = [],
            SchemaVersion = 7
        };
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        settings = Normalize(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnsafeAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task WriteUnsafeAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = File.Create(temporaryPath))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, settings, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
