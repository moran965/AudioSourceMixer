using System.Buffers.Binary;
using System.Text;
using AudioSourceMixer.Core.Browser;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Tests;

public sealed class BrowserProtocolTests
{
    [Fact]
    public void RejectsGenerationOutsideJavaScriptSafeIntegerRange()
    {
        var json = """{"protocolVersion":2,"type":"tab.update","browser":"chrome","tabId":1,"generation":9007199254740992}""";
        Assert.Throws<InvalidDataException>(() => BrowserProtocol.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ValidRegistrationParses()
    {
        var json = """{"protocolVersion":1,"type":"tab.register","browser":"chrome","tabId":7,"origin":"https://example.com"}""";
        var message = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("chrome:7", BrowserProtocol.GetSourceId(message).Value);
    }

    [Theory]
    [InlineData("""{"protocolVersion":4,"type":"bridge.hello"}""")]
    [InlineData("""{"protocolVersion":1,"type":"unknown"}""")]
    [InlineData("""{"protocolVersion":1,"type":"tab.setAudio","volume":2}""")]
    [InlineData("""{"protocolVersion":1,"type":"tab.setAudio","volume":0.5}""")]
    [InlineData("""{"protocolVersion":1,"type":"tab.stop"}""")]
    [InlineData("""{"protocolVersion":1,"type":"tab.register","browser":"firefox","tabId":1}""")]
    public void InvalidMessagesAreRejected(string json)
        => Assert.Throws<InvalidDataException>(() => BrowserProtocol.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Version2AcceptsExtendedTabGainAndOutputPreference()
    {
        var json = """{"protocolVersion":2,"type":"tab.setAudio","tabId":7,"volume":2,"outputDeviceId":"endpoint","outputDeviceName":"USB DAC"}""";
        var message = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, message.Volume);
        Assert.Equal("endpoint", message.OutputDeviceId);
    }

    [Fact]
    public void Version3CarriesValidatedEqualizerWhileVersion2RemainsCompatibleWithoutIt()
    {
        var equalizer = EqualizerCatalog.CreatePreset("vocal");
        var payload = BrowserProtocol.Serialize(new BrowserMessage
        {
            ProtocolVersion = 3, Type = "tab.setAudio", Browser = "chrome", TabId = 7,
            Volume = 1.5f, Equalizer = equalizer
        });
        var parsed = BrowserProtocol.Parse(payload);
        Assert.Equal("vocal", parsed.Equalizer!.PresetId);
        Assert.Equal(10, parsed.Equalizer.Bands.Count);

        var legacy = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(
            """{"protocolVersion":2,"type":"tab.setAudio","browser":"chrome","tabId":7,"volume":1.5}"""));
        Assert.Null(legacy.Equalizer);
        Assert.Throws<InvalidDataException>(() => BrowserProtocol.Parse(Encoding.UTF8.GetBytes(
            """{"protocolVersion":2,"type":"tab.setAudio","browser":"chrome","tabId":7,"equalizer":{"enabled":false,"presetId":"off","preampDb":0,"bands":[]}}""")));
    }

    [Fact]
    public void Version2CarriesVerifiedBrowserSinkDiagnostics()
    {
        var json = """{"protocolVersion":2,"type":"tab.update","browser":"edge","tabId":7,"outputDeviceId":"windows-endpoint","browserDeviceId":"browser-device","effectiveSinkId":"browser-device","routingState":"Applied","correlationId":"corr-7","setSinkDurationMs":12.5,"setSinkIdSupported":true}""";
        var message = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("windows-endpoint", message.OutputDeviceId);
        Assert.Equal("browser-device", message.BrowserDeviceId);
        Assert.Equal(message.BrowserDeviceId, message.EffectiveSinkId);
        Assert.Equal("Applied", message.RoutingState);
        Assert.Equal("corr-7", message.CorrelationId);
        Assert.Equal(12.5, message.SetSinkDurationMs);
        Assert.True(message.SetSinkIdSupported);
    }

    [Fact]
    public void InvalidSinkStateAndOversizedEndpointCatalogAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => BrowserProtocol.Parse(Encoding.UTF8.GetBytes(
            """{"protocolVersion":2,"type":"tab.update","browser":"edge","tabId":7,"routingState":"PretendApplied"}""")));
        var message = new BrowserMessage
        {
            Type = "tab.setAudio", TabId = 7,
            OutputDevices = Enumerable.Range(0, 65).Select(index => new BrowserOutputEndpoint
                { EndpointId = $"endpoint-{index}", FriendlyName = $"Device {index}" }).ToArray()
        };
        Assert.Throws<InvalidDataException>(() => BrowserProtocol.Serialize(message));
    }

    [Fact]
    public async Task NativeTransportRoundTripsUtf8()
    {
        await using var stream = new MemoryStream();
        var expected = new BrowserMessage { Type = "tab.register", Browser = "edge", TabId = 9, Title = "中文标题" };
        await NativeMessageTransport.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await NativeMessageTransport.ReadAsync(stream);
        Assert.Equal(expected.Title, actual!.Title);
        Assert.Equal(expected.TabId, actual.TabId);
    }

    [Fact]
    public async Task NativeTransportRejectsOversizedLength()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, BrowserProtocol.MaximumMessageBytes + 1);
        await using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessageTransport.ReadAsync(stream));
    }
}
