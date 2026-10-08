using System.Net.Sockets;
using System.Threading.Channels;
using TempLab.Protocol;

namespace TempLab.Emulator;

/// <summary>
/// One TCP connection. A reader loop decodes requests and runs them on the device in order;
/// a single writer task owns the socket's write side so frame bytes never interleave.
/// Sample frames are bounded (<see cref="MaxPendingSampleFrames"/>); responses and events are always queued.
/// </summary>
public sealed class ClientSession : IDeviceOutput, IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly InstrumentDevice _device;
    private readonly StructuredLogger _log;
    private readonly Channel<(byte[] Frame, bool IsSample)> _outbound =
        Channel.CreateUnbounded<(byte[], bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _pendingSampleFrames;
    private int _aborted;
    private Task _writer = Task.CompletedTask;

    public int MaxPendingSampleFrames { get; init; } = 256;
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public long SampleFramesDroppedForOverflow { get; private set; }

    public ClientSession(TcpClient client, InstrumentDevice device, StructuredLogger log)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        _device = device;
        _log = log;
    }

    public async Task RunAsync(CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _cts.Token);
        var ct = linked.Token;
        var writer = _writer = Task.Run(() => WriteLoopAsync(ct));
        string reason = "client closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Envelope? request;
                try
                {
                    request = await FrameCodec.ReadEnvelopeAsync(_stream, FrameCodec.DefaultMaxFrameBytes, ct).ConfigureAwait(false);
                }
                catch (ProtocolException ex)
                {
                    reason = "protocol error: " + ex.Message;
                    _log.Warn("protocol_error", ("connectionId", ConnectionId), ("kind", ex.Kind.ToString()), ("error", ex.Message));
                    // Best effort: tell the client why, then close.
                    SendEvent(ProtocolJson.Error(null, ErrorCodes.BadRequest, ex.Message));
                    break;
                }
                if (request is null) break;

                var result = await _device.ExecuteAsync(request, this, ct).ConfigureAwait(false);
                if (result.Delay > TimeSpan.Zero)
                {
                    _log.Warn("fault_injected", ("kind", "DelayReply"), ("requestId", request.Id), ("delayMs", result.Delay.TotalMilliseconds));
                    _ = SendDelayedAsync(result.Response, result.Delay, ct);
                }
                else
                {
                    Enqueue(result.Response, isSample: false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = Volatile.Read(ref _aborted) == 1 ? "aborted" : "server shutdown";
        }
        catch (IOException ex)
        {
            reason = "io error: " + ex.Message;
        }
        catch (ObjectDisposedException)
        {
            reason = "aborted";
        }
        finally
        {
            _log.Info("connection_closed", ("connectionId", ConnectionId), ("reason", reason));
            await _device.OnClientDisconnectedAsync(reason).ConfigureAwait(false);
            _outbound.Writer.TryComplete();
            _cts.Cancel();
            try { await writer.ConfigureAwait(false); } catch { /* socket already gone */ }
            _client.Dispose();
        }
    }

    private async Task SendDelayedAsync(Envelope response, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            Enqueue(response, isSample: false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (frame, isSample) in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (isSample) Interlocked.Decrement(ref _pendingSampleFrames);
                await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { Abort("write failed"); }
        catch (ObjectDisposedException) { }
    }

    private void Enqueue(Envelope env, bool isSample) => _outbound.Writer.TryWrite((FrameCodec.EncodeEnvelope(env), isSample));

    public bool TrySendSamples(Envelope envelope)
    {
        if (Interlocked.Increment(ref _pendingSampleFrames) > MaxPendingSampleFrames)
        {
            Interlocked.Decrement(ref _pendingSampleFrames);
            SampleFramesDroppedForOverflow++;
            return false;
        }
        if (!_outbound.Writer.TryWrite((FrameCodec.EncodeEnvelope(envelope), true)))
        {
            Interlocked.Decrement(ref _pendingSampleFrames);
            return false;
        }
        return true;
    }

    public void SendEvent(Envelope envelope) => Enqueue(envelope, isSample: false);

    public void SendRawFrame(byte[] frame) => _outbound.Writer.TryWrite((frame, false));

    public void Abort(string reason)
    {
        if (Interlocked.Exchange(ref _aborted, 1) == 1) return;
        _log.Warn("connection_abort", ("connectionId", ConnectionId), ("reason", reason));
        // Let already-queued frames (e.g. an injected oversized header) go out first, then close.
        _ = Task.Run(async () =>
        {
            try
            {
                _outbound.Writer.TryComplete();
                await Task.WhenAny(_writer, Task.Delay(500)).ConfigureAwait(false); // writer drains, then exits
                try { _client.Client.Shutdown(SocketShutdown.Both); } catch { }
                _cts.Cancel();
                _client.Close();
            }
            catch (Exception ex)
            {
                _log.Error("abort_failed", ("connectionId", ConnectionId), ("error", ex.Message));
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _client.Dispose();
        await Task.CompletedTask;
    }
}
