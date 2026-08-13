using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioSourceMixer.WindowsAudio.Interop;

internal enum AudioPolicyConfigAbi
{
    Unsupported,
    Downlevel,
    Windows21H2
}

internal static class AudioPolicyConfigAbiSelector
{
    public const int MinimumSupportedBuild = 17134;
    public const int Windows21H2Build = 21390;

    public static AudioPolicyConfigAbi Select(int windowsBuild) => windowsBuild switch
    {
        >= Windows21H2Build => AudioPolicyConfigAbi.Windows21H2,
        >= MinimumSupportedBuild => AudioPolicyConfigAbi.Downlevel,
        _ => AudioPolicyConfigAbi.Unsupported
    };
}

internal static class AudioPolicyConfigNative
{
    public const string RuntimeClass = "Windows.Media.Internal.AudioPolicyConfig";
    public const string MmDevicePrefix = @"\\?\SWD#MMDEVAPI#";
    public const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    [DllImport("combase.dll", PreserveSig = true)]
    internal static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out SafeHString hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    internal static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    internal static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll", PreserveSig = true)]
    internal static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);
}

internal sealed class SafeHString : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeHString() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle()
        => AudioPolicyConfigNative.WindowsDeleteString(handle) >= 0;
}

[ComImport, Guid("AB3D4648-E242-459F-B02F-541C70306324"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
internal interface IAudioPolicyConfigFactoryWindows21H2
{
    int IncompleteAddCtxVolumeChange();
    int IncompleteRemoveCtxVolumeChanged();
    int IncompleteAddRingerVibrateStateChanged();
    int IncompleteRemoveRingerVibrateStateChange();
    int IncompleteSetVolumeGroupGainForId();
    int IncompleteGetVolumeGroupGainForId();
    int IncompleteGetActiveVolumeGroupForEndpointId();
    int IncompleteGetVolumeGroupsForEndpoint();
    int IncompleteGetCurrentVolumeContext();
    int IncompleteSetVolumeGroupMuteForId();
    int IncompleteGetVolumeGroupMuteForId();
    int IncompleteSetRingerVibrateState();
    int IncompleteGetRingerVibrateState();
    int IncompleteSetPreferredChatApplication();
    int IncompleteResetPreferredChatApplication();
    int IncompleteGetPreferredChatApplication();
    int IncompleteGetCurrentChatApplications();
    int IncompleteAddChatContextChanged();
    int IncompleteRemoveChatContextChanged();
    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
    [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
    [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
}

[ComImport, Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
internal interface IAudioPolicyConfigFactoryDownlevel
{
    int IncompleteAddCtxVolumeChange();
    int IncompleteRemoveCtxVolumeChanged();
    int IncompleteAddRingerVibrateStateChanged();
    int IncompleteRemoveRingerVibrateStateChange();
    int IncompleteSetVolumeGroupGainForId();
    int IncompleteGetVolumeGroupGainForId();
    int IncompleteGetActiveVolumeGroupForEndpointId();
    int IncompleteGetVolumeGroupsForEndpoint();
    int IncompleteGetCurrentVolumeContext();
    int IncompleteSetVolumeGroupMuteForId();
    int IncompleteGetVolumeGroupMuteForId();
    int IncompleteSetRingerVibrateState();
    int IncompleteGetRingerVibrateState();
    int IncompleteSetPreferredChatApplication();
    int IncompleteResetPreferredChatApplication();
    int IncompleteGetPreferredChatApplication();
    int IncompleteGetCurrentChatApplications();
    int IncompleteAddChatContextChanged();
    int IncompleteRemoveChatContextChanged();
    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
    [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
    [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
}
