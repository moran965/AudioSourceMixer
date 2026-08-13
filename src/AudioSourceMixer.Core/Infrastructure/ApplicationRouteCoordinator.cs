using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Infrastructure;

public sealed record ApplicationRouteRequest(
    AudioApplicationInstanceKey Application,
    AudioSourceId SourceId,
    string RequestedOutputDeviceId,
    AudioRouteRequestSource RequestSource,
    string CorrelationId);

public sealed record ApplicationRouteObservation(
    string PersistedOutputDeviceId,
    IReadOnlyList<string> ActiveOutputDeviceIds,
    bool RequestedDeviceAvailable,
    string SystemDefaultOutputDeviceId,
    bool BackendCalled = false,
    string? Error = null);

public sealed class ApplicationRouteCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan AppliedStabilityWindow = TimeSpan.FromMilliseconds(750);
    private readonly Dictionary<AudioApplicationInstanceKey, Slot> _slots = [];
    private readonly object _sync = new();
    private readonly Action<string>? _log;

    public ApplicationRouteCoordinator(Action<string>? log = null) => _log = log;

    public event EventHandler<AudioRouteResult>? StateChanged;

    public Task<AudioRouteResult> RequestAsync(
        ApplicationRouteRequest request,
        Func<ApplicationRouteRequest, CancellationToken, Task<ApplicationRouteObservation>> apply,
        Func<ApplicationRouteRequest, CancellationToken, Task<ApplicationRouteObservation>> observe,
        CancellationToken cancellationToken = default)
    {
        Slot slot;
        long generation;
        CancellationTokenSource operationCancellation;
        lock (_sync)
        {
            if (!_slots.TryGetValue(request.Application, out slot!))
            {
                slot = new Slot();
                _slots.Add(request.Application, slot);
            }

            if (!string.Equals(slot.RequestedOutputDeviceId, request.RequestedOutputDeviceId, StringComparison.Ordinal) &&
                (slot.HasUserIntent && request.RequestSource != AudioRouteRequestSource.User ||
                 RoutePriority(request.RequestSource) < RoutePriority(slot.RequestSource)))
            {
                Log(request, slot.Generation, "suppressed-lower-priority", slot.Result, false);
                if (slot.InFlight is not null) return slot.InFlight;
                if (slot.Result is not null) return Task.FromResult(slot.Result);
            }

            if (string.Equals(slot.RequestedOutputDeviceId, request.RequestedOutputDeviceId, StringComparison.Ordinal) &&
                slot.InFlight is { IsCompleted: false })
            {
                Log(request, slot.Generation, "idempotent-inflight", slot.Result, false);
                return slot.InFlight;
            }

            if (string.Equals(slot.RequestedOutputDeviceId, request.RequestedOutputDeviceId, StringComparison.Ordinal) &&
                slot.Result is { State: not AudioRoutingState.Failed and not AudioRoutingState.Disconnected })
            {
                Log(request, slot.Generation, "idempotent-complete", slot.Result, false);
                return Task.FromResult(slot.Result);
            }

            slot.Cancellation?.Cancel();
            slot.Cancellation?.Dispose();
            generation = ++slot.Generation;
            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            slot.Cancellation = operationCancellation;
            slot.RequestedOutputDeviceId = request.RequestedOutputDeviceId;
            slot.RequestSource = request.RequestSource;
            if (request.RequestSource == AudioRouteRequestSource.User) slot.HasUserIntent = true;
            slot.AppliedCandidateSince = null;
            slot.BackendCalledThisGeneration = false;
            slot.InFlight = RunAsync(slot, request, generation, operationCancellation.Token, apply, observe);
            return slot.InFlight;
        }
    }

    public AudioRouteResult? GetState(AudioApplicationInstanceKey application)
    {
        lock (_sync) return _slots.TryGetValue(application, out var slot) ? slot.Result : null;
    }

    public AudioRouteResult? Observe(
        AudioApplicationInstanceKey application,
        AudioSourceId sourceId,
        ApplicationRouteObservation observation,
        DateTimeOffset? observedAt = null)
    {
        AudioRouteResult? result;
        var changed = false;
        lock (_sync)
        {
            if (!_slots.TryGetValue(application, out var slot) || slot.Result is null) return null;
            var previous = slot.Result;
            var request = new ApplicationRouteRequest(application, sourceId, slot.RequestedOutputDeviceId,
                slot.Result.RequestSource, slot.Result.CorrelationId ?? string.Empty);
            result = Evaluate(slot, request, slot.Generation, observation, observedAt ?? DateTimeOffset.UtcNow);
            changed = !Equivalent(previous, result);
            if (changed) Log(request, slot.Generation, "state", result, observation.BackendCalled);
        }
        if (changed) Publish(result);
        return result;
    }

    public void Forget(AudioApplicationInstanceKey application)
    {
        lock (_sync)
        {
            if (!_slots.Remove(application, out var slot)) return;
            slot.Generation++;
            slot.Cancellation?.Cancel();
            _ = DisposeForgottenSlotAsync(slot);
        }
    }

    private static async Task DisposeForgottenSlotAsync(Slot slot)
    {
        try { if (slot.InFlight is not null) await slot.InFlight.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
        slot.Cancellation?.Dispose();
        slot.Gate.Dispose();
    }

    public async Task CancelAllAsync()
    {
        Task[] tasks;
        lock (_sync)
        {
            foreach (var slot in _slots.Values) slot.Cancellation?.Cancel();
            tasks = _slots.Values.Select(slot => slot.InFlight).Where(task => task is not null).Cast<Task>().ToArray();
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task<AudioRouteResult> RunAsync(
        Slot slot,
        ApplicationRouteRequest request,
        long generation,
        CancellationToken cancellationToken,
        Func<ApplicationRouteRequest, CancellationToken, Task<ApplicationRouteObservation>> apply,
        Func<ApplicationRouteRequest, CancellationToken, Task<ApplicationRouteObservation>> observe)
    {
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await apply(request, cancellationToken).ConfigureAwait(false);
            var result = UpdateIfCurrent(slot, request, generation, observation);
            if (result.State is AudioRoutingState.Failed or AudioRoutingState.Disconnected or AudioRoutingState.SystemDefault)
                return result;

            var deadline = DateTimeOffset.UtcNow.Add(AppliedStabilityWindow + TimeSpan.FromMilliseconds(600));
            while (DateTimeOffset.UtcNow < deadline && result.State != AudioRoutingState.Applied)
            {
                await Task.Delay(125, cancellationToken).ConfigureAwait(false);
                observation = await observe(request, cancellationToken).ConfigureAwait(false);
                result = UpdateIfCurrent(slot, request, generation, observation);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke($"route application={request.Application} source={request.SourceId} generation={generation} " +
                         $"requested={Format(request.RequestedOutputDeviceId)} requestSource={request.RequestSource} cancelled=true");
            throw;
        }
        catch (Exception exception)
        {
            var failed = new ApplicationRouteObservation(string.Empty, [], true, string.Empty,
                Error: exception.ToString());
            return UpdateIfCurrent(slot, request, generation, failed);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private AudioRouteResult UpdateIfCurrent(Slot slot, ApplicationRouteRequest request, long generation,
        ApplicationRouteObservation observation)
    {
        AudioRouteResult result;
        var changed = false;
        lock (_sync)
        {
            if (slot.Generation != generation) throw new OperationCanceledException("Route generation was superseded.");
            var previous = slot.Result;
            result = Evaluate(slot, request, generation, observation, DateTimeOffset.UtcNow);
            changed = previous is null || !Equivalent(previous, result);
            if (changed) Log(request, generation, "state", result, observation.BackendCalled);
        }
        if (changed) Publish(result);
        return result;
    }

    private AudioRouteResult Evaluate(Slot slot, ApplicationRouteRequest request, long generation,
        ApplicationRouteObservation observation, DateTimeOffset now)
    {
        var requested = request.RequestedOutputDeviceId;
        var observed = observation.ActiveOutputDeviceIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        AudioRoutingState state;
        string? error = observation.Error;

        if (!string.IsNullOrWhiteSpace(error))
        {
            state = AudioRoutingState.Failed;
            slot.AppliedCandidateSince = null;
        }
        else if (string.IsNullOrEmpty(requested))
        {
            state = string.IsNullOrEmpty(observation.PersistedOutputDeviceId)
                ? AudioRoutingState.SystemDefault : AudioRoutingState.Failed;
            error = state == AudioRoutingState.Failed ? "Persisted route was not cleared." : null;
            slot.AppliedCandidateSince = null;
        }
        else if (!observation.RequestedDeviceAvailable)
        {
            state = AudioRoutingState.Disconnected;
            error = "Requested output device is disconnected; the persisted policy was retained.";
            slot.AppliedCandidateSince = null;
        }
        else if (!string.Equals(observation.PersistedOutputDeviceId, requested, StringComparison.Ordinal))
        {
            state = AudioRoutingState.Failed;
            error = $"Persisted route readback did not match requested endpoint {requested}.";
            slot.AppliedCandidateSince = null;
        }
        else if (observed.Length == 0)
        {
            state = AudioRoutingState.PendingStreamRestart;
            error = "Routing policy is set; start playback or restart the stream to migrate it.";
            slot.AppliedCandidateSince = null;
        }
        else if (observed.All(id => string.Equals(id, requested, StringComparison.Ordinal)))
        {
            slot.AppliedCandidateSince ??= now;
            if (now - slot.AppliedCandidateSince >= AppliedStabilityWindow)
            {
                state = AudioRoutingState.Applied;
                error = null;
            }
            else
            {
                state = AudioRoutingState.PendingStreamRestart;
                error = "All active streams reached the target; confirming stability.";
            }
        }
        else if (observed.Any(id => string.Equals(id, requested, StringComparison.Ordinal)))
        {
            state = AudioRoutingState.Partial;
            error = "Only some active streams have migrated; pause/resume playback or reopen the application.";
            slot.AppliedCandidateSince = null;
        }
        else
        {
            state = AudioRoutingState.PendingStreamRestart;
            error = "Routing policy is set, but active streams still use another endpoint; pause/resume playback or reopen the application.";
            slot.AppliedCandidateSince = null;
        }

        var effective = state == AudioRoutingState.SystemDefault
            ? observation.SystemDefaultOutputDeviceId
            : observed.Length == 1 ? observed[0] : string.Empty;
        slot.BackendCalledThisGeneration |= observation.BackendCalled;
        var result = new AudioRouteResult(request.SourceId, request.Application.ProcessId, requested, effective,
            state, error, request.CorrelationId, generation, request.RequestSource,
            observation.PersistedOutputDeviceId, observed, slot.BackendCalledThisGeneration);
        slot.Result = result;
        return result;
    }

    private void Publish(AudioRouteResult? result)
    {
        if (result is not null) StateChanged?.Invoke(this, result);
    }

    private void Log(ApplicationRouteRequest request, long generation, string action, AudioRouteResult? result, bool backendCalled)
        => _log?.Invoke($"route application={request.Application} source={request.SourceId} generation={generation} " +
                        $"requestSource={request.RequestSource} correlation={request.CorrelationId} action={action} " +
                        $"requested={Format(request.RequestedOutputDeviceId)} persisted={Format(result?.PersistedOutputDeviceId)} " +
                        $"observed=[{string.Join(',', result?.ObservedOutputDeviceIds ?? [])}] state={result?.State} " +
                        $"backendCall={backendCalled} cancelled=false error={result?.Error}");

    private static string Format(string? endpointId) => string.IsNullOrEmpty(endpointId) ? "system-default" : endpointId;

    private static int RoutePriority(AudioRouteRequestSource source) => source switch
    {
        AudioRouteRequestSource.User => 3,
        AudioRouteRequestSource.DeviceReconnect => 2,
        AudioRouteRequestSource.ProfileRestore => 1,
        _ => 4
    };

    private static bool Equivalent(AudioRouteResult left, AudioRouteResult right)
        => left.State == right.State && left.Error == right.Error &&
           left.RequestedOutputDeviceId == right.RequestedOutputDeviceId &&
           left.EffectiveOutputDeviceId == right.EffectiveOutputDeviceId &&
           left.PersistedOutputDeviceId == right.PersistedOutputDeviceId &&
           (left.ObservedOutputDeviceIds ?? []).SequenceEqual(right.ObservedOutputDeviceIds ?? [], StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        await CancelAllAsync().ConfigureAwait(false);
        lock (_sync)
        {
            foreach (var slot in _slots.Values)
            {
                slot.Cancellation?.Dispose();
                slot.Gate.Dispose();
            }
            _slots.Clear();
        }
    }

    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Generation { get; set; }
        public string RequestedOutputDeviceId { get; set; } = string.Empty;
        public AudioRouteRequestSource RequestSource { get; set; } = AudioRouteRequestSource.ProfileRestore;
        public bool HasUserIntent { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public Task<AudioRouteResult>? InFlight { get; set; }
        public AudioRouteResult? Result { get; set; }
        public DateTimeOffset? AppliedCandidateSince { get; set; }
        public bool BackendCalledThisGeneration { get; set; }
    }
}
