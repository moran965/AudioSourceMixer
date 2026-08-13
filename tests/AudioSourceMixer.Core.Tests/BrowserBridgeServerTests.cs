using System.IO.Pipes;
using System.Text;
using AudioSourceMixer.Core.Browser;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Tests;

public sealed class BrowserBridgeServerTests
{
    [Fact]
    public async Task DesktopCanAddressConnectedBrowserManagerAndReportsItsVersion()
    {
        var pipeName = TestPipeName();
        await using var server = new BrowserBridgeServer(pipeName);
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("""{"protocolVersion":2,"type":"bridge.hello","browser":"edge","extensionVersion":"0.2.0"}""");
        _ = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => server.GetConnectionStatuses().Count == 1);
        var status = Assert.Single(server.GetConnectionStatuses());
        Assert.Equal("edge", status.Browser);
        Assert.Equal("0.2.0", status.ExtensionVersion);

        var open = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await server.OpenOutputManagerAsync("edge");
        Assert.Equal("bridge.openOptions", BrowserProtocol.Parse(Encoding.UTF8.GetBytes((await open)!)).Type);
        var clear = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await server.ClearOutputMappingsAsync("edge");
        Assert.Equal("bridge.clearMappings", BrowserProtocol.Parse(Encoding.UTF8.GetBytes((await clear)!)).Type);
        await Assert.ThrowsAsync<IOException>(() => server.OpenOutputManagerAsync("chrome"));
    }

    [Fact]
    public async Task Protocol2RoutesExtendedTabGainAndOutputOnlyToOwningConnection()
    {
        var pipeName = TestPipeName();
        await using var server = new BrowserBridgeServer(pipeName);
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var statusRead = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await writer.WriteLineAsync("""{"protocolVersion":2,"type":"bridge.hello"}""");
        var status = BrowserProtocol.Parse(Encoding.UTF8.GetBytes((await statusRead)!));
        Assert.Equal("bridge.status", status.Type);
        Assert.Null(status.Error);
        await writer.WriteLineAsync("""{"protocolVersion":2,"type":"tab.register","browser":"edge","tabId":8,"title":"Test","generation":41}""");
        await WaitUntilAsync(() => server.GetTabs().Count == 1);

        var source = AudioSourceMixer.Core.Models.AudioSourceId.ForBrowserTab("edge", 8);
        var commandRead = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var setAudio = server.SetAudioAsync(source, 1.5f, -0.25f, false, "windows-endpoint", "USB DAC",
            [new AudioSourceMixer.Core.Models.OutputDeviceInfo("windows-endpoint", "USB DAC", IsDefaultMultimedia: true)]);
        var commandLine = await commandRead;
        var command = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(commandLine!));
        Assert.Equal(2, command.ProtocolVersion);
        Assert.Equal(1.5f, command.Volume);
        Assert.Equal("windows-endpoint", command.OutputDeviceId);
        Assert.Equal("USB DAC", command.OutputDeviceName);
        Assert.Null(command.Equalizer);
        Assert.False(string.IsNullOrWhiteSpace(command.CorrelationId));
        var endpoint = Assert.Single(command.OutputDevices!);
        Assert.Equal("windows-endpoint", endpoint.EndpointId);
        Assert.Equal("USB DAC", endpoint.FriendlyName);

        Assert.Equal(42, command.Generation);
        Assert.InRange(command.Generation!.Value, 1, BrowserProtocol.MaximumJavaScriptSafeInteger);
        await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.update","browser":"edge","tabId":8,"outputDeviceId":"windows-endpoint","outputDeviceName":"USB DAC","browserDeviceId":"browser-usb","effectiveSinkId":"browser-usb","effectiveSinkLabel":"USB DAC","routingState":"Applied","correlationId":"{{command.CorrelationId}}","generation":{{command.Generation}},"setSinkDurationMs":3.25,"setSinkIdSupported":true,"outputStatus":"Applied"}""");
        await setAudio;
        await WaitUntilAsync(() => server.GetTabs().Single().RoutingState == AudioSourceMixer.Core.Models.AudioRoutingState.Applied);
        var tab = Assert.Single(server.GetTabs());
        Assert.Equal("windows-endpoint", tab.OutputDeviceId);
        Assert.Equal("browser-usb", tab.EffectiveBrowserSinkId);
        Assert.Equal("browser-usb", tab.EffectiveOutputDeviceId);
        Assert.Equal(command.CorrelationId, tab.CorrelationId);
    }

    [Fact]
    public async Task Protocol3SendsEqualizerWithoutChangingAudioOrRouteFields()
    {
        var pipeName = TestPipeName();
        await using var server = new BrowserBridgeServer(pipeName);
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("""{"protocolVersion":3,"type":"tab.register","browser":"chrome","tabId":21,"title":"EQ","generation":5} """);
        await WaitUntilAsync(() => server.GetTabs().Count == 1);

        var source = AudioSourceId.ForBrowserTab("chrome", 21);
        var pending = server.SetAudioAsync(source, 1.25f, 0.4f, true, "endpoint", "Headphones",
            effects: EqualizerCatalog.CreatePreset("warm"));
        var command = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(
            (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!));
        Assert.Equal(1.25f, command.Volume);
        Assert.Equal(0.4f, command.Balance);
        Assert.True(command.Muted);
        Assert.Equal("endpoint", command.OutputDeviceId);
        Assert.Equal("warm", command.Equalizer!.PresetId);
        Assert.Equal([3f, 3f, 2f, 1f, 1f, 0f, -1f, -1f, -2f, -2f],
            command.Equalizer.Bands.Select(band => band.GainDb).ToArray());
        await writer.WriteLineAsync($$"""{"protocolVersion":3,"type":"tab.update","browser":"chrome","tabId":21,"routingState":"Applied","correlationId":"{{command.CorrelationId}}","generation":{{command.Generation}},"equalizer":{{System.Text.Json.JsonSerializer.Serialize(command.Equalizer, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))}}} """);
        await pending;
        Assert.Equal("warm", Assert.Single(server.GetTabs()).Effects!.PresetId);
    }

    [Fact]
    public async Task SetAudioIgnoresStaleAcknowledgementsAcceptsPendingAndTimesOutWithoutAck()
    {
        var pipeName = TestPipeName();
        await using var server = new BrowserBridgeServer(pipeName, commandTimeout: TimeSpan.FromMilliseconds(250));
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("""{"protocolVersion":2,"type":"tab.register","browser":"chrome","tabId":17,"title":"Ack"}""");
        await WaitUntilAsync(() => server.GetTabs().Count == 1);
        var source = AudioSourceMixer.Core.Models.AudioSourceId.ForBrowserTab("chrome", 17);

        var first = server.SetAudioAsync(source, 1, 0, false, "endpoint-a", "Device A");
        var command = BrowserProtocol.Parse(Encoding.UTF8.GetBytes((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!));
        await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.update","browser":"chrome","tabId":17,"routingState":"Applied","correlationId":"stale-correlation","generation":{{command.Generation}}}""");
        await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.update","browser":"chrome","tabId":17,"routingState":"Applied","correlationId":"{{command.CorrelationId}}","generation":{{command.Generation! - 1}}}""");
        await Task.Delay(50);
        Assert.False(first.IsCompleted);
        await writer.WriteLineAsync($$"""{"protocolVersion":2,"type":"tab.update","browser":"chrome","tabId":17,"routingState":"PendingAuthorization","correlationId":"{{command.CorrelationId}}","generation":{{command.Generation}},"error":"authorization required"}""");
        await first;

        var timedOut = server.SetAudioAsync(source, 1, 0, false, "endpoint-b", "Device B");
        _ = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
    }

    [Fact]
    public async Task LegacyConnectionRejectsExtendedTabGainInsteadOfSilentlyClamping()
    {
        var pipeName = TestPipeName();
        await using var server = new BrowserBridgeServer(pipeName);
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("""{"protocolVersion":1,"type":"tab.register","browser":"chrome","tabId":9,"title":"Legacy"}""");
        await WaitUntilAsync(() => server.GetTabs().Count == 1);
        var source = AudioSourceMixer.Core.Models.AudioSourceId.ForBrowserTab("chrome", 9);
        await Assert.ThrowsAsync<NotSupportedException>(() => server.SetAudioAsync(source, 1.01f, 0, false));
    }

    [Fact]
    public async Task DisconnectRemovesTabsOwnedByConnection()
    {
        var pipeName = TestPipeName();
        await using var server = new BrowserBridgeServer(pipeName);
        server.Start();

        await using (var client = new NamedPipeClientStream(".", pipeName,
                         PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"protocolVersion":1,"type":"tab.register","browser":"chrome","tabId":42,"title":"Test"}""");
            await WaitUntilAsync(() => server.GetTabs().Count == 1);
        }

        await WaitUntilAsync(() => server.GetTabs().Count == 0 && !server.IsConnected);
    }

    [Fact]
    public async Task DisposeRequestsEveryEnhancedTabToRestoreNormalPlayback()
    {
        var pipeName = TestPipeName();
        await using var server = new BrowserBridgeServer(pipeName);
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("""{"protocolVersion":2,"type":"tab.register","browser":"chrome","tabId":88,"title":"Exit restore"}""");
        await WaitUntilAsync(() => server.GetTabs().Count == 1);

        var commandRead = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = server.DisposeAsync().AsTask();
        var command = BrowserProtocol.Parse(Encoding.UTF8.GetBytes((await commandRead)!));

        Assert.Equal("tab.stop", command.Type);
        Assert.Equal(88, command.TabId);
        await dispose;
    }

    private static string TestPipeName()
        => $"{BrowserBridgeServer.PipeName}.Tests.{Guid.NewGuid():N}";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
