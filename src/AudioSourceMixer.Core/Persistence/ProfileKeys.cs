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
    int SchemaVersion = 4);

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
                return loaded with
                {
                    BrowserOnboardingChoice = "existing-user",
                    OnboardingCompletedVersion = "0.2.1",
                    BrowserGuideDismissed = true,
                    SchemaVersion = 4
                };
            }
            return loaded with { SchemaVersion = 4 };
        }
        catch (System.Text.Json.JsonException) { return new ApplicationSettings(); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
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
