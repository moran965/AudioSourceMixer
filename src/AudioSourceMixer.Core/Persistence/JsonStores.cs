using System.Text.Json;
using AudioSourceMixer.Core.Abstractions;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Persistence;

public sealed class JsonAudioProfileStore(string directory) : IAudioProfileStore
{
    public const int CurrentSchemaVersion = 3;
    private readonly string _path = Path.Combine(directory, "profiles.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyDictionary<string, AudioSourceProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, AudioSourceProfile>();
            var bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> json = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                json = bytes.AsMemory(3);
            using var document = JsonDocument.Parse(json);
            Dictionary<string, AudioSourceProfile> profiles;
            var migrated = false;
            if (document.RootElement.TryGetProperty("schemaVersion", out var schemaElement))
            {
                var schemaVersion = schemaElement.GetInt32();
                if (schemaVersion is not (2 or CurrentSchemaVersion))
                    throw new InvalidDataException($"Unsupported audio profile schema version {schemaVersion}.");
                profiles = document.RootElement.Deserialize<AudioProfileDocument>(Options)?.Profiles ?? [];
                migrated = schemaVersion != CurrentSchemaVersion;
            }
            else
            {
                profiles = document.RootElement.Deserialize<Dictionary<string, AudioSourceProfile>>(Options) ?? [];
                profiles = profiles.ToDictionary(item => item.Key, item => Normalize(item.Value));
                migrated = true;
            }

            var beforeNormalization = JsonSerializer.Serialize(profiles, Options);
            var normalized = profiles.ToDictionary(item => item.Key, item => Normalize(item.Value));
            migrated |= !string.Equals(beforeNormalization, JsonSerializer.Serialize(normalized, Options), StringComparison.Ordinal);
            profiles = normalized;
            if (migrated) await WriteUnsafeAsync(profiles, cancellationToken).ConfigureAwait(false);
            return profiles;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return new Dictionary<string, AudioSourceProfile>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AudioSourceProfile profile, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = new Dictionary<string, AudioSourceProfile>(await LoadAsync(cancellationToken).ConfigureAwait(false));
            profiles[profile.StableKey] = Normalize(profile) with { UpdatedAt = DateTimeOffset.UtcNow };
            await WriteAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { if (File.Exists(_path)) File.Delete(_path); }
            finally { _gate.Release(); }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task RemoveAsync(string stableKey, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = new Dictionary<string, AudioSourceProfile>(await LoadAsync(cancellationToken).ConfigureAwait(false));
            if (!profiles.Remove(stableKey)) return;
            await WriteAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutationGate.Release(); }
    }

    private async Task WriteAsync(Dictionary<string, AudioSourceProfile> profiles, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnsafeAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteUnsafeAsync(Dictionary<string, AudioSourceProfile> profiles, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, new AudioProfileDocument(CurrentSchemaVersion, profiles), Options, cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(temporaryPath, _path, true);
    }

    private static AudioSourceProfile Normalize(AudioSourceProfile profile)
    {
        var maximum = profile.SourceKind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab ? 2f : 1f;
        var volume = float.IsFinite(profile.Volume) ? Math.Clamp(profile.Volume, 0, maximum) : 1f;
        _ = AudioMath.BalanceToGains(profile.Balance);
        var effects = profile.SourceKind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab
            ? EqualizerCatalog.Normalize(profile.Effects)
            : null;
        return profile with { Volume = volume, Effects = effects };
    }

    private sealed record AudioProfileDocument(int SchemaVersion, Dictionary<string, AudioSourceProfile> Profiles);
}

public sealed class JsonRollbackJournal(string directory) : IRollbackJournal
{
    private readonly string _path = Path.Combine(directory, "rollback.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<AudioRollbackEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertAsync(AudioRollbackEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
            entries.RemoveAll(item => item.SourceId == entry.SourceId);
            entries.Add(entry);
            await WriteUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(AudioSourceId sourceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).Where(item => item.SourceId != sourceId).ToList();
            await WriteUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (File.Exists(_path)) File.Delete(_path); }
        finally { _gate.Release(); }
    }

    private async Task<List<AudioRollbackEntry>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<AudioRollbackEntry>>(stream, Options, cancellationToken)
                       .ConfigureAwait(false) ?? [];
        }
        catch (JsonException) { return []; }
    }

    private async Task WriteUnsafeAsync(List<AudioRollbackEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, entries, Options, cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporaryPath, _path, true);
    }
}
