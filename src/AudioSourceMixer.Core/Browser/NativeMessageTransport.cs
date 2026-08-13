using System.Buffers.Binary;

namespace AudioSourceMixer.Core.Browser;

public static class NativeMessageTransport
{
    public static async Task<BrowserMessage?> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var headerRead = await ReadExactlyOrEofAsync(input, header, cancellationToken).ConfigureAwait(false);
        if (!headerRead) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > BrowserProtocol.MaximumMessageBytes)
            throw new InvalidDataException($"Native message length {length} is invalid.");
        var payload = new byte[length];
        await input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return BrowserProtocol.Parse(payload);
    }

    public static async Task WriteAsync(Stream output, BrowserMessage message, CancellationToken cancellationToken = default)
    {
        var payload = BrowserProtocol.Serialize(message);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream input, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await input.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new EndOfStreamException("Native message header ended unexpectedly.");
            }
            offset += read;
        }
        return true;
    }
}
