using System.Runtime.InteropServices;

namespace AudioSourceMixer.WindowsAudio.Interop;

internal static class ComHelpers
{
    public static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0) throw new COMException($"{operation} failed (0x{hresult:X8}).", hresult);
    }

    public static string ReadAndFreeString(int hresult, IntPtr pointer, string operation)
    {
        ThrowIfFailed(hresult, operation);
        try { return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer) ?? string.Empty; }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }

    public static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            try { Marshal.ReleaseComObject(instance); }
            catch (InvalidComObjectException) { }
        }
    }
}
