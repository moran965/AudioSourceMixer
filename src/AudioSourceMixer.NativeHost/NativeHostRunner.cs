using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using AudioSourceMixer.Core.Browser;

namespace AudioSourceMixer.NativeHost;

public static partial class NativeHostRunner
{
    public static async Task<int> RunAsync(string[] args, Stream standardInput, Stream standardOutput,
        TextWriter standardError, CancellationToken cancellationToken = default, string? pipeName = null,
        int connectTimeoutMilliseconds = 800)
    {
        if (args.Length > 0 && !AllowedOrigin().IsMatch(args[0]))
        {
            await standardError.WriteLineAsync("Caller origin is not a valid Chromium extension origin.").ConfigureAwait(false);
            return 3;
        }

        await using var pipe = new NamedPipeClientStream(".", pipeName ?? BrowserBridgeServer.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        if (!await ConnectAsync(pipe, connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false))
        {
            await standardError.WriteLineAsync("Audio Source Mixer is not running; the native host will exit without starting it.")
                .ConfigureAwait(false);
            await NativeMessageTransport.WriteAsync(standardOutput, new BrowserMessage
            {
                Type = "bridge.status", Error = "请先打开 Audio Source Mixer。"
            }, cancellationToken).ConfigureAwait(false);
            return 2;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var outputGate = new SemaphoreSlim(1, 1);

        var browserToDesktop = ForwardBrowserMessagesAsync(standardInput, writer, linked.Token);
        var desktopToBrowser = ForwardDesktopMessagesAsync(reader, standardOutput, outputGate, linked.Token);
        await Task.WhenAny(browserToDesktop, desktopToBrowser).ConfigureAwait(false);
        linked.Cancel();
        try { await Task.WhenAll(browserToDesktop, desktopToBrowser).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        finally { outputGate.Dispose(); }
        return 0;
    }

    private static async Task<bool> ConnectAsync(NamedPipeClientStream pipe, int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    private static async Task ForwardBrowserMessagesAsync(Stream input, StreamWriter writer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await NativeMessageTransport.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            if (message is null) return;
            var json = Encoding.UTF8.GetString(BrowserProtocol.Serialize(message));
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ForwardDesktopMessagesAsync(StreamReader reader, Stream output, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return;
            var message = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(line));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await NativeMessageTransport.WriteAsync(output, message, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
    }

    [GeneratedRegex("^chrome-extension://[a-p]{32}/$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedOrigin();
}
