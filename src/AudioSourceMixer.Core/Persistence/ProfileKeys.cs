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
    public static string Normalize(string? value) => value == Manual ? Manual : Recent;
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
    string SourceSortMode = SourceSortModes.Recent,
    IReadOnlyList<string>? ManualSourceOrder = null,
    IReadOnlyList<HiddenSourceSetting>? ManuallyHiddenSources = null,
    bool HideBrowserAggregateSessions = true,
    int SchemaVersion = 5);

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
            await using var stream = File.OpenRead(_path);
            var loaded = await System.Text.Json.JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (loaded is null) return new ApplicationSettings();
            if (loaded.SchemaVersion < 4)
            {
                loaded = loaded with
                {
                    BrowserOnboardingChoice = "existing-user",
                    OnboardingCompletedVersion = "0.2.1",
                    BrowserGuideDismissed = true,
                    SchemaVersion = 5
                };
            }
            return Normalize(loaded);
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
            SourceSortMode = SourceSortModes.Normalize(settings.SourceSortMode),
            ManualSourceOrder = manualOrder,
            ManuallyHiddenSources = hidden,
            SchemaVersion = 5
        };
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        settings = Normalize(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            _gate.Release();
        }
    }
}
