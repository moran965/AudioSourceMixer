using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using AudioSourceMixer.Core.Infrastructure;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Core.Browser;

public sealed class BrowserBridgeServer : IAsyncDisposable
{
    public const string PipeName = "AudioSourceMixer.BrowserBridge.v1";
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly ConcurrentDictionary<int, Task> _connectionHandlers = new();
    private readonly ConcurrentDictionary<AudioSourceId, BrowserTabSource> _tabs = new();
    private readonly Dictionary<AudioSourceId, int> _tabOwners = [];
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AudioSourceId, string> _pendingBySource = new();
    private readonly ConcurrentDictionary<AudioSourceId, long> _commandGenerations = new();
    private readonly ConcurrentDictionary<AudioSourceId, long> _observedGenerations = new();
    private readonly object _tabsGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly RollingFileLogger? _logger;
    private readonly TimeSpan _commandTimeout;
    private Task? _acceptLoop;
    private int _connectionId;
    private int _disposed;

    public event EventHandler<IReadOnlyList<BrowserTabSource>>? TabsChanged;
    public bool IsConnected => !_connections.IsEmpty;
    public IReadOnlyList<BrowserConnectionStatus> GetConnectionStatuses() => _connections.Values
        .Where(connection => connection.Browser is not null)
        .GroupBy(connection => connection.Browser!, StringComparer.OrdinalIgnoreCase)
        .Select(group => new BrowserConnectionStatus(group.Key, true,
            group.Select(item => item.ExtensionVersion).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))))
        .OrderBy(status => status.Browser).ToArray();

    public BrowserBridgeServer(string? pipeName = null, RollingFileLogger? logger = null,
        TimeSpan? commandTimeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
        _logger = logger;
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(10);
    }

    public void Start() => _acceptLoop ??= AcceptLoopAsync(_shutdown.Token);

    public IReadOnlyList<BrowserTabSource> GetTabs() => _tabs.Values.OrderBy(tab => tab.Browser).ThenBy(tab => tab.TabId).ToArray();

    public Task OpenOutputManagerAsync(string browser, CancellationToken cancellationToken = default)
        => SendBrowserCommandAsync(browser, "bridge.openOptions", cancellationToken);

    public Task ClearOutputMappingsAsync(string browser, CancellationToken cancellationToken = default)
        => SendBrowserCommandAsync(browser, "bridge.clearMappings", cancellationToken);

    private async Task SendBrowserCommandAsync(string browser, string type, CancellationToken cancellationToken)
    {
        if (browser is not ("chrome" or "edge")) throw new ArgumentOutOfRangeException(nameof(browser));
        var connection = _connections.Values.FirstOrDefault(item =>
            string.Equals(item.Browser, browser, StringComparison.OrdinalIgnoreCase));
        if (connection is null)
            throw new IOException($"{browser} 扩展尚未连接；请先打开浏览器并启用一个标签页。 ");
        await connection.WriteAsync(Encoding.UTF8.GetString(BrowserProtocol.Serialize(new BrowserMessage
        {
            Type = type, Browser = browser, ProtocolVersion = connection.ProtocolVersion
        })), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAudioAsync(AudioSourceId sourceId, float volume, float balance, bool muted,
        string outputDeviceId = "", string? outputDeviceName = null,
        IReadOnlyList<OutputDeviceInfo>? outputDevices = null, CancellationToken cancellationToken = default,
        AudioRouteRequestSource requestSource = AudioRouteRequestSource.ProfileRestore,
        bool forceAuthorization = false,
        AudioEffectSettings? effects = null)
    {
        var tab = _tabs.TryGetValue(sourceId, out var value) ? value : throw new KeyNotFoundException($"Browser source {sourceId} is unavailable.");
        var connection = GetOwnerConnection(sourceId);
        if (tab.ProtocolVersion == BrowserProtocol.LegacyVersion && volume > 1)
            throw new NotSupportedException("The connected browser extension uses protocol 1 and supports volume only up to 100%. Reload the current extension.");
        var correlationId = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion ? Guid.NewGuid().ToString("N") : null;
        var observedGeneration = _observedGenerations.GetValueOrDefault(sourceId);
        var generation = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion
            ? _commandGenerations.AddOrUpdate(sourceId,
                _ => NextGeneration(observedGeneration),
                (_, previous) => NextGeneration(Math.Max(previous, observedGeneration)))
            : 0;
        PendingCommand? pending = null;
        if (correlationId is not null)
        {
            pending = new PendingCommand(sourceId, connection.Id, correlationId, generation);
            if (_pendingBySource.TryGetValue(sourceId, out var previousCorrelation) &&
                _pendingCommands.TryRemove(previousCorrelation, out var previous))
                previous.Completion.TrySetException(new OperationCanceledException("A newer browser audio command superseded this command."));
            _pendingBySource[sourceId] = correlationId;
            _pendingCommands[correlationId] = pending;
        }
        try
        {
            await connection.WriteAsync(Encoding.UTF8.GetString(BrowserProtocol.Serialize(new BrowserMessage
            {
                ProtocolVersion = tab.ProtocolVersion,
                Type = "tab.setAudio", Browser = tab.Browser, TabId = tab.TabId, SourceId = sourceId.Value,
                Volume = tab.ProtocolVersion == BrowserProtocol.LegacyVersion ? AudioMath.EnsureSessionVolume(volume) : AudioMath.EnsureUserGain(volume),
                Balance = Math.Clamp(balance, -1, 1), Muted = muted,
                OutputDeviceId = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion ? outputDeviceId : null,
                OutputDeviceName = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion ? outputDeviceName : null,
                CorrelationId = correlationId,
                Generation = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion ? generation : null,
                RequestSource = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion ? requestSource.ToString() : null,
                ForceAuthorization = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion ? forceAuthorization : null,
                Equalizer = tab.ProtocolVersion >= BrowserProtocol.Version ? EqualizerCatalog.Normalize(effects) : null,
                OutputDevices = tab.ProtocolVersion >= BrowserProtocol.RoutingVersion ? outputDevices?.Select(device => new BrowserOutputEndpoint
                {
                    EndpointId = device.Id,
                    FriendlyName = device.Name,
                    IsSystemDefault = device.IsSystemDefault,
                    IsDefaultMultimedia = device.IsDefaultMultimedia,
                    IsAvailable = device.IsAvailable
                }).ToArray() : null
            })), cancellationToken).ConfigureAwait(false);
            if (pending is null) return;
            var result = await pending.Completion.Task.WaitAsync(_commandTimeout, cancellationToken).ConfigureAwait(false);
            if (result.RoutingState == "Failed")
                throw new InvalidOperationException(result.Error ?? $"Browser audio route {correlationId} failed.");
        }
        finally
        {
            if (correlationId is not null)
            {
                _pendingCommands.TryRemove(correlationId, out _);
                _pendingBySource.TryRemove(new KeyValuePair<AudioSourceId, string>(sourceId, correlationId));
            }
        }
    }

    public async Task StopAsync(AudioSourceId sourceId, CancellationToken cancellationToken = default)
    {
        var tab = _tabs.TryGetValue(sourceId, out var value) ? value : throw new KeyNotFoundException($"Browser source {sourceId} is unavailable.");
        var connection = GetOwnerConnection(sourceId);
        await connection.WriteAsync(Encoding.UTF8.GetString(BrowserProtocol.Serialize(new BrowserMessage
        {
            ProtocolVersion = tab.ProtocolVersion, Type = "tab.stop", Browser = tab.Browser,
            TabId = tab.TabId, SourceId = sourceId.Value
        })), cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var sourceId in _tabs.Keys.ToArray())
        {
            try { await StopAsync(sourceId, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or KeyNotFoundException or ObjectDisposedException) { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _connectionId);
                var connection = new Connection(id, server);
                _connections[id] = connection;
                TabsChanged?.Invoke(this, GetTabs());
                var handler = HandleConnectionAsync(connection, cancellationToken);
                _connectionHandlers[id] = handler;
            }
            catch (OperationCanceledException) { await server.DisposeAsync().ConfigureAwait(false); }
            catch { await server.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private async Task HandleConnectionAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && connection.Stream.IsConnected)
            {
                var line = await connection.Reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                BrowserMessage message;
                try { message = BrowserProtocol.Parse(Encoding.UTF8.GetBytes(line)); }
                catch (InvalidDataException) { continue; }
                await HandleMessageAsync(connection, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            var changed = false;
            lock (_tabsGate)
            {
                foreach (var sourceId in connection.SourceIds)
                {
                    if (!_tabOwners.TryGetValue(sourceId, out var owner) || owner != connection.Id) continue;
                    _tabOwners.Remove(sourceId);
                    changed |= _tabs.TryRemove(sourceId, out _);
                    FailPendingForSource(sourceId, new IOException($"Browser connection for {sourceId} disconnected."));
                }
            }
            await connection.DisposeAsync().ConfigureAwait(false);
            if (changed || _connections.IsEmpty) TabsChanged?.Invoke(this, GetTabs());
            _connectionHandlers.TryRemove(connection.Id, out _);
        }
    }

    private async Task HandleMessageAsync(Connection connection, BrowserMessage message, CancellationToken cancellationToken)
    {
        connection.ProtocolVersion = message.ProtocolVersion;
        if (message.Type == "bridge.hello")
        {
            connection.Browser = message.Browser;
            connection.ExtensionVersion = message.ExtensionVersion;
            await connection.WriteAsync(Encoding.UTF8.GetString(BrowserProtocol.Serialize(new BrowserMessage
            {
                ProtocolVersion = message.ProtocolVersion,
                Type = "bridge.status",
                Error = null
            })), cancellationToken).ConfigureAwait(false);
            TabsChanged?.Invoke(this, GetTabs());
        }
        else if (message.Type is "tab.register" or "tab.update")
        {
            var id = BrowserProtocol.GetSourceId(message);
            if (message.Type == "tab.update" && ShouldIgnoreTabUpdate(id, connection.Id, message)) return;
            if (message.Generation is { } observedGeneration)
                _observedGenerations.AddOrUpdate(id, observedGeneration, (_, current) => Math.Max(current, observedGeneration));
            lock (_tabsGate)
            {
                connection.SourceIds.Add(id);
                _tabOwners[id] = connection.Id;
                _tabs.AddOrUpdate(id,
                    _ => new BrowserTabSource(id, message.Browser!, message.TabId!.Value, message.Title ?? "未命名标签页",
                        SanitizeOrigin(message.Origin), message.CaptureState ?? "active", message.Volume ?? 1, message.Balance ?? 0,
                        message.Muted ?? false, message.Peak ?? 0, message.ProtocolVersion,
                        message.OutputDeviceId ?? "", message.OutputDeviceName, message.OutputStatus,
                        message.EffectiveSinkId ?? "", message.EffectiveSinkLabel,
                        ParseRoutingState(message.RoutingState), message.Error, message.CorrelationId,
                        message.BrowserDeviceId, message.EffectiveSinkId, message.Equalizer),
                    (_, old) => old with
                    {
                        Title = message.Title ?? old.Title,
                        Origin = message.Origin is null ? old.Origin : SanitizeOrigin(message.Origin),
                        CaptureState = message.CaptureState ?? old.CaptureState,
                        Volume = message.Volume ?? old.Volume,
                        Balance = message.Balance ?? old.Balance,
                        Muted = message.Muted ?? old.Muted,
                        Peak = message.Peak ?? old.Peak,
                        ProtocolVersion = message.ProtocolVersion,
                        OutputDeviceId = message.OutputDeviceId ?? old.OutputDeviceId,
                        OutputDeviceName = message.OutputDeviceName ?? old.OutputDeviceName,
                        OutputStatus = message.OutputStatus ?? old.OutputStatus,
                        EffectiveOutputDeviceId = message.EffectiveSinkId ?? old.EffectiveOutputDeviceId,
                        EffectiveOutputDeviceName = message.EffectiveSinkLabel ?? old.EffectiveOutputDeviceName,
                        RoutingState = message.RoutingState is null ? old.RoutingState : ParseRoutingState(message.RoutingState),
                        RoutingError = message.Error ?? (message.RoutingState == "Applied" ? null : old.RoutingError),
                        CorrelationId = message.CorrelationId ?? old.CorrelationId,
                        BrowserDeviceId = message.BrowserDeviceId ?? old.BrowserDeviceId,
                        EffectiveBrowserSinkId = message.EffectiveSinkId ?? old.EffectiveBrowserSinkId,
                        Effects = message.Equalizer ?? old.Effects
                    });
            }
            if (!string.IsNullOrWhiteSpace(message.CorrelationId))
            {
                _logger?.Info($"Browser sink result correlation={message.CorrelationId}; browser={message.Browser}; " +
                    $"tab={message.TabId}; windowsEndpoint={message.OutputDeviceId}; windowsName={message.OutputDeviceName}; " +
                    $"browserDeviceHash={HashIdentifier(message.BrowserDeviceId)}; sinkHash={HashIdentifier(message.EffectiveSinkId)}; " +
                    $"setSinkSupported={message.SetSinkIdSupported}; sinkMatched={string.Equals(message.BrowserDeviceId, message.EffectiveSinkId, StringComparison.Ordinal) && !string.IsNullOrEmpty(message.EffectiveSinkId)}; " +
                    $"durationMs={message.SetSinkDurationMs}; state={message.RoutingState}; error={message.Error}");
            }
            CompletePendingCommand(id, connection.Id, message);
            TabsChanged?.Invoke(this, GetTabs());
        }
        else if (message.Type == "tab.unregister")
        {
            var id = BrowserProtocol.GetSourceId(message);
            var changed = false;
            lock (_tabsGate)
            {
                if (_tabOwners.TryGetValue(id, out var owner) && owner == connection.Id)
                {
                    _tabOwners.Remove(id);
                    connection.SourceIds.Remove(id);
                    changed = _tabs.TryRemove(id, out _);
                }
            }
            if (changed) TabsChanged?.Invoke(this, GetTabs());
        }
    }

    private Connection GetOwnerConnection(AudioSourceId sourceId)
    {
        lock (_tabsGate)
        {
            if (!_tabOwners.TryGetValue(sourceId, out var owner) || !_connections.TryGetValue(owner, out var connection))
                throw new IOException($"The browser connection for {sourceId} is unavailable.");
            return connection;
        }
    }

    private void CompletePendingCommand(AudioSourceId sourceId, int connectionId, BrowserMessage message)
    {
        if (message.Type != "tab.update" || string.IsNullOrWhiteSpace(message.CorrelationId) ||
            !_pendingCommands.TryGetValue(message.CorrelationId, out var pending)) return;
        if (pending.SourceId != sourceId || pending.ConnectionId != connectionId || message.Generation != pending.Generation)
        {
            _logger?.Info($"Ignored stale browser sink acknowledgement. Correlation={message.CorrelationId}; Source={sourceId}; Generation={message.Generation}; ExpectedSource={pending.SourceId}; ExpectedGeneration={pending.Generation}.");
            return;
        }
        if (message.RoutingState is not ("PendingAuthorization" or "Applied" or "Failed" or "Default" or "SystemDefault")) return;
        pending.Completion.TrySetResult(message);
    }

    private bool ShouldIgnoreTabUpdate(AudioSourceId sourceId, int connectionId, BrowserMessage message)
    {
        if (message.Generation is { } generation && _observedGenerations.TryGetValue(sourceId, out var observed) && generation < observed)
        {
            _logger?.Info($"Ignored stale browser tab update. Source={sourceId}; Correlation={message.CorrelationId}; Generation={generation}; Observed={observed}.");
            return true;
        }
        if (!_pendingBySource.TryGetValue(sourceId, out var expectedCorrelation) || string.IsNullOrWhiteSpace(message.CorrelationId))
            return false;
        _pendingCommands.TryGetValue(expectedCorrelation, out var pending);
        if (!string.Equals(message.CorrelationId, expectedCorrelation, StringComparison.Ordinal) ||
            pending is null || pending.ConnectionId != connectionId ||
            message.Generation != pending.Generation)
        {
            _logger?.Info($"Ignored unmatched browser tab update. Source={sourceId}; Correlation={message.CorrelationId}; Generation={message.Generation}; ExpectedCorrelation={expectedCorrelation}; ExpectedGeneration={(pending is null ? null : pending.Generation)}.");
            return true;
        }
        return false;
    }

    private void FailPendingForSource(AudioSourceId sourceId, Exception exception)
    {
        if (!_pendingBySource.TryRemove(sourceId, out var correlationId) ||
            !_pendingCommands.TryRemove(correlationId, out var pending)) return;
        pending.Completion.TrySetException(exception);
    }

    private static long NextGeneration(long current)
    {
        if (current >= BrowserProtocol.MaximumJavaScriptSafeInteger)
            throw new InvalidOperationException("Browser command generation exhausted the JavaScript safe integer range.");
        return current + 1;
    }

    private static string SanitizeOrigin(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : string.Empty;

    private static string HashIdentifier(string? value)
        => string.IsNullOrEmpty(value) ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    private static AudioRoutingState ParseRoutingState(string? state) => state switch
    {
        "PendingAuthorization" => AudioRoutingState.PendingAuthorization,
        "Applied" => AudioRoutingState.Applied,
        "Fallback" => AudioRoutingState.Disconnected,
        "Failed" => AudioRoutingState.Failed,
        "Default" or "SystemDefault" => AudioRoutingState.SystemDefault,
        "PendingStreamRestart" => AudioRoutingState.PendingStreamRestart,
        "Partial" => AudioRoutingState.Partial,
        "Disconnected" => AudioRoutingState.Disconnected,
        _ => AudioRoutingState.SystemDefault
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        using (var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try { await StopAllAsync(stopTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _shutdown.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        foreach (var connection in _connections.Values) await connection.DisposeAsync().ConfigureAwait(false);
        foreach (var pending in _pendingCommands.Values)
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(BrowserBridgeServer)));
        _pendingCommands.Clear();
        _pendingBySource.Clear();
        _observedGenerations.Clear();
        var handlers = _connectionHandlers.Values.ToArray();
        if (handlers.Length > 0)
        {
            try { await Task.WhenAll(handlers).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        _connections.Clear();
        _connectionHandlers.Clear();
        _shutdown.Dispose();
    }

    private sealed class Connection(int id, NamedPipeServerStream stream) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _disposed;
        public int Id { get; } = id;
        public int ProtocolVersion { get; set; } = BrowserProtocol.LegacyVersion;
        public string? Browser { get; set; }
        public string? ExtensionVersion { get; set; }
        public NamedPipeServerStream Stream { get; } = stream;
        public HashSet<AudioSourceId> SourceIds { get; } = [];
        public StreamReader Reader { get; } = new(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        private StreamWriter Writer { get; } = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        public async Task WriteAsync(string json, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await Writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            try { Stream.Dispose(); } catch { }
            try { Reader.Dispose(); } catch { }
            try { Writer.Dispose(); } catch { }
            try { _writeGate.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record PendingCommand(AudioSourceId SourceId, int ConnectionId, string CorrelationId,
        long Generation)
    {
        public TaskCompletionSource<BrowserMessage> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record BrowserConnectionStatus(string Browser, bool IsConnected, string? ExtensionVersion);
