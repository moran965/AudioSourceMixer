using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;
using AudioSourceMixer.WindowsAudio.Interop;

namespace AudioSourceMixer.WindowsAudio.Tests;

public sealed class WindowsAudioIntegrationTests
{
    [Fact]
    public async Task DefaultDeviceAndLiveSessionsCanBeProbed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "AudioSourceMixer.AudioTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var service = new WindowsAudioService(new JsonRollbackJournal(directory), new RollingFileLogger(directory));
            var device = await service.InitializeAsync();
            var sources = await service.GetSourcesAsync();
            var outputDevices = await service.GetOutputDevicesAsync();
            var results = await service.ProbeAsync();
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            Assert.NotEmpty(sources);
            Assert.True(outputDevices.Count >= 2);
            Assert.True(outputDevices[0].IsSystemDefault);
            Assert.All(outputDevices.Skip(1), output =>
            {
                Assert.True(output.IsAvailable);
                Assert.False(string.IsNullOrWhiteSpace(output.Id));
                Assert.False(string.IsNullOrWhiteSpace(output.Name));
                Assert.True(output.ChannelCount > 0);
                Assert.True(output.SampleRate > 0);
            });
            Assert.All(results, result => Assert.True(result.MasterVolumeRoundTrip));
            Assert.All(results, result => Assert.True(result.MuteRoundTrip));
            var activeEndpointIds = outputDevices.Skip(1).Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
            Assert.All(sources, source => Assert.Contains(source.DeviceId, activeEndpointIds));
            Assert.Contains(sources, source => source.DeviceId == device.Id);
            var ordinarySources = sources.Where(source => source.Kind == AudioSourceKind.WindowsSession &&
                                                          source.ProcessId != 0 &&
                                                          source.ProcessId != Environment.ProcessId)
                .ToArray();
            Assert.All(ordinarySources, source =>
            {
                Assert.False(source.Capabilities.SupportsExtendedGain);
                Assert.True(source.Capabilities.SupportsOutputRouting, source.Capabilities.Limitation);
                Assert.InRange(source.Volume, 0, 1);
            });
            Assert.All(sources.Where(source => source.ProcessId == 0), source =>
                Assert.False(source.Capabilities.SupportsOutputRouting));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(17133, 0)]
    [InlineData(17134, 1)]
    [InlineData(21389, 1)]
    [InlineData(21390, 2)]
    [InlineData(26200, 2)]
    public void AudioPolicyConfigAbiIsSelectedByWindowsBuild(int build, int expected)
        => Assert.Equal(expected, (int)AudioPolicyConfigAbiSelector.Select(build));

    [Fact]
    public void PersistedEndpointIdRoundTripsWithoutChangingTheMmDeviceId()
    {
        const string endpointId = "{0.0.0.00000000}.{d910e41a-5138-4596-b3e2-4b951e7e6744}";
        var packed = WindowsAppRoutingBackend.PackEndpointId(endpointId);
        Assert.StartsWith(@"\\?\SWD#MMDEVAPI#", packed, StringComparison.Ordinal);
        Assert.EndsWith("#{e6327cad-dcec-4949-ae8a-991e976a79d2}", packed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(endpointId, WindowsAppRoutingBackend.UnpackEndpointId(packed));
        Assert.Null(WindowsAppRoutingBackend.UnpackEndpointId(null));
    }

    [Fact]
    public void HStringSafeHandleReleasesExactlyOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = AudioPolicyConfigNative.WindowsCreateString("Audio Source Mixer", 18, out var value);
        Assert.True(result >= 0, $"WindowsCreateString failed with 0x{result:X8}.");
        Assert.False(value.IsInvalid);
        value.Dispose();
        Assert.True(value.IsClosed);
        value.Dispose();
        Assert.True(value.IsClosed);
    }

    [Fact]
    public void ThreeRoleRouteTransactionRollsEveryRoleBackWhenOneWriteFails()
    {
        var originals = Enum.GetValues<AudioRouteRole>().ToDictionary(role => role,
            role => new PersistedAudioRoute(role, $"original-{role}", 0));
        var writes = new List<(AudioRouteRole Role, string Endpoint)>();

        var transaction = WindowsAppRoutingBackend.ExecuteTransaction(42, "target", "test-abi", 26200,
            role => originals[role],
            (role, endpoint) =>
            {
                writes.Add((role, endpoint));
                return endpoint == "target" && role == AudioRouteRole.Console
                    ? unchecked((int)0x80004005) : 0;
            });

        Assert.False(transaction.Succeeded);
        Assert.Equal(2, transaction.AppliedRoutes.Count);
        Assert.Equal([AudioRouteRole.Multimedia, AudioRouteRole.Console],
            transaction.AppliedRoutes.Select(route => route.Role).ToArray());
        Assert.Equal(3, writes.Count(write => write.Endpoint.StartsWith("original-", StringComparison.Ordinal)));
        Assert.All(Enum.GetValues<AudioRouteRole>(), role =>
            Assert.Contains(writes, write => write == (role, $"original-{role}")));
        Assert.Contains("0x80004005", transaction.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HardwareRoutePersistsPolicyAndObservesRealThirdPartySessions()
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(Environment.GetEnvironmentVariable("AUDIO_SOURCE_MIXER_HARDWARE_TEST"), "1", StringComparison.Ordinal)) return;

        var directory = Path.Combine(Path.GetTempPath(), "AudioSourceMixer.HardwareTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var service = new WindowsAudioService(new JsonRollbackJournal(directory), new RollingFileLogger(directory));
        try
        {
            await service.InitializeAsync();
            var devices = await service.GetOutputDevicesAsync();
            var sources = await service.GetSourcesAsync();
            var source = sources.FirstOrDefault(item => item.ProcessId != 0 && item.State == AudioPlaybackState.Active &&
                                                        item.Capabilities.SupportsOutputRouting &&
                                                        (Path.GetFileName(item.ExecutablePath) ?? string.Empty).Contains("PotPlayer", StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException("Hardware test requires an active PotPlayer audio session.");
            var target = devices.Skip(1).FirstOrDefault(device => device.IsAvailable && device.Id != source.DeviceId)
                         ?? throw new InvalidOperationException("Hardware test requires a second active render endpoint.");

            var route = await service.SetOutputDeviceAsync(source.Id, target.Id);
            Assert.NotEqual(AudioRoutingState.Failed, route.State);
            Assert.Equal(target.Id, route.PersistedOutputDeviceId);
            Assert.Contains(route.State, new[]
            {
                AudioRoutingState.Applied,
                AudioRoutingState.Partial,
                AudioRoutingState.PendingStreamRestart
            });
            if (route.State == AudioRoutingState.Applied)
                Assert.All(route.ObservedOutputDeviceIds!, endpoint => Assert.Equal(target.Id, endpoint));
            await service.RestoreAsync(source.Id);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            AudioSourceSnapshot? restored = null;
            while (DateTime.UtcNow < deadline)
            {
                await service.RefreshAsync();
                restored = (await service.GetSourcesAsync()).FirstOrDefault(item =>
                    item.ProcessId == source.ProcessId && item.DeviceId == source.DeviceId && item.State == AudioPlaybackState.Active);
                if (restored is not null) break;
                await Task.Delay(150);
            }
            Assert.NotNull(restored);
        }
        finally
        {
            try { await service.RestoreAllAsync(); } catch { }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
