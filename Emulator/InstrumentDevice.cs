using System.Diagnostics;
using System.Text;
using TempLab.Protocol;

namespace TempLab.Emulator;

/// <summary>Transport the device writes to. Implemented by <see cref="ClientSession"/> (and fakes in tests).</summary>
public interface IDeviceOutput
{
    /// <summary>Queue a sample frame; false when the outbound sample buffer is full.</summary>
    bool TrySendSamples(Envelope envelope);
    void SendEvent(Envelope envelope);
    void SendRawFrame(byte[] frame);
    void Abort(string reason);
}

public sealed record CommandResult(Envelope Response, TimeSpan Delay);

/// <summary>
/// The emulated instrument: device state machine, signal generation and fault injection.
/// <para>Threading: commands are serialised by <see cref="_commandLock"/> (one command at a time).
/// The generator task never takes that lock; it only takes <see cref="_stateGate"/> briefly, so
/// <c>stop</c> can cancel and await the generator while holding the command lock without deadlock.</para>
/// </summary>
public sealed class InstrumentDevice
{
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly object _stateGate = new();
    private readonly StructuredLogger _log;
    private readonly FaultInjector _faults = new();

    private DeviceState _state = DeviceState.Idle;
    private DeviceConfiguration _config = new();
    private string? _acquisitionId;
    private ErrorInfo? _fault;
    private long _samplesSent;
    private CancellationTokenSource? _acqCts;
    private Task _generator = Task.CompletedTask;

    public InstrumentDevice(StructuredLogger log, FaultScenario? scenario = null)
    {
        _log = log;
        if (scenario is not null) _faults.Load(scenario);
    }

    /// <summary>Target interval between sample frames.</summary>
    public TimeSpan BatchInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Upper bound per frame so a frame stays well under the 1 MiB limit.</summary>
    public int MaxSamplesPerFrame { get; init; } = 4000;

    public DeviceState State
    {
        get { lock (_stateGate) return _state; }
    }

    public DeviceStatus GetStatus()
    {
        lock (_stateGate)
        {
            var sc = _faults.Scenario;
            return new DeviceStatus
            {
                State = _state,
                AcquisitionId = _acquisitionId,
                SamplesSentPerChannel = Interlocked.Read(ref _samplesSent),
                Configuration = _config,
                Fault = _fault,
                Scenario = new FaultScenarioStatus(sc.Name, sc.Faults.Count),
            };
        }
    }

