using System.Diagnostics;
using AudioSourceMixer.Core;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.WindowsAudio.Interop;

namespace AudioSourceMixer.WindowsAudio;

internal sealed class SessionHandle : IDisposable
{
    private static Guid EventContext = new("AA2CD8E1-113A-461D-884E-E26926A46E31");
    private readonly IAudioSessionControl2 _control;
    private readonly ISimpleAudioVolume? _simpleVolume;
    private readonly IChannelAudioVolume? _channelVolume;
    private readonly IAudioMeterInformation? _meter;
    private readonly SessionEvents _events;
    private readonly Action _changed;
    private readonly string _endpointName;
    private float[]? _balanceBaseVolumes;

    public SessionHandle(IAudioSessionControl control, string deviceId, string endpointName, Action changed)
    {
        _control = (IAudioSessionControl2)control;
        _simpleVolume = control as ISimpleAudioVolume;
        _channelVolume = control as IChannelAudioVolume;
        _meter = control as IAudioMeterInformation;
        _changed = changed;
        _endpointName = endpointName;

        var sessionIdentifier = ReadString(_control.GetSessionIdentifier, "GetSessionIdentifier");
        var instanceIdentifier = ReadString(_control.GetSessionInstanceIdentifier, "GetSessionInstanceIdentifier");
        ComHelpers.ThrowIfFailed(_control.GetProcessId(out var processId), "GetProcessId");
        var processInfo = ResolveProcess(processId);
        Identity = new AudioSessionIdentity(deviceId, sessionIdentifier, instanceIdentifier, processId, processInfo.Path, processInfo.StartTime);
        Id = AudioSourceId.ForWindowsSession(deviceId, instanceIdentifier);
        _events = new SessionEvents(_changed);
        ComHelpers.ThrowIfFailed(_control.RegisterAudioSessionNotification(_events), "RegisterAudioSessionNotification");
    }

    public AudioSourceId Id { get; }
    public AudioSessionIdentity Identity { get; }
    public float Balance { get; private set; }

    public AudioSourceSnapshot Snapshot()
    {
        ComHelpers.ThrowIfFailed(_control.GetState(out var state), "GetState");
        var displayName = ReadString(_control.GetDisplayName, "GetDisplayName");
        var processName = ResolveProcessName(Identity.ProcessId);
        if (string.IsNullOrWhiteSpace(displayName)) displayName = processName;

        var volume = 1f;
        var muted = false;
        if (_simpleVolume is not null)
        {
            ComHelpers.ThrowIfFailed(_simpleVolume.GetMasterVolume(out volume), "GetMasterVolume");
            ComHelpers.ThrowIfFailed(_simpleVolume.GetMute(out muted), "GetMute");
        }

        var channels = ReadChannelVolumes();
        var peak = ReadPeak();
        var isStereo = channels.Length == 2;
        var channelLimitation = channels.Length switch
        {
            0 => "会话未公开逐声道控制接口",
            1 => "此会话仅提供一个声道，当前模式无法分离左右声道",
            > 2 => "多声道会话未验证声道布局，仅提供主音量和静音",
            _ => null
        };
        var limitation = channelLimitation;

        return new AudioSourceSnapshot(
            Id,
            AudioSourceKind.WindowsSession,
            displayName,
            $"Windows 会话 · {_endpointName} · PID {Identity.ProcessId}",
            Identity.ProcessId,
            Identity.ExecutablePath,
            Identity.DeviceId,
            Identity.SessionIdentifier,
            Identity.SessionInstanceIdentifier,
            state switch
            {
                AudioSessionState.Active => AudioPlaybackState.Active,
                AudioSessionState.Expired => AudioPlaybackState.Expired,
                _ => AudioPlaybackState.Inactive
            },
            volume,
            muted,
            Balance,
            peak,
            channels,
            new AudioSourceCapabilities(_simpleVolume is not null, _simpleVolume is not null, isStereo, channels.Length,
                false, _meter is not null, true, limitation),
            DateTimeOffset.UtcNow,
            OutputDeviceId: Identity.DeviceId,
            OutputDeviceName: _endpointName,
            ProcessStartTimeUtc: Identity.ProcessStartTimeUtc);
    }

    public AudioRollbackEntry CaptureOriginal()
    {
        var snapshot = Snapshot();
        _balanceBaseVolumes ??= snapshot.ChannelVolumes.ToArray();
        return new AudioRollbackEntry(Id, Identity, snapshot.Volume, snapshot.Muted, snapshot.ChannelVolumes, DateTimeOffset.UtcNow);
    }

    public float ReadPeak()
    {
        if (_meter is null) return 0;
        ComHelpers.ThrowIfFailed(_meter.GetPeakValue(out var peak), "GetPeakValue");
        return float.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
    }

    public void SetVolume(float volume)
    {
        if (_simpleVolume is null) throw new NotSupportedException("This session does not expose ISimpleAudioVolume.");
        ComHelpers.ThrowIfFailed(_simpleVolume.SetMasterVolume(AudioMath.EnsureVolume(volume), ref EventContext), "SetMasterVolume");
    }

    public void SetMute(bool muted)
    {
        if (_simpleVolume is null) throw new NotSupportedException("This session does not expose ISimpleAudioVolume.");
        ComHelpers.ThrowIfFailed(_simpleVolume.SetMute(muted, ref EventContext), "SetMute");
    }

