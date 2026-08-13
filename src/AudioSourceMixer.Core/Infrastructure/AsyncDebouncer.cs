namespace AudioSourceMixer.Core.Infrastructure;

public sealed class AsyncDebouncer(TimeSpan delay) : IDisposable, IAsyncDisposable
{
    private CancellationTokenSource? _pending;
    private Task _running = Task.CompletedTask;
    private bool _disposed;

    public void Schedule(Func<CancellationToken, Task> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pending, current);
        previous?.Cancel();
        previous?.Dispose();
        var running = RunAsync(current, action);
        Volatile.Write(ref _running, running);
    }

    public async Task CancelPendingAsync()
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        pending?.Cancel();
        try { await Volatile.Read(ref _running).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { pending?.Dispose(); }
    }

    private async Task RunAsync(CancellationTokenSource source, Func<CancellationToken, Task> action)
    {
        try
        {
            await Task.Delay(delay, source.Token).ConfigureAwait(false);
            await action(source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var pending = Interlocked.Exchange(ref _pending, null);
        pending?.Cancel();
        pending?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CancelPendingAsync().ConfigureAwait(false);
    }
}