    public async Task<CommandResult> ExecuteAsync(Envelope request, IDeviceOutput output, CancellationToken ct = default)
    {
        string? id = request.Id;
        if (request.Version != ProtocolInfo.Version)
            return Done(request, ProtocolJson.Error(id, ErrorCodes.UnsupportedVersion, $"protocol version {request.Version} not supported"));

        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = State;
            Envelope response;
            try
            {
                response = request.Type switch
                {
                    MessageTypes.Configure => Configure(request),
                    MessageTypes.Start => Start(id, output),
                    MessageTypes.Stop => await StopAsync(id).ConfigureAwait(false),
                    MessageTypes.Status => ProtocolJson.Ok(id, GetStatus()),
                    MessageTypes.Reset => await ResetAsync(id).ConfigureAwait(false),
                    MessageTypes.SetFaults => SetFaults(request),
                    _ => ProtocolJson.Error(id, ErrorCodes.UnknownCommand, $"unknown command '{request.Type}'"),
                };
            }
            catch (System.Text.Json.JsonException ex)
            {
                response = ProtocolJson.Error(id, ErrorCodes.BadRequest, "payload could not be decoded: " + ex.Message);
            }

            var payload = ProtocolJson.FromElement<ResponsePayload>(response.Payload);
            _log.Info("command",
                ("requestId", id), ("command", request.Type), ("ok", payload?.Ok),
                ("error", payload?.Error?.Code), ("stateBefore", before.ToString()), ("stateAfter", State.ToString()),
                ("acquisitionId", GetStatus().AcquisitionId));
            return Done(request, response);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private CommandResult Done(Envelope request, Envelope response) =>
        new(response, _faults.ReplyDelayFor(request.Type));

    private Envelope Configure(Envelope request)
    {
        if (State != DeviceState.Idle)
            return ProtocolJson.Error(request.Id, ErrorCodes.InvalidState, $"configure not allowed in {State}");
        var cfg = ProtocolJson.FromElement<DeviceConfiguration>(request.Payload);
        if (cfg is null) return ProtocolJson.Error(request.Id, ErrorCodes.BadRequest, "configuration payload required");
        var problem = cfg.Validate();
        if (problem is not null) return ProtocolJson.Error(request.Id, ErrorCodes.InvalidConfiguration, problem);
        lock (_stateGate) _config = cfg;
        _log.Info("configured", ("requestId", request.Id), ("channels", cfg.Channels.Count), ("rateHz", cfg.RateHz), ("seed", cfg.Seed));
        return ProtocolJson.Ok(request.Id);
    }

    private Envelope SetFaults(Envelope request)
    {
        if (State != DeviceState.Idle)
            return ProtocolJson.Error(request.Id, ErrorCodes.InvalidState, $"setFaults not allowed in {State}");
        var sc = ProtocolJson.FromElement<FaultScenario>(request.Payload) ?? new FaultScenario();
        var problem = sc.Validate();
        if (problem is not null) return ProtocolJson.Error(request.Id, ErrorCodes.InvalidConfiguration, problem);
        _faults.Load(sc);
        _log.Info("fault_scenario_loaded", ("requestId", request.Id), ("scenario", sc.Name), ("faults", sc.Faults.Count));
        return ProtocolJson.Ok(request.Id);
    }

    private Envelope Start(string? id, IDeviceOutput output)
    {
        DeviceConfiguration cfg;
        string acqId;
        CancellationTokenSource cts;
        lock (_stateGate)
        {
            if (_state != DeviceState.Idle)
                return ProtocolJson.Error(id, ErrorCodes.InvalidState, $"start not allowed in {_state}");
            cfg = _config;
            acqId = Guid.NewGuid().ToString("N");
            _acquisitionId = acqId;
            _state = DeviceState.Acquiring;
            _fault = null;
            Interlocked.Exchange(ref _samplesSent, 0);
            cts = _acqCts = new CancellationTokenSource();
        }
        _faults.ArmForAcquisition();
        var sc = _faults.Scenario;
        _log.Info("state_transition", ("requestId", id), ("from", "Idle"), ("to", "Acquiring"), ("acquisitionId", acqId),
            ("scenario", sc.Name), ("scenarioSeed", sc.Seed), ("faults", sc.Faults.Count), ("seed", cfg.Seed));
        _generator = Task.Run(() => GenerateAsync(cfg, acqId, output, cts.Token));
        return ProtocolJson.Ok(id, new StartResult(acqId));
    }

    private async Task<Envelope> StopAsync(string? id)
    {
        bool was = await StopGeneratorAsync("stop", id).ConfigureAwait(false);
        return ProtocolJson.Ok(id, new StopResult(was, Interlocked.Read(ref _samplesSent)));
    }

    private async Task<Envelope> ResetAsync(string? id)
    {
        await StopGeneratorAsync("reset", id).ConfigureAwait(false);
        lock (_stateGate)
        {
            if (_state != DeviceState.Idle)
                _log.Info("state_transition", ("requestId", id), ("from", _state.ToString()), ("to", "Idle"), ("reason", "reset"));
            _state = DeviceState.Idle;
            _fault = null;
        }
        return ProtocolJson.Ok(id);
    }

    /// <summary>Cancels and awaits the generator. Returns true if an acquisition was running.</summary>
    private async Task<bool> StopGeneratorAsync(string reason, string? requestId)
    {
        CancellationTokenSource? cts;
        bool wasAcquiring;
        lock (_stateGate)
        {
            wasAcquiring = _state == DeviceState.Acquiring;
            cts = _acqCts;
            _acqCts = null;
        }
        cts?.Cancel();
        try
        {
            await _generator.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error("generator_failed", ("error", ex.Message));
        }
        cts?.Dispose();
        lock (_stateGate)
        {
            if (_state == DeviceState.Acquiring)
            {
                _state = DeviceState.Idle;
                _log.Info("state_transition", ("requestId", requestId), ("from", "Acquiring"), ("to", "Idle"),
                    ("reason", reason), ("acquisitionId", _acquisitionId), ("samplesSent", Interlocked.Read(ref _samplesSent)));
            }
        }
        return wasAcquiring;
    }

    /// <summary>Called by the session when the transport closes. Never resumes the acquisition.</summary>
    public async Task OnClientDisconnectedAsync(string reason)
    {
        await _commandLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string? acq;
            bool acquiring;
            lock (_stateGate)
            {
                acquiring = _state == DeviceState.Acquiring;
                acq = _acquisitionId;
            }
            await StopGeneratorAsync("client_disconnected", null).ConfigureAwait(false);
            if (acquiring) _log.Warn("acquisition_abandoned", ("acquisitionId", acq), ("reason", reason));
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private void EnterFaulted(string acqId, string code, string message, IDeviceOutput output)
    {
        lock (_stateGate)
        {
            if (_state != DeviceState.Acquiring || _acquisitionId != acqId) return;
            _state = DeviceState.Faulted;
            _fault = new ErrorInfo(code, message);
            // Queued (non-blocking) inside the gate so anyone who observes Faulted also finds the event queued.
            output.SendEvent(ProtocolJson.Event(MessageTypes.DeviceFault, new DeviceFaultPayload(acqId, code, message)));
        }
        _log.Error("state_transition", ("from", "Acquiring"), ("to", "Faulted"), ("acquisitionId", acqId), ("code", code), ("message", message));
    }

    private async Task GenerateAsync(DeviceConfiguration cfg, string acqId, IDeviceOutput output, CancellationToken ct)
    {
        var gen = new SignalGenerator(cfg);
        var sw = Stopwatch.StartNew();
        long seq = 0; // next sequence to emit, per channel
        var batch = new List<SampleDto>(256);

        bool Flush()
        {
            if (batch.Count == 0) return true;
            var env = ProtocolJson.Event(MessageTypes.Samples, new SampleBatchPayload { AcquisitionId = acqId, Samples = batch.ToArray() });
            batch.Clear();
            if (output.TrySendSamples(env)) return true;
            EnterFaulted(acqId, "BufferOverflow", "outbound sample buffer full: consumer too slow", output);
            return false;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Samples due so far: index 0 is due at t = 0. Values depend only on seq, never on timing.
                long due = (long)Math.Floor(sw.Elapsed.TotalSeconds * cfg.RateHz) + 1;
                int perChannelLimit = Math.Max(1, MaxSamplesPerFrame / gen.Channels.Count);
                long frameEnd = Math.Min(due, seq + perChannelLimit);
                while (seq < frameEnd)
                {
                    foreach (var fault in _faults.TakeTriggered(seq))
                    {
                        switch (fault.Kind)
                        {
                            case FaultKind.SkipSequences:
                                _log.Warn("fault_injected", ("kind", "SkipSequences"), ("acquisitionId", acqId), ("at", seq), ("count", fault.Count));
                                seq += fault.Count;
                                break;
                            case FaultKind.AcquisitionFault:
                                if (!Flush()) return;
                                _log.Warn("fault_injected", ("kind", "AcquisitionFault"), ("acquisitionId", acqId), ("at", seq));
                                EnterFaulted(acqId, fault.Code ?? "SensorFault", $"injected acquisition fault at sequence {seq}", output);
                                return;
                            case FaultKind.Disconnect:
                                Flush();
                                _log.Warn("fault_injected", ("kind", "Disconnect"), ("acquisitionId", acqId), ("at", seq));
                                output.Abort("injected disconnect");
                                return;
                            case FaultKind.MalformedFrame:
                                if (!Flush()) return;
                                _log.Warn("fault_injected", ("kind", "MalformedFrame"), ("acquisitionId", acqId), ("at", seq));
                                output.SendRawFrame(FrameCodec.Encode(Encoding.UTF8.GetBytes("{this is not json")));
                                break;
                            case FaultKind.OversizedFrame:
                                Flush();
                                _log.Warn("fault_injected", ("kind", "OversizedFrame"), ("acquisitionId", acqId), ("at", seq));
                                var header = new byte[4];
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 64u * 1024 * 1024);
                                output.SendRawFrame(header);
                                output.Abort("injected oversized frame");
                                return;
                        }
                    }

                    double t = seq / cfg.RateHz;
                    foreach (var ch in gen.Channels)
                    {
                        double v = ch.ValueAt(seq);
                        var q = SampleQuality.Good;
                        if (_faults.IsInvalidReading(ch.Channel, seq))
                        {
                            v = double.NaN;
                            q = SampleQuality.Invalid;
                        }
                        batch.Add(new SampleDto { Channel = ch.Channel, Sequence = seq, ElapsedSeconds = t, Value = v, Quality = q });
                    }
                    seq++;
                    Interlocked.Exchange(ref _samplesSent, seq);
                }
                if (!Flush()) return;
                if (seq < due) continue; // catching up after a stall: send the next frame immediately
                await Task.Delay(BatchInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error("generator_exception", ("acquisitionId", acqId), ("error", ex.ToString()));
            EnterFaulted(acqId, ErrorCodes.Internal, ex.Message, output);
        }
    }
}
