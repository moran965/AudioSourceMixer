using System.Runtime.InteropServices;

internal sealed class WaveOutTestPlayer : IDisposable
{
    private const uint WaveMapper = unchecked((uint)-1);
    private const uint WhdrBeginLoop = 0x00000004;
    private const uint WhdrEndLoop = 0x00000008;
    private IntPtr _waveOut;
    private IntPtr _audioData;
    private IntPtr _headerMemory;
    private bool _prepared;

    public WaveOutTestPlayer(string wavePath)
    {
        var wave = ReadWave(wavePath);
        var format = new WaveFormat
        {
            FormatTag = wave.FormatTag,
            Channels = wave.Channels,
            SamplesPerSecond = wave.SamplesPerSecond,
            AverageBytesPerSecond = wave.AverageBytesPerSecond,
            BlockAlign = wave.BlockAlign,
            BitsPerSample = wave.BitsPerSample,
            ExtraSize = 0
        };

        ThrowIfFailed(waveOutOpen(out _waveOut, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0), "waveOutOpen");
        try
        {
            _audioData = Marshal.AllocHGlobal(wave.Data.Length);
            Marshal.Copy(wave.Data, 0, _audioData, wave.Data.Length);
            var header = new WaveHeader
            {
                Data = _audioData,
                BufferLength = checked((uint)wave.Data.Length),
                Flags = WhdrBeginLoop | WhdrEndLoop,
                Loops = uint.MaxValue
            };
            _headerMemory = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(header, _headerMemory, false);
            ThrowIfFailed(waveOutPrepareHeader(_waveOut, _headerMemory, checked((uint)Marshal.SizeOf<WaveHeader>())), "waveOutPrepareHeader");
            _prepared = true;
            ThrowIfFailed(waveOutWrite(_waveOut, _headerMemory, checked((uint)Marshal.SizeOf<WaveHeader>())), "waveOutWrite");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_waveOut != IntPtr.Zero)
        {
            waveOutReset(_waveOut);
            if (_prepared) waveOutUnprepareHeader(_waveOut, _headerMemory, checked((uint)Marshal.SizeOf<WaveHeader>()));
            waveOutClose(_waveOut);
        }
        if (_headerMemory != IntPtr.Zero) Marshal.FreeHGlobal(_headerMemory);
        if (_audioData != IntPtr.Zero) Marshal.FreeHGlobal(_audioData);
        _waveOut = IntPtr.Zero;
        _headerMemory = IntPtr.Zero;
        _audioData = IntPtr.Zero;
        _prepared = false;
    }

    private static WaveData ReadWave(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Expected a RIFF wave file.");
        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Expected WAVE data.");

        ushort formatTag = 0, channels = 0, blockAlign = 0, bitsPerSample = 0;
        uint samplesPerSecond = 0, averageBytesPerSecond = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            if (chunk == "fmt ")
            {
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                samplesPerSecond = reader.ReadUInt32();
                averageBytesPerSecond = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                stream.Position += size - 16;
            }
            else if (chunk == "data") data = reader.ReadBytes(checked((int)size));
            else stream.Position += size;
            if ((size & 1) != 0) stream.Position++;
        }

        if (formatTag != 1 || data is null || data.Length == 0)
            throw new NotSupportedException("The deterministic test player requires non-empty PCM wave data.");
        return new WaveData(formatTag, channels, samplesPerSecond, averageBytesPerSecond, blockAlign, bitsPerSample, data);
    }

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"{operation} failed with MMRESULT {result}.");
    }

    private sealed record WaveData(ushort FormatTag, ushort Channels, uint SamplesPerSecond,
        uint AverageBytesPerSecond, ushort BlockAlign, ushort BitsPerSample, byte[] Data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out IntPtr waveOut, uint deviceId, ref WaveFormat format,
        IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr waveOut);
}
