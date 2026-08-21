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
        Assert.Contains("\"schemaVersion\": 3", migrated);
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
        Assert.Contains("\"schemaVersion\": 3", await File.ReadAllTextAsync(Path.Combine(_directory, "profiles.json")));
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
        Assert.False(loaded.Effects!.Enabled);
    }

    [Fact]
    public async Task V2BrowserProfileMigratesToSchema3WithEqualizerOff()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "profiles.json"),
            """{"schemaVersion":2,"profiles":{"browser":{"stableKey":"browser","volume":1.25,"balance":0,"muted":false,"sourceKind":1}}}""");
        var loaded = (await new JsonAudioProfileStore(_directory).LoadAsync())["browser"];
        Assert.False(loaded.Effects!.Enabled);
        Assert.All(loaded.Effects.Bands, band => Assert.Equal(0, band.GainDb));
        Assert.Contains("\"schemaVersion\": 3", await File.ReadAllTextAsync(Path.Combine(_directory, "profiles.json")));
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
        var loaded = await store.LoadAsync();
        Assert.Equal(expected.CloseToTray, loaded.CloseToTray);
        Assert.Equal(expected.AutoApplyProfiles, loaded.AutoApplyProfiles);
        Assert.Equal(expected.RememberProfiles, loaded.RememberProfiles);
        Assert.Equal(expected.ShowInactiveSessions, loaded.ShowInactiveSessions);
        Assert.Equal(SourceSortModes.Manual, loaded.SourceSortMode);
        Assert.Empty(loaded.ManualSourceOrder!);
        Assert.Empty(loaded.ManuallyHiddenSources!);
        Assert.True(loaded.HideBrowserAggregateSessions);
        Assert.Empty(loaded.VisibleBrowserAggregates!);
        Assert.Equal(8, loaded.SchemaVersion);
    }

    [Fact]
    public async Task FreshSettingsRequestOptionalBrowserOnboarding()
    {
        var loaded = await new JsonApplicationSettingsStore(_directory).LoadAsync();
        Assert.Equal(8, loaded.SchemaVersion);
        Assert.Equal("undecided", loaded.BrowserOnboardingChoice);
        Assert.Null(loaded.OnboardingCompletedVersion);
        Assert.False(loaded.BrowserGuideDismissed);
    }

    [Fact]
    public async Task ExistingSettingsMigrateWithoutForcingNewOnboarding()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"),
            """{"CloseToTray":false,"ShowInactiveSessions":false,"SchemaVersion":3}""");
        var loaded = await new JsonApplicationSettingsStore(_directory).LoadAsync();
        Assert.Equal(8, loaded.SchemaVersion);
        Assert.False(loaded.CloseToTray);
        Assert.False(loaded.ShowInactiveSessions);
        Assert.Equal("existing-user", loaded.BrowserOnboardingChoice);
        Assert.Equal("0.2.1", loaded.OnboardingCompletedVersion);
        Assert.True(loaded.BrowserGuideDismissed);
        Assert.Equal(SourceSortModes.Manual, loaded.SourceSortMode);
        Assert.Empty(loaded.ManualSourceOrder!);
        Assert.Empty(loaded.ManuallyHiddenSources!);
        Assert.True(loaded.HideBrowserAggregateSessions);
    }

    [Fact]
    public async Task SchemaFourSettingsGainPresentationDefaultsWithoutChangingOnboarding()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"),
            """{"BrowserOnboardingChoice":"not-now","OnboardingCompletedVersion":"0.2.2","BrowserGuideDismissed":true,"SchemaVersion":4}""");

        var loaded = await new JsonApplicationSettingsStore(_directory).LoadAsync();

        Assert.Equal(8, loaded.SchemaVersion);
        Assert.Equal("not-now", loaded.BrowserOnboardingChoice);
        Assert.Equal("0.2.2", loaded.OnboardingCompletedVersion);
        Assert.True(loaded.BrowserGuideDismissed);
        Assert.Equal(SourceSortModes.Manual, loaded.SourceSortMode);
        Assert.Empty(loaded.ManualSourceOrder!);
        Assert.Empty(loaded.ManuallyHiddenSources!);
        Assert.True(loaded.HideBrowserAggregateSessions);
    }

    [Fact]
    public async Task PresentationSettingsRoundTripOnlySafeBoundedWindowsIdentities()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new JsonApplicationSettingsStore(_directory);
        await store.SaveAsync(new ApplicationSettings(
            SourceSortMode: SourceSortModes.Manual,
            ManualSourceOrder: ["win:session-b", "tab:private-title", "win:session-a", "win:session-b"],
            ManuallyHiddenSources:
            [
                new HiddenSourceSetting("win:session-b", AudioSourceKind.WindowsSession, now),
                new HiddenSourceSetting("tab:private-title", AudioSourceKind.ChromeTab, now)
            ],
            HideBrowserAggregateSessions: false));

        var loaded = await store.LoadAsync();

        Assert.Equal(SourceSortModes.Manual, loaded.SourceSortMode);
        Assert.Equal(["win:session-b", "win:session-a"], loaded.ManualSourceOrder);
        Assert.Equal("win:session-b", Assert.Single(loaded.ManuallyHiddenSources!).SourceId);
        Assert.False(loaded.HideBrowserAggregateSessions);
        Assert.Equal(8, loaded.SchemaVersion);
    }

    [Fact]
    public async Task SchemaSixBrowserVisibilityExceptionsAreClearedSafely()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"),
            """{"HideBrowserAggregateSessions":true,"VisibleBrowserAggregates":["edge","chrome"],"SchemaVersion":6}""");

        var loaded = await new JsonApplicationSettingsStore(_directory).LoadAsync();

        Assert.True(loaded.HideBrowserAggregateSessions);
        Assert.Empty(loaded.VisibleBrowserAggregates!);
        Assert.Equal(8, loaded.SchemaVersion);
        var rewritten = await File.ReadAllTextAsync(Path.Combine(_directory, "settings.json"));
        Assert.Contains("\"SchemaVersion\":8", rewritten);
        Assert.Contains("\"VisibleBrowserAggregates\":[]", rewritten);
    }

    [Fact]
    public async Task SchemaSevenSettingsGainChineseLanguageWithoutChangingExistingChoices()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"),
            """{"CloseToTray":false,"RememberProfiles":false,"ManualSourceOrder":["win:session-a"],"Language":null,"SchemaVersion":7}""");

        var loaded = await new JsonApplicationSettingsStore(_directory).LoadAsync();

        Assert.Equal(8, loaded.SchemaVersion);
        Assert.Equal("zh-CN", loaded.Language);
        Assert.False(loaded.CloseToTray);
        Assert.False(loaded.RememberProfiles);
        Assert.Equal(["win:session-a"], loaded.ManualSourceOrder);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public async Task FreshSettingsConsumeInstallerLanguageBootstrapOnce(string language)
    {
        Directory.CreateDirectory(_directory);
        var bootstrap = Path.Combine(_directory, "initial-language.json");
        await File.WriteAllTextAsync(bootstrap, $$"""{"language":"{{language}}"}""");

        var loaded = await new JsonApplicationSettingsStore(_directory).LoadAsync();

        Assert.Equal(language, loaded.Language);
        Assert.Equal(8, loaded.SchemaVersion);
        Assert.False(File.Exists(bootstrap));
        Assert.True(File.Exists(Path.Combine(_directory, "settings.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
