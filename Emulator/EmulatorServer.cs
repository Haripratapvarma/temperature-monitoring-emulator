using System.Net;
using System.Net.Sockets;
using TempLab.Protocol;

namespace TempLab.Emulator;

/// <summary>
/// Accepts TCP connections. Exactly one active client owns the device; others get <c>Busy</c> and are closed.
/// </summary>
public sealed class EmulatorServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly StructuredLogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task _acceptLoop = Task.CompletedTask;
    private Task _activeSession = Task.CompletedTask;
    private int _hasActive;
    private int _disposed;

    public InstrumentDevice Device { get; }
    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public EmulatorServer(IPAddress address, int port, StructuredLogger log, FaultScenario? scenario = null,
        InstrumentDevice? device = null)
    {
        _listener = new TcpListener(address, port);
        _log = log;
        Device = device ?? new InstrumentDevice(log.ForComponent("device"), scenario);
    }

    public void Start()
    {
        _listener.Start();
        _log.Info("server_started", ("endpoint", EndPoint.ToString()));
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }

            if (Interlocked.CompareExchange(ref _hasActive, 1, 0) != 0)
            {
                _log.Warn("connection_rejected", ("remote", client.Client.RemoteEndPoint?.ToString()), ("reason", "busy"));
                _ = RejectBusyAsync(client);
                continue;
            }

            var session = new ClientSession(client, Device, _log.ForComponent("session"));
            _log.Info("connection_accepted", ("connectionId", session.ConnectionId), ("remote", client.Client.RemoteEndPoint?.ToString()));
            _activeSession = Task.Run(async () =>
            {
                try { await session.RunAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { _log.Error("session_failed", ("error", ex.ToString())); }
                finally { Volatile.Write(ref _hasActive, 0); }
            });
        }
    }

    private static async Task RejectBusyAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var frame = FrameCodec.EncodeEnvelope(ProtocolJson.Error(null, ErrorCodes.Busy, "another client is connected"));
                await client.GetStream().WriteAsync(frame).ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Send);
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        catch { /* client went away */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        try { await _activeSession.ConfigureAwait(false); } catch { }
        _log.Info("server_stopped");
        _cts.Dispose();
    }
}
