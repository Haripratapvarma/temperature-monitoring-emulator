using System.Collections.Concurrent;
using System.Net.Sockets;
using TempLab.Protocol;

namespace TempLab.Client;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

public sealed record ClientOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5055;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxFrameBytes { get; init; } = FrameCodec.DefaultMaxFrameBytes;
    public ReconnectPolicy Reconnect { get; init; } = new();
}

/// <summary>Bounded exponential backoff. Never retries forever.</summary>
public sealed record ReconnectPolicy
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public double Multiplier { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan DelayBeforeAttempt(int attempt) // attempt is 1-based; no delay before the first
    {
        if (attempt <= 1) return TimeSpan.Zero;
        double ms = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 2);
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
    }
}

public enum DisconnectKind
{
    ClientRequested,
    RemoteClosed,
    ProtocolError,
    IoError,
}

public sealed record DisconnectInfo(DisconnectKind Kind, string Reason);

public class InstrumentException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class DeviceCommandException(string command, ErrorInfo error)
    : InstrumentException($"{command} failed: {error.Code}: {error.Message}")
{
    public string Command { get; } = command;
    public ErrorInfo Error { get; } = error;
}

public sealed class RequestTimeoutException(string command, string requestId, TimeSpan timeout)
    : InstrumentException($"{command} ({requestId}) timed out after {timeout.TotalMilliseconds:0} ms")
{
    public string RequestId { get; } = requestId;
}

public sealed class ConnectionLostException(string reason, Exception? inner = null)
    : InstrumentException("connection lost: " + reason, inner);

