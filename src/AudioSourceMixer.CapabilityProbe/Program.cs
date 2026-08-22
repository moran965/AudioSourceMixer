using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;
using AudioSourceMixer.WindowsAudio;

var dataDirectory = Path.Combine(AppContext.BaseDirectory, "probe-data");
var logger = new RollingFileLogger(Path.Combine(dataDirectory, "logs"));
var journal = new JsonRollbackJournal(dataDirectory);

try
{
    var playIndex = Array.FindIndex(args, argument => string.Equals(argument, "--play-wav", StringComparison.OrdinalIgnoreCase));
    if (playIndex >= 0)
    {
        if (playIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: AudioSourceMixer.CapabilityProbe --play-wav <pcm-wave-path>");
            return 2;
        }
        var durationSeconds = 20;
        if (playIndex + 2 < args.Length &&
            (!int.TryParse(args[playIndex + 2], out durationSeconds) || durationSeconds < 5 || durationSeconds > 300))
        {
            Console.Error.WriteLine("Optional player duration must be between 5 and 300 seconds.");
            return 2;
        }
        using var player = new WaveOutTestPlayer(Path.GetFullPath(args[playIndex + 1]));
        Console.WriteLine($"PLAYER_READY pid={Environment.ProcessId}");
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
        return 0;
    }

    await using var audio = new WindowsAudioService(journal, logger);
    var device = await audio.InitializeAsync();

    var endpointMeterIndex = Array.FindIndex(args, argument => string.Equals(argument, "--endpoint-meter-samples", StringComparison.OrdinalIgnoreCase));
    if (endpointMeterIndex >= 0)
    {
        var durationSeconds = 6;
        if (endpointMeterIndex + 1 < args.Length &&
            (!int.TryParse(args[endpointMeterIndex + 1], out durationSeconds) || durationSeconds is < 2 or > 30))
        {
            Console.Error.WriteLine("Usage: AudioSourceMixer.CapabilityProbe --endpoint-meter-samples [2-30 seconds]");
            return 2;
        }
        var endpoints = (await audio.GetOutputDevicesAsync()).Where(item => !item.IsSystemDefault).ToArray();
        var endpointEvidence = endpoints.ToDictionary(item => item.Id, item => new
        {
            endpointIdSha256 = HashForEvidence(item.Id),
            nameSha256 = HashForEvidence(item.Name),
            item.IsDefaultMultimedia,
            item.IsDefaultCommunications
        }, StringComparer.Ordinal);
        var samples = new List<object>();
        var startedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var started = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"METER_READY endpointCount={endpoints.Length} durationSeconds={durationSeconds}");
        Console.Out.Flush();
        while (started.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            var sources = await audio.GetSourcesAsync();
            foreach (var endpoint in endpoints)
            {
                var peak = sources.Where(source => string.Equals(source.DeviceId, endpoint.Id, StringComparison.Ordinal))
                    .Select(source => source.Peak).DefaultIfEmpty(0).Max();
                samples.Add(new
                {
                    unixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    elapsedMilliseconds = Math.Round(started.Elapsed.TotalMilliseconds, 1),
                    endpointIdSha256 = endpointEvidence[endpoint.Id].endpointIdSha256,
                    peak = Math.Round(peak, 6)
                });
            }
            await Task.Delay(40);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            startedUnixMilliseconds,
            durationMilliseconds = Math.Round(started.Elapsed.TotalMilliseconds, 1),
            sampleIntervalMilliseconds = 40,
            endpoints = endpointEvidence.Values,
            samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    var coordinatedRouteIndex = Array.FindIndex(args, argument => string.Equals(argument, "--coordinated-route", StringComparison.OrdinalIgnoreCase));
    if (coordinatedRouteIndex >= 0)
    {
        if (coordinatedRouteIndex + 2 >= args.Length ||
            !uint.TryParse(args[coordinatedRouteIndex + 1], out var coordinatedProcessId))
        {
            Console.Error.WriteLine("Usage: AudioSourceMixer.CapabilityProbe --coordinated-route <processId> <endpointId|default> [holdSeconds]");
            return 2;
        }
        var requested = args[coordinatedRouteIndex + 2];
        if (string.Equals(requested, "default", StringComparison.OrdinalIgnoreCase)) requested = string.Empty;
        var holdSeconds = 3;
        if (coordinatedRouteIndex + 3 < args.Length &&
            (!int.TryParse(args[coordinatedRouteIndex + 3], out holdSeconds) || holdSeconds is < 1 or > 60))
        {
            Console.Error.WriteLine("holdSeconds must be between 1 and 60.");
            return 2;
        }
        var source = (await audio.GetSourcesAsync()).FirstOrDefault(item => item.ProcessId == coordinatedProcessId)
                     ?? throw new InvalidOperationException($"No audio session exists for PID {coordinatedProcessId}.");
        var before = (await audio.GetSourcesAsync()).Where(item => item.ProcessId == coordinatedProcessId).ToArray();
        var transitions = new System.Collections.Concurrent.ConcurrentQueue<AudioRouteResult>();
        audio.RoutingStateChanged += (_, state) => { if (state.ProcessId == coordinatedProcessId) transitions.Enqueue(state); };
        AudioRouteResult? requestedResult = null;
        IReadOnlyList<AudioSourceSnapshot> observed = [];
        try
        {
            requestedResult = await audio.SetOutputDeviceAsync(source.Id, requested, AudioRouteRequestSource.User);
            await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
            await audio.RefreshAsync();
            observed = (await audio.GetSourcesAsync()).Where(item => item.ProcessId == coordinatedProcessId).ToArray();
        }
        finally
        {
            await audio.RestoreAsync(source.Id);
            await audio.RefreshAsync();
        }
        var restored = (await audio.GetSourcesAsync()).Where(item => item.ProcessId == coordinatedProcessId).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            processId = coordinatedProcessId,
            requested,
            before,
            requestedResult,
            transitions = transitions.ToArray(),
            observed,
            restored
        }, new JsonSerializerOptions { WriteIndented = true }));
        return requestedResult?.State == AudioRoutingState.Failed ? 1 : 0;
    }

    var routeSetIndex = Array.FindIndex(args, argument => string.Equals(argument, "--set-route-policy", StringComparison.OrdinalIgnoreCase));
    if (routeSetIndex >= 0)
    {
        if (routeSetIndex + 2 >= args.Length || !uint.TryParse(args[routeSetIndex + 1], out var routeProcessId))
        {
            Console.Error.WriteLine("Usage: AudioSourceMixer.CapabilityProbe --set-route-policy <processId> <endpointId|default>");
            return 2;
        }
        var requested = args[routeSetIndex + 2];
        using var routing = new WindowsAppRoutingBackend(logger);
        var before = routing.GetPersistedRoutes(routeProcessId);
        AudioRouteTransaction transaction;
        if (string.Equals(requested, "default", StringComparison.OrdinalIgnoreCase))
        {
            // Match EarTrumpet's reset order. On some Windows builds the shared
            // Console/Multimedia preference rejects a null Console write until
            // Multimedia has first been cleared.
            try
            {
                routing.RestoreRoutes(routeProcessId,
                [
                    new PersistedAudioRoute(AudioRouteRole.Multimedia, null, 0),
                    new PersistedAudioRoute(AudioRouteRole.Console, null, 0),
                    new PersistedAudioRoute(AudioRouteRole.Communications, null, 0)
                ]);
                transaction = new AudioRouteTransaction(routeProcessId, string.Empty, routing.AbiVariant,
                    routing.WindowsBuild, before, [], true, null);
            }
            catch (Exception exception)
            {
                transaction = new AudioRouteTransaction(routeProcessId, string.Empty, routing.AbiVariant,
                    routing.WindowsBuild, before, [], false, exception.ToString());
            }
        }
        else
        {
            transaction = routing.SetPersistedRoutes(routeProcessId, requested);
        }
        var after = routing.GetPersistedRoutes(routeProcessId);
        Console.WriteLine(JsonSerializer.Serialize(new { routeProcessId, requested, before, transaction, after },
            new JsonSerializerOptions { WriteIndented = true }));
        return transaction.Succeeded ? 0 : 1;
    }

    var routeProbeIndex = Array.FindIndex(args, argument => string.Equals(argument, "--route-policy-probe", StringComparison.OrdinalIgnoreCase));
    if (routeProbeIndex >= 0)
    {
        if (routeProbeIndex + 1 >= args.Length || !uint.TryParse(args[routeProbeIndex + 1], out var routeProcessId))
        {
            Console.Error.WriteLine("Usage: AudioSourceMixer.CapabilityProbe --route-policy-probe <processId> [endpointId] [holdSeconds]");
            return 2;
        }

        var requestedEndpoint = routeProbeIndex + 2 < args.Length ? args[routeProbeIndex + 2] : null;
        var routeHoldSeconds = 2;
        if (routeProbeIndex + 3 < args.Length &&
            (!int.TryParse(args[routeProbeIndex + 3], out routeHoldSeconds) || routeHoldSeconds is < 1 or > 30))
        {
            Console.Error.WriteLine("Route probe holdSeconds must be between 1 and 30.");
            return 2;
        }

        using var routing = new WindowsAppRoutingBackend(logger);
        var before = routing.GetPersistedRoutes(routeProcessId);
        var beforeSessions = (await audio.GetSourcesAsync()).Where(source => source.ProcessId == routeProcessId)
            .Select(source => new { source.Id, source.DeviceId, source.State, source.Peak }).ToArray();
        AudioRouteTransaction? transaction = null;
        IReadOnlyList<PersistedAudioRoute>? applied = null;
        IReadOnlyList<PersistedAudioRoute>? restored = null;
        object? appliedSessions = null;
        object? restoredSessions = null;
        try
        {
            if (requestedEndpoint is not null)
            {
                transaction = routing.SetPersistedRoutes(routeProcessId,
                    string.Equals(requestedEndpoint, "default", StringComparison.OrdinalIgnoreCase) ? null : requestedEndpoint);
                if (!transaction.Succeeded) throw new InvalidOperationException(transaction.Error);
                applied = routing.GetPersistedRoutes(routeProcessId);
                await Task.Delay(TimeSpan.FromSeconds(routeHoldSeconds));
                await audio.RefreshAsync();
                appliedSessions = (await audio.GetSourcesAsync()).Where(source => source.ProcessId == routeProcessId)
                    .Select(source => new { source.Id, source.DeviceId, source.State, source.Peak }).ToArray();
            }
        }
        finally
        {
            if (requestedEndpoint is not null)
            {
                routing.RestoreRoutes(routeProcessId, before);
                restored = routing.GetPersistedRoutes(routeProcessId);
                await Task.Delay(TimeSpan.FromSeconds(2));
                await audio.RefreshAsync();
                restoredSessions = (await audio.GetSourcesAsync()).Where(source => source.ProcessId == routeProcessId)
                    .Select(source => new { source.Id, source.DeviceId, source.State, source.Peak }).ToArray();
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            operatingSystem = Environment.OSVersion.VersionString,
            processId = routeProcessId,
            routing.WindowsBuild,
            routing.AbiVariant,
            before,
            beforeSessions,
            requestedEndpoint,
            transaction,
            applied,
            appliedSessions,
            restored,
            restoredSessions,
            restoreMatched = restored is null || before.Select(route => route.EndpointId).SequenceEqual(restored.Select(route => route.EndpointId))
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    var holdStateIndex = Array.FindIndex(args, argument => string.Equals(argument, "--hold-state", StringComparison.OrdinalIgnoreCase));
    if (holdStateIndex >= 0)
    {
        if (holdStateIndex + 3 >= args.Length ||
            !uint.TryParse(args[holdStateIndex + 1], out var processId) ||
            !int.TryParse(args[holdStateIndex + 3], out var holdSeconds) ||
            holdSeconds < 5 || holdSeconds > 60)
        {
            Console.Error.WriteLine("Usage: AudioSourceMixer.CapabilityProbe --hold-state <processId> <normal|volume0|mute> <seconds>");
            return 2;
        }

        var requestedState = args[holdStateIndex + 2].ToLowerInvariant();
        if (requestedState is not ("normal" or "volume0" or "mute"))
        {
            Console.Error.WriteLine("State must be normal, volume0, or mute.");
            return 2;
        }

        IReadOnlyList<AudioSourceMixer.Core.Models.AudioSourceSnapshot> targets = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (targets.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await audio.RefreshAsync();
            var candidates = (await audio.GetSourcesAsync()).Where(source => source.ProcessId == processId).ToArray();
            targets = candidates.Where(source => source.State == AudioSourceMixer.Core.Models.AudioPlaybackState.Active).ToArray();
            if (targets.Count == 0)
            {
                var fallback = candidates.FirstOrDefault(source => source.DeviceId == device.Id) ?? candidates.FirstOrDefault();
                if (fallback is not null) targets = [fallback];
            }
            if (targets.Count == 0) await Task.Delay(100);
        }

        if (targets.Count == 0)
        {
            Console.Error.WriteLine($"No audio session was found for process {processId}.");
            return 3;
        }

        var changed = new List<AudioSourceMixer.Core.Models.AudioSourceSnapshot>();
        try
        {
            foreach (var target in targets)
            {
                if (requestedState == "volume0")
                {
                    await audio.SetMuteAsync(target.Id, false);
                    await audio.SetVolumeAsync(target.Id, 0f);
                    changed.Add(target);
                }
                else if (requestedState == "mute")
                {
                    await audio.SetMuteAsync(target.Id, true);
                    changed.Add(target);
                }
            }

            var appliedById = (await audio.GetSourcesAsync()).ToDictionary(source => source.Id);
            var details = targets.Select(target =>
            {
                var applied = appliedById[target.Id];
                return $"source={target.Id},device={target.DeviceId},originalVolume={target.Volume:F6},originalMuted={target.Muted},appliedVolume={applied.Volume:F6},appliedMuted={applied.Muted}";
            });
            Console.WriteLine($"STATE_READY pid={processId} state={requestedState} targetCount={targets.Count} targets=[{string.Join(';', details)}] holdSeconds={holdSeconds}");
            Console.Out.Flush();
            await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
        }
        finally
        {
            foreach (var target in changed)
            {
                try { await audio.RestoreAsync(target.Id); }
                catch (Exception restoreException) { Console.Error.WriteLine($"RESTORE_FAILED source={target.Id} exception={restoreException}"); }
            }
            var restoredById = (await audio.GetSourcesAsync()).ToDictionary(source => source.Id);
            var restoreDetails = targets.Select(target => restoredById.TryGetValue(target.Id, out var restored)
                ? $"source={target.Id},device={target.DeviceId},volume={restored.Volume:F6},muted={restored.Muted}"
                : $"source={target.Id},device={target.DeviceId},missing=true");
            Console.WriteLine($"STATE_RESTORED pid={processId} targets=[{string.Join(';', restoreDetails)}]");
            Console.Out.Flush();
        }
        return 0;
    }

    var mutePidIndex = Array.FindIndex(args, argument => string.Equals(argument, "--mute-pid", StringComparison.OrdinalIgnoreCase));
    if (mutePidIndex >= 0)
    {
        if (mutePidIndex + 1 >= args.Length || !uint.TryParse(args[mutePidIndex + 1], out var processId))
        {
            Console.Error.WriteLine("Usage: AudioSourceMixer.CapabilityProbe --mute-pid <processId>");
            return 2;
        }

        AudioSourceMixer.Core.Models.AudioSourceSnapshot? target = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (target is null && DateTimeOffset.UtcNow < deadline)
        {
            await audio.RefreshAsync();
            target = (await audio.GetSourcesAsync()).FirstOrDefault(source => source.ProcessId == processId);
            if (target is null) await Task.Delay(100);
        }

        if (target is null)
        {
            Console.Error.WriteLine($"No audio session was found for process {processId}.");
            return 3;
        }

        Console.WriteLine($"TARGET_READY pid={processId} source={target.Id} muted={target.Muted} volume={target.Volume:F3}");
        await Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            await audio.SetMuteAsync(target.Id, true);
            Console.WriteLine("TARGET_MUTED");
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await audio.RestoreAsync(target.Id);
            Console.WriteLine("TARGET_RESTORED");
        }
        await Task.Delay(TimeSpan.FromSeconds(3));
        return 0;
    }

    var outputDevices = await audio.GetOutputDevicesAsync();
    var results = await audio.ProbeAsync();
    object? exercise = null;
    if (args.Contains("--exercise", StringComparer.OrdinalIgnoreCase))
    {
        var target = results.Select(item => item.Snapshot).FirstOrDefault(item =>
            item.ProcessId != 0 && item.State != AudioSourceMixer.Core.Models.AudioPlaybackState.Active && item.Capabilities.SupportsStereoBalance);
        if (target is not null)
        {
            await audio.SetVolumeAsync(target.Id, 0.73f);
            await audio.SetMuteAsync(target.Id, true);
            await audio.SetBalanceAsync(target.Id, -1f);
            var modified = (await audio.GetSourcesAsync()).Single(item => item.Id == target.Id);
            await audio.RestoreAsync(target.Id);
            var restored = (await audio.GetSourcesAsync()).Single(item => item.Id == target.Id);
            exercise = new
            {
                target = target.DisplayName,
                original = new { target.Volume, target.Muted, target.ChannelVolumes },
                modified = new { modified.Volume, modified.Muted, modified.ChannelVolumes },
                restored = new { restored.Volume, restored.Muted, restored.ChannelVolumes },
                restoreMatched = Math.Abs(restored.Volume - target.Volume) < 0.0001f &&
                                 restored.Muted == target.Muted &&
                                 restored.ChannelVolumes.SequenceEqual(target.ChannelVolumes)
            };
        }
    }
    var report = new
    {
        timestamp = DateTimeOffset.Now,
        operatingSystem = Environment.OSVersion.VersionString,
        architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
        device,
        outputDevices,
        exercise,
        sessionNotificationRegistered = true,
        deviceNotificationRegistered = true,
        sessions = results.Select(result => new
        {
            result.Snapshot.DisplayName,
            result.Snapshot.ProcessId,
            result.Snapshot.ExecutablePath,
            result.Snapshot.SessionIdentifier,
            result.Snapshot.SessionInstanceIdentifier,
            result.Snapshot.State,
            result.Snapshot.Volume,
            result.Snapshot.Muted,
            result.Snapshot.Capabilities.ChannelCount,
            result.Snapshot.ChannelVolumes,
            result.Snapshot.Peak,
            result.MasterVolumeRoundTrip,
            result.MuteRoundTrip,
            result.ChannelVolumeRoundTrip,
            result.PeakMeterAvailable,
            result.Error
        })
    };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Capability probe failed: {exception}");
    return 1;
}

static string HashForEvidence(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
