using System.Runtime.InteropServices;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.WindowsAudio.Interop;

namespace AudioSourceMixer.WindowsAudio;

public enum AudioRouteRole
{
    Console,
    Multimedia,
    Communications
}

public sealed record PersistedAudioRoute(AudioRouteRole Role, string? EndpointId, int HResult);

public sealed record AudioRouteTransaction(
    uint ProcessId,
    string RequestedEndpointId,
    string AbiVariant,
    int WindowsBuild,
    IReadOnlyList<PersistedAudioRoute> OriginalRoutes,
    IReadOnlyList<PersistedAudioRoute> AppliedRoutes,
    bool Succeeded,
    string? Error);

public sealed class WindowsAppRoutingBackend : IDisposable
{
    private const int SetPersistedEndpointSlot = 25;
    private const int GetPersistedEndpointSlot = 26;
    private readonly IntPtr _factory;
    private readonly AudioPolicyConfigAbi _abi;
    private readonly RollingFileLogger? _logger;
    private bool _disposed;
    private static readonly AudioRouteRole[] WriteOrder =
    [
        AudioRouteRole.Multimedia,
        AudioRouteRole.Console,
        AudioRouteRole.Communications
    ];

    public WindowsAppRoutingBackend(RollingFileLogger? logger = null)
    {
        _logger = logger;
        WindowsBuild = Environment.OSVersion.Version.Build;
        _abi = AudioPolicyConfigAbiSelector.Select(WindowsBuild);
        if (_abi == AudioPolicyConfigAbi.Unsupported)
            throw new PlatformNotSupportedException($"Per-app routing requires Windows build {AudioPolicyConfigAbiSelector.MinimumSupportedBuild} or later; current build is {WindowsBuild}.");

        using var runtimeClass = CreateHString(AudioPolicyConfigNative.RuntimeClass);
        var iid = _abi == AudioPolicyConfigAbi.Windows21H2
            ? typeof(IAudioPolicyConfigFactoryWindows21H2).GUID
            : typeof(IAudioPolicyConfigFactoryDownlevel).GUID;
        var factoryPointer = IntPtr.Zero;
        try
        {
            var result = AudioPolicyConfigNative.RoGetActivationFactory(runtimeClass.DangerousGetHandle(), ref iid, out factoryPointer);
            ComHelpers.ThrowIfFailed(result, $"RoGetActivationFactory({AudioPolicyConfigNative.RuntimeClass}, {iid})");
            _factory = factoryPointer;
            factoryPointer = IntPtr.Zero;
        }
        finally
        {
            if (factoryPointer != IntPtr.Zero) Marshal.Release(factoryPointer);
        }
    }

    public int WindowsBuild { get; }
    public string AbiVariant => _abi.ToString();

    public IReadOnlyList<PersistedAudioRoute> GetPersistedRoutes(uint processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateProcessId(processId);
        return Enum.GetValues<AudioRouteRole>().Select(role => GetPersistedRoute(processId, role)).ToArray();
    }

    public AudioRouteTransaction SetPersistedRoutes(uint processId, string? endpointId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateProcessId(processId);
        var requested = endpointId ?? string.Empty;
        var transaction = ExecuteTransaction(processId, requested, AbiVariant, WindowsBuild,
            role => GetPersistedRoute(processId, role),
            (role, target) => SetPersistedRoute(processId, role, target));
        Log(transaction);
        return transaction;
    }

    internal static AudioRouteTransaction ExecuteTransaction(uint processId, string requested, string abiVariant,
        int windowsBuild, Func<AudioRouteRole, PersistedAudioRoute> readOriginal,
        Func<AudioRouteRole, string, int> writeRoute)
    {
        var originals = Enum.GetValues<AudioRouteRole>().Select(readOriginal).ToArray();
        var applied = new List<PersistedAudioRoute>();
        try
        {
            // Windows stores Console and Multimedia as a shared render preference.
            // Clearing that preference can return E_ACCESSDENIED if Console is
            // written first; EarTrumpet uses Multimedia -> Console as well.
            foreach (var role in WriteOrder)
            {
                var hresult = writeRoute(role, requested);
                applied.Add(new PersistedAudioRoute(role, requested, hresult));
                if (hresult < 0)
                    throw new COMException($"SetPersistedDefaultAudioEndpoint PID={processId}, role={role} failed.", hresult);
            }
            return new AudioRouteTransaction(processId, requested, abiVariant, windowsBuild,
                originals, applied, true, null);
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<string>();
            foreach (var original in originals.OrderBy(route => Array.IndexOf(WriteOrder, route.Role)))
            {
                try
                {
                    var hresult = writeRoute(original.Role, original.EndpointId ?? string.Empty);
                    if (hresult < 0) rollbackErrors.Add($"role={original.Role}, HRESULT=0x{hresult:X8}");
                }
                catch (Exception rollbackException) { rollbackErrors.Add($"role={original.Role}: {rollbackException}"); }
            }
            var error = rollbackErrors.Count == 0
                ? exception.ToString()
                : $"{exception}\nRollback errors:\n{string.Join(Environment.NewLine, rollbackErrors)}";
            return new AudioRouteTransaction(processId, requested, abiVariant, windowsBuild,
                originals, applied, false, error);
        }
    }

