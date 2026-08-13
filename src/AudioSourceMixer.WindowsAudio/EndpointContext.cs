using AudioSourceMixer.WindowsAudio.Interop;

namespace AudioSourceMixer.WindowsAudio;

internal sealed class EndpointContext : IDisposable
{
    private readonly IAudioSessionNotification _notification;
    private bool _disposed;

    public EndpointContext(IMMDevice device, string endpointId, Action sessionsChanged)
    {
        Device = device;
        Id = endpointId;
        object? activated = null;
        try
        {
            var interfaceId = NativeMethods.AudioSessionManager2InterfaceId;
            ComHelpers.ThrowIfFailed(device.Activate(ref interfaceId, ClsCtx.All, IntPtr.Zero, out activated),
                $"IMMDevice.Activate(IAudioSessionManager2) for {endpointId}");
            SessionManager = (IAudioSessionManager2)activated;
            activated = null;
            _notification = new SessionNotification(sessionsChanged);
            ComHelpers.ThrowIfFailed(SessionManager.RegisterSessionNotification(_notification),
                $"RegisterSessionNotification for {endpointId}");
        }
        catch
        {
            ComHelpers.Release(activated);
            ComHelpers.Release(SessionManager);
            ComHelpers.Release(Device);
            throw;
        }
    }

    public string Id { get; }
    public IMMDevice Device { get; }
    public IAudioSessionManager2 SessionManager { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { SessionManager.UnregisterSessionNotification(_notification); } catch { }
        ComHelpers.Release(SessionManager);
        ComHelpers.Release(Device);
    }

    private sealed class SessionNotification(Action changed) : IAudioSessionNotification
    {
        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            changed();
            return 0;
        }
    }
}
