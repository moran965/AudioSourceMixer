using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Tests;

public sealed class ApplicationRouteCoordinatorTests
{
    private static readonly AudioApplicationInstanceKey App = new("player", 42, DateTimeOffset.UnixEpoch);
    private static readonly AudioSourceId Source = new("win:source");

    [Fact]
    public async Task SameTargetWhileInFlightUsesOneBackendCall()
    {
        await using var coordinator = new ApplicationRouteCoordinator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<ApplicationRouteObservation> Apply(ApplicationRouteRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return WaitAsync(token);
        }
        async Task<ApplicationRouteObservation> WaitAsync(CancellationToken token)
        {
            await release.Task.WaitAsync(token);
            return Observation("headphones", ["headphones"], backend: true);
        }

        var request = Request("headphones");
        var first = coordinator.RequestAsync(request, Apply, (_, _) => Task.FromResult(Observation("headphones", ["headphones"])));
        var second = coordinator.RequestAsync(request, Apply, (_, _) => Task.FromResult(Observation("headphones", ["headphones"])));
        Assert.Same(first, second);
        release.SetResult();
        Assert.Equal(AudioRoutingState.Applied, (await first).State);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RapidAToBToAIsLastWriterWinsAndOldGenerationCannotPublish()
    {
        await using var coordinator = new ApplicationRouteCoordinator();
        var published = new List<AudioRouteResult>();
        coordinator.StateChanged += (_, state) => published.Add(state);
        var calls = new List<string>();
        async Task<ApplicationRouteObservation> Apply(ApplicationRouteRequest request, CancellationToken _)
        {
            lock (calls) calls.Add(request.RequestedOutputDeviceId);
            await Task.Delay(request.RequestedOutputDeviceId == "A" ? 80 : 40);
            return Observation(request.RequestedOutputDeviceId, [request.RequestedOutputDeviceId], backend: true);
        }
        Task<ApplicationRouteObservation> Observe(ApplicationRouteRequest request, CancellationToken _)
            => Task.FromResult(Observation(request.RequestedOutputDeviceId, [request.RequestedOutputDeviceId]));

        var first = coordinator.RequestAsync(Request("A"), Apply, Observe);
        await Task.Delay(10);
        var second = coordinator.RequestAsync(Request("B"), Apply, Observe);
        await Task.Delay(10);
        var last = coordinator.RequestAsync(Request("A"), Apply, Observe);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        var result = await last;
        Assert.Equal("A", result.RequestedOutputDeviceId);
        Assert.Equal(AudioRoutingState.Applied, result.State);
        Assert.DoesNotContain(published, state => state.Generation < result.Generation && state.State == AudioRoutingState.Applied);
    }

    [Fact]
    public async Task PolicyWithoutActiveStreamIsPendingAndNeverRolledBack()
    {
        await using var coordinator = new ApplicationRouteCoordinator();
        var backendCalls = 0;
        var result = await coordinator.RequestAsync(Request("headphones"),
            (_, _) =>
            {
                backendCalls++;
                return Task.FromResult(Observation("headphones", [], backend: true));
            },
            (_, _) => Task.FromResult(Observation("headphones", [])));
        Assert.Equal(AudioRoutingState.PendingStreamRestart, result.State);
        Assert.Equal("headphones", result.PersistedOutputDeviceId);
        Assert.Equal(1, backendCalls);
    }

    [Fact]
    public async Task AppliedRequiresEveryActiveEndpointAndStabilityWindow()
    {
        await using var coordinator = new ApplicationRouteCoordinator();
        var result = await coordinator.RequestAsync(Request("headphones"),
            (_, _) => Task.FromResult(Observation("headphones", [])),
            (_, _) => Task.FromResult(Observation("headphones", [])));
        Assert.Equal(AudioRoutingState.PendingStreamRestart, result.State);

        var now = DateTimeOffset.UtcNow;
        result = coordinator.Observe(App, Source, Observation("headphones", ["headphones", "realtek"]), now)!;
        Assert.Equal(AudioRoutingState.Partial, result.State);
        result = coordinator.Observe(App, Source, Observation("headphones", ["headphones"]), now.AddMilliseconds(10))!;
        Assert.Equal(AudioRoutingState.PendingStreamRestart, result.State);
        result = coordinator.Observe(App, Source, Observation("headphones", ["headphones"]), now.AddMilliseconds(800))!;
        Assert.Equal(AudioRoutingState.Applied, result.State);
    }

    [Fact]
    public async Task DisconnectedTargetRetainsPersistedPolicyState()
    {
        await using var coordinator = new ApplicationRouteCoordinator();
        var result = await coordinator.RequestAsync(Request("headphones"),
            (_, _) => Task.FromResult(new ApplicationRouteObservation("headphones", ["realtek"], false, "realtek")),
            (_, _) => throw new InvalidOperationException("observe should not run"));
        Assert.Equal(AudioRoutingState.Disconnected, result.State);
        Assert.Equal("headphones", result.PersistedOutputDeviceId);
    }

    [Fact]
    public async Task SystemDefaultIsIdempotentAndReportedExplicitly()
    {
        await using var coordinator = new ApplicationRouteCoordinator();
        var calls = 0;
        var request = Request("");
        var first = await coordinator.RequestAsync(request,
            (_, _) => { calls++; return Task.FromResult(Observation("", ["realtek"], backend: true)); },
            (_, _) => throw new InvalidOperationException());
        var second = await coordinator.RequestAsync(request,
            (_, _) => { calls++; return Task.FromResult(Observation("", ["realtek"], backend: true)); },
            (_, _) => throw new InvalidOperationException());
        Assert.Equal(AudioRoutingState.SystemDefault, first.State);
        Assert.Equal(AudioRoutingState.SystemDefault, second.State);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UserIntentOutranksReconnectAndProfileRestore()
    {
        await using var coordinator = new ApplicationRouteCoordinator();
        var calls = new List<(string Endpoint, AudioRouteRequestSource Source)>();
        Task<ApplicationRouteObservation> Apply(ApplicationRouteRequest request, CancellationToken _)
        {
            calls.Add((request.RequestedOutputDeviceId, request.RequestSource));
            return Task.FromResult(Observation(request.RequestedOutputDeviceId, []));
        }
        Task<ApplicationRouteObservation> Observe(ApplicationRouteRequest request, CancellationToken _)
            => Task.FromResult(Observation(request.RequestedOutputDeviceId, []));

        await coordinator.RequestAsync(Request("profile", AudioRouteRequestSource.ProfileRestore), Apply, Observe);
        await coordinator.RequestAsync(Request("reconnect", AudioRouteRequestSource.DeviceReconnect), Apply, Observe);
        await coordinator.RequestAsync(Request("profile-again", AudioRouteRequestSource.ProfileRestore), Apply, Observe);
        await coordinator.RequestAsync(Request("user", AudioRouteRequestSource.User), Apply, Observe);
        var reconnectAfterUser = await coordinator.RequestAsync(
            Request("reconnect-after-user", AudioRouteRequestSource.DeviceReconnect), Apply, Observe);
        var profileAfterUser = await coordinator.RequestAsync(
            Request("profile-after-user", AudioRouteRequestSource.ProfileRestore), Apply, Observe);

        Assert.Equal([
            ("profile", AudioRouteRequestSource.ProfileRestore),
            ("reconnect", AudioRouteRequestSource.DeviceReconnect),
            ("user", AudioRouteRequestSource.User)
        ], calls);
        Assert.Equal("user", reconnectAfterUser.RequestedOutputDeviceId);
        Assert.Equal("user", profileAfterUser.RequestedOutputDeviceId);
    }

    private static ApplicationRouteRequest Request(string endpoint)
        => new(App, Source, endpoint, AudioRouteRequestSource.User, Guid.NewGuid().ToString("N"));

    private static ApplicationRouteRequest Request(string endpoint, AudioRouteRequestSource source)
        => new(App, Source, endpoint, source, Guid.NewGuid().ToString("N"));

    private static ApplicationRouteObservation Observation(string persisted, IReadOnlyList<string> observed,
        bool backend = false)
        => new(persisted, observed, true, "realtek", backend);
}
