using System.Collections.Concurrent;
using AudioSourceMixer.WindowsAudio.Interop;

namespace AudioSourceMixer.WindowsAudio;

internal sealed class AudioWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public AudioWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioSourceMixer.CoreAudio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try { completion.TrySetResult(action()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }, cancellationToken);
        return completion.Task;
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        => InvokeAsync(() => { action(); return true; }, cancellationToken);

    private void Run()
    {
        var hresult = NativeMethods.CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            ComHelpers.ThrowIfFailed(hresult, "CoInitializeEx(COINIT_MULTITHREADED)");
            foreach (var action in _queue.GetConsumingEnumerable()) action();
        }
        finally
        {
            if (hresult >= 0) NativeMethods.CoUninitialize();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