    public void RestoreRoutes(uint processId, IReadOnlyList<PersistedAudioRoute> originalRoutes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateProcessId(processId);
        var errors = RestoreRoutesCore(processId, originalRoutes);
        if (errors.Count != 0) throw new AggregateException(errors.Select(error => new COMException(error)));
    }

    private PersistedAudioRoute GetPersistedRoute(uint processId, AudioRouteRole role)
    {
        // The internal Get ABI treats Console/Multimedia as one persisted render preference.
        // EarTrumpet queries eMultimedia | eConsole, whose numeric value is eMultimedia (1).
        var readRole = role == AudioRouteRole.Console ? ERole.Multimedia : ToNativeRole(role);
        var hresult = GetPersistedRouteCore(processId, readRole, out var hstring);
        try
        {
            ComHelpers.ThrowIfFailed(hresult, $"GetPersistedDefaultAudioEndpoint PID={processId}, role={role}");
            return new PersistedAudioRoute(role, UnpackEndpointId(ReadHString(hstring)), hresult);
        }
        finally
        {
            if (hstring != IntPtr.Zero) AudioPolicyConfigNative.WindowsDeleteString(hstring);
        }
    }

    private int SetPersistedRoute(uint processId, AudioRouteRole role, string endpointId)
    {
        SafeHString? device = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(endpointId)) device = CreateHString(PackEndpointId(endpointId));
            return SetPersistedRouteCore(processId, ToNativeRole(role), device?.DangerousGetHandle() ?? IntPtr.Zero);
        }
        finally { device?.Dispose(); }
    }

    private List<string> RestoreRoutesCore(uint processId, IReadOnlyList<PersistedAudioRoute> routes)
    {
        var errors = new List<string>();
        foreach (var route in routes.OrderBy(item => Array.IndexOf(WriteOrder, item.Role)))
        {
            try
            {
                var hresult = SetPersistedRoute(processId, route.Role, route.EndpointId ?? string.Empty);
                if (hresult < 0) errors.Add($"role={route.Role}, HRESULT=0x{hresult:X8}");
            }
            catch (Exception exception) { errors.Add($"role={route.Role}: {exception}"); }
        }
        return errors;
    }

    private int GetPersistedRouteCore(uint processId, ERole role, out IntPtr deviceId)
    {
        var method = GetVtableDelegate<GetPersistedEndpointDelegate>(GetPersistedEndpointSlot);
        return method(_factory, processId, EDataFlow.Render, role, out deviceId);
    }

    private int SetPersistedRouteCore(uint processId, ERole role, IntPtr deviceId)
    {
        var method = GetVtableDelegate<SetPersistedEndpointDelegate>(SetPersistedEndpointSlot);
        return method(_factory, processId, EDataFlow.Render, role, deviceId);
    }

    private T GetVtableDelegate<T>(int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(_factory);
        var method = Marshal.ReadIntPtr(vtable, checked(slot * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedEndpointDelegate(IntPtr @this, uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedEndpointDelegate(IntPtr @this, uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

    private static ERole ToNativeRole(AudioRouteRole role) => role switch
    {
        AudioRouteRole.Console => ERole.Console,
        AudioRouteRole.Multimedia => ERole.Multimedia,
        AudioRouteRole.Communications => ERole.Communications,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    internal static string PackEndpointId(string endpointId)
        => $"{AudioPolicyConfigNative.MmDevicePrefix}{endpointId}{AudioPolicyConfigNative.RenderInterfaceSuffix}";

    internal static string? UnpackEndpointId(string? persistedId)
    {
        if (string.IsNullOrWhiteSpace(persistedId)) return null;
        var value = persistedId;
        if (value.StartsWith(AudioPolicyConfigNative.MmDevicePrefix, StringComparison.OrdinalIgnoreCase))
            value = value[AudioPolicyConfigNative.MmDevicePrefix.Length..];
        if (value.EndsWith(AudioPolicyConfigNative.RenderInterfaceSuffix, StringComparison.OrdinalIgnoreCase))
            value = value[..^AudioPolicyConfigNative.RenderInterfaceSuffix.Length];
        return value;
    }

    private static SafeHString CreateHString(string value)
    {
        var hresult = AudioPolicyConfigNative.WindowsCreateString(value, checked((uint)value.Length), out var hstring);
        ComHelpers.ThrowIfFailed(hresult, "WindowsCreateString");
        return hstring;
    }

    private static string? ReadHString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return null;
        var buffer = AudioPolicyConfigNative.WindowsGetStringRawBuffer(hstring, out var length);
        return buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer, checked((int)length));
    }

    private static void ValidateProcessId(uint processId)
    {
        if (processId == 0) throw new NotSupportedException("System Sounds/PID 0 cannot receive a per-app routing policy.");
    }

    private void Log(AudioRouteTransaction transaction)
    {
        _logger?.Info($"Audio route PID={transaction.ProcessId}; ABI={transaction.AbiVariant}; Build={transaction.WindowsBuild}; " +
            $"Requested={transaction.RequestedEndpointId}; Success={transaction.Succeeded}; " +
            $"Original=[{string.Join(",", transaction.OriginalRoutes.Select(route => $"{route.Role}:{route.EndpointId ?? "default"}:0x{route.HResult:X8}"))}]; " +
            $"Applied=[{string.Join(",", transaction.AppliedRoutes.Select(route => $"{route.Role}:0x{route.HResult:X8}"))}]; Error={transaction.Error}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_factory != IntPtr.Zero) Marshal.Release(_factory);
    }
}
