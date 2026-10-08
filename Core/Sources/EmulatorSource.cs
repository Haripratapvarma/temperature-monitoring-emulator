using System.Diagnostics;
using TempLab.Client;
using TempLab.Protocol;

namespace TempLab.Acquisition.Sources;

/// <summary>
/// Live acquisition from the emulator. The client connection is owned by the caller (the UI connects and
/// disconnects); this source configures, starts and stops one acquisition on it.
/// <para>On an unexpected disconnect the acquisition is reported as interrupted, the client reconnects with
/// bounded backoff and reconciles from <c>status</c>. It never sends <c>start</c> again on its own.</para>
/// </summary>
public sealed class EmulatorSource(InstrumentClient client, StructuredLogger? log = null) : IAcquisitionSource
{
    private readonly StructuredLogger _log = log ?? StructuredLogger.Null;
    private ISampleSink? _sink;
    private string? _acquisitionId;
    private volatile bool _active;
    private Task _recovery = Task.CompletedTask;
    private CancellationTokenSource? _recoveryCts;

    public string Description => $"emulator {client.Options.Host}:{client.Options.Port}";

    /// <summary>Set false to only report interruptions (no automatic reconnect).</summary>
    public bool AutoReconnect { get; init; } = true;

    public async Task<SourceStartInfo> StartAsync(AcquisitionSettings settings, ISampleSink sink, CancellationToken ct)
    {
        if (client.State != ConnectionState.Connected) throw new InvalidOperationException("connect to the emulator first");
        var device = settings.ToDeviceConfiguration();
        _sink = sink;
        client.SamplesReceived += OnSamples;
        client.DeviceFaultReceived += OnDeviceFault;
        client.Disconnected += OnDisconnected;
        try
        {
            var status = await client.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.State == DeviceState.Faulted)
                throw new InvalidOperationException($"device is faulted ({status.Fault?.Code}); reset it first");
            await client.ConfigureAsync(device, ct).ConfigureAwait(false);
            _active = true;
            var start = await client.StartAsync(ct).ConfigureAwait(false);
            _acquisitionId = start.AcquisitionId;
            _log.Info("acquisition_started", ("acquisitionId", start.AcquisitionId));
            return new SourceStartInfo(start.AcquisitionId, Description, device);
        }
        catch
        {
            _active = false;
            Detach();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        bool wasActive = _active;
        _active = false;
        _recoveryCts?.Cancel();
        try { await _recovery.ConfigureAwait(false); } catch { }
        try
        {
            if (wasActive && client.State == ConnectionState.Connected)
            {
                var r = await client.StopAsync(ct).ConfigureAwait(false);
                _log.Info("acquisition_stopped", ("acquisitionId", _acquisitionId), ("samplesPerChannel", r.SamplesSentPerChannel));
            }
        }
        catch (InstrumentException ex)
        {
            _log.Warn("stop_failed", ("acquisitionId", _acquisitionId), ("error", ex.Message));
        }
        finally
        {
            Detach();
        }
    }

    private void Detach()
    {
        client.SamplesReceived -= OnSamples;
        client.DeviceFaultReceived -= OnDeviceFault;
        client.Disconnected -= OnDisconnected;
    }

    private void OnSamples(SampleBatchPayload p)
    {
        if (!_active || _sink is null) return;
        var ts = Stopwatch.GetTimestamp();
        var arr = new Sample[p.Samples.Count];
        for (int i = 0; i < arr.Length; i++)
        {
            var s = p.Samples[i];
            arr[i] = new Sample(s.Channel, s.Sequence, s.ElapsedSeconds, s.Value, s.Quality);
        }
        _sink.OnBatch(new SampleBatch(p.AcquisitionId, arr, ts));
    }

    private void OnDeviceFault(DeviceFaultPayload f)
    {
        _active = false;
        _sink?.OnSourceEvent(new EngineEvent(EngineEventKind.DeviceFault, $"{f.Code}: {f.Message}"));
    }

    private void OnDisconnected(DisconnectInfo info)
    {
        if (info.Kind == DisconnectKind.ClientRequested || !_active) return;
        _active = false;
        _sink?.OnSourceEvent(new EngineEvent(EngineEventKind.SourceInterrupted, $"connection lost ({info.Kind}): {info.Reason}"));
        if (!AutoReconnect) return;
        _recoveryCts = new CancellationTokenSource();
        var token = _recoveryCts.Token;
        _recovery = Task.Run(() => RecoverAsync(token));
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            var status = await client.ReconnectAsync(ct).ConfigureAwait(false);
            var outcome = AcquisitionReconciler.Reconcile(status, _acquisitionId ?? "");
            _log.Info("acquisition_reconciled", ("acquisitionId", _acquisitionId), ("deviceState", status.State.ToString()),
                ("outcome", outcome.ToString()));
            if (outcome == ReconcileOutcome.StillAcquiring)
            {
                _active = true; // same acquisition is provably still running: keep consuming it
            }
            _sink?.OnSourceEvent(new EngineEvent(EngineEventKind.SourceReconciled,
                $"reconnected; device {status.State}; acquisition {outcome}"));
        }
        catch (OperationCanceledException) { }
        catch (InstrumentException ex)
        {
            _sink?.OnSourceEvent(new EngineEvent(EngineEventKind.SourceReconciled, "reconnect failed: " + ex.Message));
        }
    }
}