    public void SetBalance(float balance)
    {
        var current = ReadChannelVolumes();
        if (_channelVolume is null || current.Length != 2)
            throw new NotSupportedException("Stereo balance is available only when the session exposes exactly two controllable channels.");

        _balanceBaseVolumes ??= current;
        var gains = AudioMath.BalanceToGains(balance);
        var target = new[] { _balanceBaseVolumes[0] * gains.Left, _balanceBaseVolumes[1] * gains.Right };
        ComHelpers.ThrowIfFailed(_channelVolume.SetAllVolumes(2, target, ref EventContext), "SetAllVolumes");
        Balance = balance;
    }

    public void Restore(AudioRollbackEntry entry)
    {
        if (!entry.Identity.IsSafeRestoreMatch(Identity))
            throw new InvalidOperationException("Rollback identity no longer safely matches the live audio session.");

        RestoreValues(entry);
    }

    public void RestoreForMigratedApplication(AudioRollbackEntry entry)
    {
        if (entry.Identity.ProcessId == 0 || entry.Identity.ProcessId != Identity.ProcessId ||
            (!string.IsNullOrWhiteSpace(entry.Identity.ExecutablePath) &&
             !string.Equals(entry.Identity.ExecutablePath, Identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)) ||
            (entry.Identity.ProcessStartTimeUtc is not null && Identity.ProcessStartTimeUtc is not null &&
             entry.Identity.ProcessStartTimeUtc != Identity.ProcessStartTimeUtc))
            throw new InvalidOperationException("Rollback application identity no longer safely matches the live process.");

        RestoreValues(entry);
    }

    private void RestoreValues(AudioRollbackEntry entry)
    {

        if (_simpleVolume is not null)
        {
            ComHelpers.ThrowIfFailed(_simpleVolume.SetMasterVolume(entry.MasterVolume, ref EventContext), "Restore master volume");
            ComHelpers.ThrowIfFailed(_simpleVolume.SetMute(entry.Muted, ref EventContext), "Restore mute");
        }

        if (_channelVolume is not null && entry.ChannelVolumes.Count > 0)
        {
            var values = entry.ChannelVolumes.ToArray();
            ComHelpers.ThrowIfFailed(_channelVolume.SetAllVolumes((uint)values.Length, values, ref EventContext), "Restore channel volumes");
        }
        Balance = 0;
        _balanceBaseVolumes = null;
    }

    public AudioCapabilityResult ProbeSetters()
    {
        var snapshot = Snapshot();
        var master = false;
        var mute = false;
        var channels = false;
        if (_simpleVolume is not null)
        {
            ComHelpers.ThrowIfFailed(_simpleVolume.SetMasterVolume(snapshot.Volume, ref EventContext), "Probe SetMasterVolume");
            master = true;
            ComHelpers.ThrowIfFailed(_simpleVolume.SetMute(snapshot.Muted, ref EventContext), "Probe SetMute");
            mute = true;
        }
        if (_channelVolume is not null && snapshot.ChannelVolumes.Count > 0)
        {
            var values = snapshot.ChannelVolumes.ToArray();
            ComHelpers.ThrowIfFailed(_channelVolume.SetAllVolumes((uint)values.Length, values, ref EventContext), "Probe SetAllVolumes");
            channels = true;
        }
        return new AudioCapabilityResult(snapshot, master, mute, channels, _meter is not null, null);
    }

    private float[] ReadChannelVolumes()
    {
        if (_channelVolume is null) return [];
        ComHelpers.ThrowIfFailed(_channelVolume.GetChannelCount(out var count), "GetChannelCount");
        if (count is 0 or > 32) return [];
        var volumes = new float[count];
        ComHelpers.ThrowIfFailed(_channelVolume.GetAllVolumes(count, volumes), "GetAllVolumes");
        return volumes;
    }

    private delegate int StringGetter(out IntPtr value);
    private static string ReadString(StringGetter getter, string operation)
    {
        var result = getter(out var value);
        return ComHelpers.ReadAndFreeString(result, value, operation);
    }

    private static (string? Path, DateTimeOffset? StartTime) ResolveProcess(uint processId)
    {
        if (processId == 0) return (null, null);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return (process.MainModule?.FileName, process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, null);
        }
    }

    private static string ResolveProcessName(uint processId)
    {
        if (processId == 0) return "系统声音";
        try { using var process = Process.GetProcessById((int)processId); return process.ProcessName; }
        catch { return $"进程 {processId}"; }
    }

    public void Dispose()
    {
        try { _control.UnregisterAudioSessionNotification(_events); } catch { }
        ComHelpers.Release(_control);
    }

    private sealed class SessionEvents(Action changed) : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(string newDisplayName, IntPtr eventContext) { changed(); return 0; }
        public int OnIconPathChanged(string newIconPath, IntPtr eventContext) { changed(); return 0; }
        public int OnSimpleVolumeChanged(float newVolume, bool newMute, IntPtr eventContext) { changed(); return 0; }
        public int OnChannelVolumeChanged(uint channelCount, IntPtr values, uint changedChannel, IntPtr eventContext) { changed(); return 0; }
        public int OnGroupingParamChanged(ref Guid newGroupingId, IntPtr eventContext) { changed(); return 0; }
        public int OnStateChanged(AudioSessionState newState) { changed(); return 0; }
        public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) { changed(); return 0; }
    }
}

public sealed record AudioCapabilityResult(
    AudioSourceSnapshot Snapshot,
    bool MasterVolumeRoundTrip,
    bool MuteRoundTrip,
    bool ChannelVolumeRoundTrip,
    bool PeakMeterAvailable,
    string? Error);