/// <summary>
/// Client for the emulator protocol.
/// <list type="bullet">
/// <item>Exact-read framing (see <see cref="FrameCodec"/>); protocol errors close the connection.</item>
/// <item>Responses are matched to requests by ID. A response that arrives after its request timed out is
/// counted and dropped: it can never complete a different request.</item>
/// <item>Writes are serialised with a lock, so concurrent requests never interleave frame bytes.</item>
/// <item>Events (<see cref="SamplesReceived"/>, <see cref="DeviceFaultReceived"/>, <see cref="Disconnected"/>)
/// are raised on the reader thread; handlers must be fast and must not block (hand off to a queue).</item>
/// </list>
/// </summary>
public sealed class InstrumentClient : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly StructuredLogger _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Command, TaskCompletionSource<ResponsePayload> Tcs)> _pending = new();
    private long _nextRequest;
    private long _lateResponses;
    private long _protocolErrors;
    private int _state = (int)ConnectionState.Disconnected;

    private Connection? _connection;

    private sealed class Connection(TcpClient tcp, CancellationTokenSource cts)
    {
        public TcpClient Tcp { get; } = tcp;
        public NetworkStream Stream { get; } = tcp.GetStream();
        public CancellationTokenSource Cts { get; } = cts;
        public Task ReadLoop { get; set; } = Task.CompletedTask;
        public int Closed;
    }

    public InstrumentClient(ClientOptions options, StructuredLogger? log = null)
    {
        _options = options;
        _log = log ?? StructuredLogger.Null;
    }

    public ClientOptions Options => _options;
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);
    public long LateResponses => Interlocked.Read(ref _lateResponses);
    public long ProtocolErrors => Interlocked.Read(ref _protocolErrors);

    public event Action<SampleBatchPayload>? SamplesReceived;
    public event Action<DeviceFaultPayload>? DeviceFaultReceived;
    public event Action<DisconnectInfo>? Disconnected;
    public event Action<ConnectionState>? StateChanged;

    private void SetState(ConnectionState s)
    {
        if ((ConnectionState)Interlocked.Exchange(ref _state, (int)s) != s) StateChanged?.Invoke(s);
    }

    /// <summary>Opens the TCP connection and verifies the device answers <c>status</c>.</summary>
    public async Task<DeviceStatus> ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is not null) throw new InvalidOperationException("already connected");
            SetState(ConnectionState.Connecting);
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.ConnectTimeout);
                await tcp.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                tcp.Dispose();
                SetState(ConnectionState.Disconnected);
                _log.Warn("connect_failed", ("host", _options.Host), ("port", _options.Port), ("error", ex.Message));
                throw new ConnectionLostException("connect failed: " + ex.Message, ex);
            }
            var conn = new Connection(tcp, new CancellationTokenSource());
            _connection = conn;
            conn.ReadLoop = Task.Run(() => ReadLoopAsync(conn));
            SetState(ConnectionState.Connected);
            _log.Info("connected", ("host", _options.Host), ("port", _options.Port));
        }
        finally
        {
            _connectLock.Release();
        }

        try
        {
            return await GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reconnects with bounded backoff, returning the device status from the first successful attempt.
    /// It deliberately does not send <c>start</c>: the caller reconciles using the returned status.
    /// </summary>
    public async Task<DeviceStatus> ReconnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        var policy = _options.Reconnect;
        Exception? last = null;
        for (int attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            var delay = policy.DelayBeforeAttempt(attempt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            try
            {
                _log.Info("reconnect_attempt", ("attempt", attempt), ("maxAttempts", policy.MaxAttempts));
                return await ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (InstrumentException ex)
            {
                last = ex;
            }
        }
        throw new ConnectionLostException($"reconnect failed after {policy.MaxAttempts} attempts", last);
    }

    public Task DisconnectAsync() => CloseAsync(DisconnectKind.ClientRequested, "client requested");

    private async Task CloseAsync(DisconnectKind kind, string reason, Connection? only = null)
    {
        var conn = only ?? _connection;
        if (conn is null || Interlocked.Exchange(ref conn.Closed, 1) == 1) return;
        Interlocked.CompareExchange(ref _connection, null, conn);
        conn.Cts.Cancel();
        try { conn.Tcp.Client.Shutdown(SocketShutdown.Both); } catch { }
        conn.Tcp.Dispose();
        if (only is null)
        {
            try { await conn.ReadLoop.ConfigureAwait(false); } catch { }
        }
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var p))
                p.Tcs.TrySetException(new ConnectionLostException(reason));
        }
        SetState(ConnectionState.Disconnected);
        _log.Info("disconnected", ("kind", kind.ToString()), ("reason", reason));
        Disconnected?.Invoke(new DisconnectInfo(kind, reason));
    }

    private async Task ReadLoopAsync(Connection conn)
    {
        var ct = conn.Cts.Token;
        DisconnectKind kind = DisconnectKind.RemoteClosed;
        string reason = "remote closed the connection";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var env = await FrameCodec.ReadEnvelopeAsync(conn.Stream, _options.MaxFrameBytes, ct).ConfigureAwait(false);
                if (env is null) break;
                Dispatch(env);
            }
        }
        catch (ProtocolException ex)
        {
            Interlocked.Increment(ref _protocolErrors);
            kind = DisconnectKind.ProtocolError;
            reason = $"protocol error ({ex.Kind}): {ex.Message}";
            _log.Error("protocol_error", ("kind", ex.Kind.ToString()), ("error", ex.Message));
        }
        catch (System.Text.Json.JsonException ex)
        {
            Interlocked.Increment(ref _protocolErrors);
            kind = DisconnectKind.ProtocolError;
            reason = "protocol error (InvalidMessage): " + ex.Message;
            _log.Error("protocol_error", ("kind", "InvalidMessage"), ("error", ex.Message));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // CloseAsync initiated by us handles notification
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (ct.IsCancellationRequested) return;
            kind = DisconnectKind.IoError;
            reason = "io error: " + ex.Message;
        }
        _ = CloseAsync(kind, reason, conn);
    }

    private void Dispatch(Envelope env)
    {
        switch (env.Type)
        {
            case MessageTypes.Response:
            {
                var payload = ProtocolJson.FromElement<ResponsePayload>(env.Payload)
                              ?? throw new ProtocolException(ProtocolErrorKind.InvalidMessage, "response without payload");
                if (env.Id is null)
                {
                    // Unsolicited error (e.g. Busy on connect, or the server reporting our protocol error).
                    _log.Warn("unsolicited_error", ("code", payload.Error?.Code), ("message", payload.Error?.Message));
                    var err = payload.Error ?? new ErrorInfo(ErrorCodes.Internal, "unsolicited response");
                    foreach (var key in _pending.Keys)
                        if (_pending.TryRemove(key, out var p)) p.Tcs.TrySetException(new DeviceCommandException(p.Command, err));
                    return;
                }
                if (_pending.TryRemove(env.Id, out var pending))
                {
                    pending.Tcs.TrySetResult(payload);
                }
                else
                {
                    Interlocked.Increment(ref _lateResponses);
                    _log.Warn("late_response_dropped", ("requestId", env.Id));
                }
                break;
            }
            case MessageTypes.Samples:
            {
                var batch = ProtocolJson.FromElement<SampleBatchPayload>(env.Payload)
                            ?? throw new ProtocolException(ProtocolErrorKind.InvalidMessage, "samples without payload");
                SamplesReceived?.Invoke(batch);
                break;
            }
            case MessageTypes.DeviceFault:
            {
                var f = ProtocolJson.FromElement<DeviceFaultPayload>(env.Payload)
                        ?? throw new ProtocolException(ProtocolErrorKind.InvalidMessage, "deviceFault without payload");
                _log.Warn("device_fault", ("acquisitionId", f.AcquisitionId), ("code", f.Code), ("message", f.Message));
                DeviceFaultReceived?.Invoke(f);
                break;
            }
            default:
                _log.Warn("unknown_message", ("type", env.Type));
                break;
        }
    }

    /// <summary>Sends a request and waits for its response (or timeout/cancellation/disconnect).</summary>
    public async Task<ResponsePayload> SendAsync(string command, object? payload = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var conn = _connection ?? throw new ConnectionLostException("not connected");
        string id = "c-" + Interlocked.Increment(ref _nextRequest);
        var tcs = new TaskCompletionSource<ResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (command, tcs);
        var frame = FrameCodec.EncodeEnvelope(ProtocolJson.Request(command, id, payload));
        var limit = timeout ?? _options.RequestTimeout;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await conn.Stream.WriteAsync(frame, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
            _log.Debug("request_sent", ("requestId", id), ("command", command));
            return await tcs.Task.WaitAsync(limit, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn("request_timeout", ("requestId", id), ("command", command), ("timeoutMs", limit.TotalMilliseconds));
            throw new RequestTimeoutException(command, id, limit);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            throw new ConnectionLostException("write failed: " + ex.Message, ex);
        }
        finally
        {
            _pending.TryRemove(id, out _); // a late reply now finds nothing to complete
        }
    }

    private async Task<T?> CallAsync<T>(string command, object? payload, CancellationToken ct)
    {
        var r = await SendAsync(command, payload, null, ct).ConfigureAwait(false);
        if (!r.Ok) throw new DeviceCommandException(command, r.Error ?? new ErrorInfo(ErrorCodes.Internal, "no error info"));
        return ProtocolJson.FromElement<T>(r.Result);
    }

    public Task ConfigureAsync(DeviceConfiguration config, CancellationToken ct = default) =>
        CallAsync<object>(MessageTypes.Configure, config, ct);

    public async Task<StartResult> StartAsync(CancellationToken ct = default) =>
        await CallAsync<StartResult>(MessageTypes.Start, null, ct).ConfigureAwait(false)
        ?? throw new InstrumentException("start returned no acquisition id");

    public async Task<StopResult> StopAsync(CancellationToken ct = default) =>
        await CallAsync<StopResult>(MessageTypes.Stop, null, ct).ConfigureAwait(false) ?? new StopResult(false, 0);

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken ct = default) =>
        await CallAsync<DeviceStatus>(MessageTypes.Status, null, ct).ConfigureAwait(false)
        ?? throw new InstrumentException("status returned no payload");

    public Task ResetAsync(CancellationToken ct = default) => CallAsync<object>(MessageTypes.Reset, null, ct);

    public Task SetFaultsAsync(FaultScenario scenario, CancellationToken ct = default) =>
        CallAsync<object>(MessageTypes.SetFaults, scenario, ct);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _connectLock.Dispose();
    }
}

public enum ReconcileOutcome
{
    /// <summary>The device is still running the acquisition we started; keep consuming.</summary>
    StillAcquiring,
    /// <summary>Our acquisition is gone (device idle or running a different one). Do not restart automatically.</summary>
    Interrupted,
    /// <summary>The device is faulted and needs an explicit reset.</summary>
    DeviceFaulted,
}

public static class AcquisitionReconciler
{
    public static ReconcileOutcome Reconcile(DeviceStatus status, string expectedAcquisitionId) => status.State switch
    {
        DeviceState.Acquiring when status.AcquisitionId == expectedAcquisitionId => ReconcileOutcome.StillAcquiring,
        DeviceState.Faulted => ReconcileOutcome.DeviceFaulted,
        _ => ReconcileOutcome.Interrupted,
    };
}
