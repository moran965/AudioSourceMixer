using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;

namespace AudioSourceMixer.Core.Tests;

public sealed class IdentityAndPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AudioSourceMixer.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void BrowserSourceIdIncludesBrowserAndTab()
    {
        Assert.NotEqual(AudioSourceId.ForBrowserTab("chrome", 42), AudioSourceId.ForBrowserTab("edge", 42));
        Assert.Equal("chrome:42", AudioSourceId.ForBrowserTab("chrome", 42).Value);
    }

    [Fact]
    public void BrowserSourceIdRejectsUnsupportedBrowser()
        => Assert.Throws<ArgumentException>(() => AudioSourceId.ForBrowserTab("firefox", 1));

    [Fact]
    public void RestoreIdentityRejectsProcessReuse()
    {
        var original = new AudioSessionIdentity("device", "session", "instance", 10, "C:\\app.exe", DateTimeOffset.UnixEpoch);
        var reused = original with { ProcessId = 10, ProcessStartTimeUtc = DateTimeOffset.UnixEpoch.AddHours(1) };
        Assert.False(original.IsSafeRestoreMatch(reused));
    }

    [Fact]
    public void RestoreIdentityRequiresDeviceAndInstance()
    {
        var original = new AudioSessionIdentity("device", "session", "instance", 10, null, null);
        Assert.False(original.IsSafeRestoreMatch(original with { DeviceId = "other" }));
        Assert.False(original.IsSafeRestoreMatch(original with { SessionInstanceIdentifier = "other" }));
        Assert.True(original.IsSafeRestoreMatch(original));
    }

    [Fact]
    public void WindowsSourceIdentityIncludesEndpointButStableProfileSurvivesEndpointMove()
    {
        Assert.NotEqual(
            AudioSourceId.ForWindowsSession("headphones", "session-instance"),
            AudioSourceId.ForWindowsSession("realtek", "session-instance"));

        var headphones = new AudioSessionIdentity("headphones", "session", "one", 42,
            "C:\\Player\\player.exe", DateTimeOffset.UnixEpoch);
        var realtek = headphones with { DeviceId = "realtek", SessionInstanceIdentifier = "two" };
        Assert.Equal(headphones.StableProfileKey, realtek.StableProfileKey);
    }

    [Fact]
    public async Task ProfileStoreRoundTripsAndClears()
    {
        var store = new JsonAudioProfileStore(_directory);
        await store.SaveAsync(new AudioSourceProfile("stable", 0.4f, -1, true));
        var loaded = await store.LoadAsync();
        Assert.Equal(0.4f, loaded["stable"].Volume);
        Assert.True(loaded["stable"].Muted);
        await store.ClearAsync();
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task LegacyProfileSchemaMigratesUnitVolumeToV2GainWithoutChangingMeaning()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "profiles.json"),
            """{"stable":{"stableKey":"stable","volume":0.75,"balance":0.25,"muted":false,"autoApply":true}}""");
        var store = new JsonAudioProfileStore(_directory);
        var loaded = await store.LoadAsync();
        Assert.Equal(0.75f, loaded["stable"].Volume);
        Assert.Equal(75f, loaded["stable"].Volume * 100f);
        var migrated = await File.ReadAllTextAsync(Path.Combine(_directory, "profiles.json"));
        Assert.Contains("\"schemaVersion\": 2", migrated);
        Assert.Contains("\"profiles\"", migrated);
    }

    [Fact]
    public async Task LegacyProfileSchemaWithUtf8BomAlsoMigrates()
    {
        Directory.CreateDirectory(_directory);
        var json = """{"stable":{"stableKey":"stable","volume":0.75,"balance":0,"muted":false}}""";
        await File.WriteAllBytesAsync(Path.Combine(_directory, "profiles.json"),
            [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(json)]);

        var loaded = await new JsonAudioProfileStore(_directory).LoadAsync();

        Assert.Equal(0.75f, loaded["stable"].Volume);
        Assert.Contains("\"schemaVersion\": 2", await File.ReadAllTextAsync(Path.Combine(_directory, "profiles.json")));
    }

    [Fact]
    public async Task BrowserProfileRoundTripsTwoHundredPercentAndOutputPreference()
    {
        var store = new JsonAudioProfileStore(_directory);
        await store.SaveAsync(new AudioSourceProfile("stable", 1.5f, 0, false,
            OutputDeviceId: "endpoint", OutputDeviceName: "USB DAC", SourceKind: AudioSourceKind.ChromeTab));
        var loaded = (await store.LoadAsync())["stable"];
        Assert.Equal(1.5f, loaded.Volume);
        Assert.Equal("endpoint", loaded.OutputDeviceId);
        Assert.Equal("USB DAC", loaded.OutputDeviceName);
    }

    [Fact]
    public async Task OrdinaryV2ProfileAboveOneIsClampedAndRewritten()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "profiles.json"),
            """{"schemaVersion":2,"profiles":{"ordinary":{"stableKey":"ordinary","volume":2,"balance":0,"muted":false}}}""");
        var store = new JsonAudioProfileStore(_directory);
        var loaded = await store.LoadAsync();
        Assert.Equal(1f, loaded["ordinary"].Volume);
        Assert.Contains("\"volume\": 1", await File.ReadAllTextAsync(Path.Combine(_directory, "profiles.json")));
    }

    [Fact]
    public async Task RemoveDeletesOnlyRequestedStableProfile()
    {
        var store = new JsonAudioProfileStore(_directory);
        await store.SaveAsync(new AudioSourceProfile("one", 0.4f, 0, false));
        await store.SaveAsync(new AudioSourceProfile("two", 0.8f, 0, false));
        await store.RemoveAsync("one");
        var loaded = await store.LoadAsync();
        Assert.False(loaded.ContainsKey("one"));
        Assert.True(loaded.ContainsKey("two"));
    }

    [Fact]
    public async Task RollbackJournalUpsertsAndRemoves()
    {
        var journal = new JsonRollbackJournal(_directory);
        var identity = new AudioSessionIdentity("device", "session", "instance", 10, null, null);
        var id = AudioSourceId.ForWindowsSession("device", "instance");
        await journal.UpsertAsync(new AudioRollbackEntry(id, identity, 0.5f, false, [1, 1], DateTimeOffset.UtcNow));
        await journal.UpsertAsync(new AudioRollbackEntry(id, identity, 0.8f, true, [1, 0], DateTimeOffset.UtcNow));
        var entries = await journal.LoadAsync();
        Assert.Single(entries);
        Assert.Equal(0.8f, entries[0].MasterVolume);
        await journal.RemoveAsync(id);
        Assert.Empty(await journal.LoadAsync());
    }

    [Fact]
    public async Task RollbackJournalPersistsPerRoleRoutes()
    {
        var journal = new JsonRollbackJournal(_directory);
        var identity = new AudioSessionIdentity("headphones", "session", "instance", 42,
            "C:\\Player\\player.exe", DateTimeOffset.UnixEpoch);
        var id = AudioSourceId.ForWindowsSession(identity.DeviceId, identity.SessionInstanceIdentifier);
        await journal.UpsertAsync(new AudioRollbackEntry(id, identity, 0.4f, true, [0.4f, 0.2f], DateTimeOffset.UtcNow,
            [new AudioPersistedRouteState("Console", "headphones"),
             new AudioPersistedRouteState("Multimedia", "headphones"),
             new AudioPersistedRouteState("Communications", null)],
            RequestedOutputDeviceId: "realtek"));

        var restored = Assert.Single(await journal.LoadAsync());
        Assert.Equal(3, restored.OriginalRoutes!.Count);
        Assert.Equal("realtek", restored.RequestedOutputDeviceId);
    }

    [Fact]
    public async Task SettingsStoreRoundTrips()
    {
        var store = new JsonApplicationSettingsStore(_directory);
        var expected = new ApplicationSettings(false, false, true, false);
        await store.SaveAsync(expected);
        Assert.Equal(expected, await store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
