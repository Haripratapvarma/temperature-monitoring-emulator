using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using TempLab.Acquisition.Processing;
using TempLab.Acquisition.Recording;
using TempLab.Acquisition.Sources;
using TempLab.Protocol;

namespace TempLab.Acquisition;

/// <summary>
/// Acquisition pipeline.
/// <code>
///  source thread ──OnBatch──▶ bounded queue (drop-newest when full, counted) ──▶ processing worker
///                                                                              ├─ gap / invalid checks
///                                                                              ├─ CSV recorder (raw samples)
///                                                                              ├─ native rolling stats
///                                                                              ├─ alert evaluation
///                                                                              └─ display buffer ──▶ UI tick drains
/// </code>
/// Thread ownership: the processing worker is the only thread touching the processor, alert evaluators,
/// sequence tracking and the recorder. The UI never touches them; it drains the display/alert/event buffers
/// (lock-protected) on its own timer. Lifecycle methods are serialised by <see cref="_lifecycle"/>.
/// </summary>
public sealed class AcquisitionEngine : ISampleSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly StructuredLogger _log;
    private readonly object _displayGate = new();
    private readonly Queue<ProcessedSample> _display = new();
    private readonly Queue<AlertEvent> _alerts = new();
    private readonly Queue<EngineEvent> _events = new();
    private readonly ConcurrentQueue<EngineEvent> _sourceEvents = new();

    /// <summary>Sentinel queued after a source event so the worker publishes it even if no data follows.</summary>
    private static readonly SampleBatch Wakeup = new("", [], 0);

    private volatile EngineState _state = EngineState.Idle;
    private Channel<SampleBatch>? _queue;
    private Task _worker = Task.CompletedTask;
    private IAcquisitionSource? _source;
    private AcquisitionSettings _settings = new();
    private CsvSessionWriter? _recorder;
    private IRollingStatsProcessor? _processor;

    private long _received, _processed, _dropped, _gaps, _invalid, _outOfOrder, _alertCount, _displaySkipped, _ignored;
    private long _latencyCount;
    private double _latencySumMs, _latencyMaxMs;
    private readonly object _latencyGate = new();

    public AcquisitionEngine(StructuredLogger? log = null) => _log = log ?? StructuredLogger.Null;

    public EngineState State => _state;
    public Guid SessionId { get; private set; }
    public string? AcquisitionId { get; private set; }
    public string? SourceDescription { get; private set; }
    public AcquisitionSettings Settings => _settings;
    public string? RecordingPath => _recorder?.Path;

    /// <summary>Optional observer of per-batch processing latency (ms); called on the worker thread.</summary>
    public Action<double>? LatencyObserver { get; set; }

    /// <summary>
    /// Optional observer of every processed batch, called on the worker thread in processing order.
    /// Used by the exporter and tests; must be fast.
    /// </summary>
    public Action<IReadOnlyList<ProcessedSample>>? ProcessedObserver { get; set; }

    /// <summary>Test hook: runs on the worker before each batch (used to simulate a slow consumer).</summary>
    internal Func<CancellationToken, ValueTask>? BeforeBatchHook { get; set; }

    public EngineCounters Counters
    {
        get
        {
            double max, mean;
            lock (_latencyGate)
            {
                max = _latencyMaxMs;
                mean = _latencyCount == 0 ? 0 : _latencySumMs / _latencyCount;
            }
            return new EngineCounters(
                Interlocked.Read(ref _received), Interlocked.Read(ref _processed), Interlocked.Read(ref _dropped),
                Interlocked.Read(ref _gaps), Interlocked.Read(ref _invalid), Interlocked.Read(ref _outOfOrder),
                Interlocked.Read(ref _alertCount), Interlocked.Read(ref _displaySkipped), Interlocked.Read(ref _ignored),
                _queue?.Reader.CanCount == true ? _queue.Reader.Count : 0, max, mean);
        }
    }

    public async Task StartAsync(IAcquisitionSource source, AcquisitionSettings settings, CancellationToken ct = default)
    {
        var problem = settings.Validate();
        if (problem is not null) throw new ArgumentException(problem, nameof(settings));

        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state != EngineState.Idle)
                throw new InvalidOperationException($"acquisition already {_state}");
            _state = EngineState.Starting;
            ResetCounters();
            _settings = settings;
            SessionId = Guid.NewGuid();
            AcquisitionId = null;
            SourceDescription = source.Description;

            try
            {
                _processor = RollingStatsProcessorFactory.Create(settings.Processor, settings.ProcessorChannelCount, settings.WindowSize);
                if (settings.RecordingPath is not null)
                {
                    _recorder = new CsvSessionWriter(settings.RecordingPath, new SessionMetadata
                    {
                        SessionId = SessionId,
                        Source = source.Description,
                        Settings = settings,
                        Device = source is CsvReplaySource ? null : settings.ToDeviceConfiguration(),
                    });
                }
                _queue = Channel.CreateBounded<SampleBatch>(new BoundedChannelOptions(settings.QueueCapacityBatches)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait, // with TryWrite this means "reject newest"
                });
                var state = new WorkerState(settings, _processor, _recorder);
                var reader = _queue.Reader;
                _worker = Task.Run(() => WorkerLoopAsync(reader, state));
                _source = source;
                _state = EngineState.Running; // accept batches before the device starts streaming

                var info = await source.StartAsync(settings, this, ct).ConfigureAwait(false);
                AcquisitionId = info.AcquisitionId;
                _log.Info("engine_started", ("sessionId", SessionId), ("acquisitionId", info.AcquisitionId),
                    ("source", source.Description), ("recording", settings.RecordingPath), ("window", settings.WindowSize));
            }
            catch
            {
                await TearDownAsync(stopSource: false).ConfigureAwait(false);
                _state = EngineState.Idle;
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops the source, drains the queue, awaits the worker and releases file/native resources.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state is EngineState.Idle) return;
            _state = EngineState.Stopping;
            await TearDownAsync(stopSource: true).ConfigureAwait(false);
            _state = EngineState.Idle;
            _log.Info("engine_stopped", ("sessionId", SessionId), ("acquisitionId", AcquisitionId),
                ("received", Interlocked.Read(ref _received)), ("processed", Interlocked.Read(ref _processed)),
                ("dropped", Interlocked.Read(ref _dropped)), ("gaps", Interlocked.Read(ref _gaps)));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task TearDownAsync(bool stopSource)
    {
        var source = _source;
        _source = null;
        if (stopSource && source is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await source.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("source_stop_failed", ("error", ex.Message));
            }
        }
        _queue?.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("worker_failed", ("error", ex.ToString()));
        }
        _worker = Task.CompletedTask;
        _recorder?.Dispose();
        _recorder = null;
        _processor?.Dispose();
        _processor = null;
    }

    // ---- ISampleSink: called on the source thread. Never blocks. ----

    public void OnBatch(SampleBatch batch)
    {
        var q = _queue;
        if (_state != EngineState.Running || q is null)
        {
            Interlocked.Add(ref _ignored, batch.Samples.Length);
            return;
        }
        Interlocked.Add(ref _received, batch.Samples.Length);
        if (!q.Writer.TryWrite(batch))
        {
            long total = Interlocked.Add(ref _dropped, batch.Samples.Length);
            if (total == batch.Samples.Length) _log.Warn("queue_overflow", ("sessionId", SessionId), ("policy", "drop-newest"));
        }
    }

    public void OnSourceEvent(EngineEvent evt)
    {
        _sourceEvents.Enqueue(evt);
        _queue?.Writer.TryWrite(Wakeup); // if the queue is full the worker is busy and will drain events anyway
        _log.Warn("source_event", ("kind", evt.Kind.ToString()), ("message", evt.Message), ("sessionId", SessionId),
            ("acquisitionId", AcquisitionId));
    }

    // ---- UI side: drain buffers. Thread-safe. ----

    public int DrainDisplay(List<ProcessedSample> into, int max = int.MaxValue)
    {
        lock (_displayGate)
        {
            int n = 0;
            while (n < max && _display.TryDequeue(out var s))
            {
                into.Add(s);
                n++;
            }
            return n;
        }
    }

    public int DrainAlerts(List<AlertEvent> into)
    {
        lock (_displayGate)
        {
            int n = _alerts.Count;
            while (_alerts.TryDequeue(out var a)) into.Add(a);
            return n;
        }
    }

    public int DrainEvents(List<EngineEvent> into)
    {
        lock (_displayGate)
        {
            int n = _events.Count;
            while (_events.TryDequeue(out var e)) into.Add(e);
            return n;
        }
    }

    // ---- Worker ----

    private sealed class WorkerState(AcquisitionSettings settings, IRollingStatsProcessor processor, CsvSessionWriter? recorder)
    {
        public AcquisitionSettings Settings { get; } = settings;
        public IRollingStatsProcessor Processor { get; } = processor;
        public CsvSessionWriter? Recorder { get; } = recorder;
        public Dictionary<int, long> NextSequence { get; } = new();
        public Dictionary<int, AlertEvaluator> Alerts { get; } = settings.Channels.ToDictionary(
            c => c,
            c => new AlertEvaluator(settings.Alerts.TryGetValue(c, out var a) ? a : new ChannelAlertSettings(null, null), settings.Hysteresis));
        public string? CurrentAcquisition;
        public int[] Channels = new int[256];
        public double[] Values = new double[256];
        public RollingStats[] Results = new RollingStats[256];
        public bool[] Accepted = new bool[256];
    }

    private async Task WorkerLoopAsync(ChannelReader<SampleBatch> reader, WorkerState st)
    {
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    DrainSourceEvents(st);
                    if (ReferenceEquals(batch, Wakeup)) continue;
                    if (BeforeBatchHook is { } hook) await hook(CancellationToken.None).ConfigureAwait(false);
                    ProcessBatch(batch, st);
                }
            }
            DrainSourceEvents(st);
        }
        catch (Exception ex)
        {
            _state = EngineState.Faulted;
            Publish(new EngineEvent(EngineEventKind.WorkerFailed, ex.Message));
            _log.Error("worker_exception", ("sessionId", SessionId), ("error", ex.ToString()));
            throw;
        }
    }

    private void DrainSourceEvents(WorkerState st)
    {
        while (_sourceEvents.TryDequeue(out var e))
        {
            st.Recorder?.WriteEvent(e.Kind.ToString(), ("message", e.Message));
            if (e.Kind == EngineEventKind.SourceInterrupted) st.NextSequence.Clear();
            Publish(e);
        }
    }

    private void Publish(EngineEvent e)
    {
        lock (_displayGate)
        {
            _events.Enqueue(e);
            while (_events.Count > 1000) _events.Dequeue();
        }
    }

    private void ProcessBatch(SampleBatch batch, WorkerState st)
    {
        var samples = batch.Samples;
        int n = samples.Length;
        if (st.Channels.Length < n)
        {
            int size = Math.Max(n, st.Channels.Length * 2);
            st.Channels = new int[size];
            st.Values = new double[size];
            st.Results = new RollingStats[size];
            st.Accepted = new bool[size];
        }

        if (st.CurrentAcquisition != batch.AcquisitionId)
        {
            st.CurrentAcquisition = batch.AcquisitionId;
            st.NextSequence.Clear(); // sequences restart with every acquisition
        }

        int m = 0; // samples forwarded to the processor
        for (int i = 0; i < n; i++)
        {
            ref readonly var s = ref samples[i];
            st.Accepted[i] = false;
            if ((uint)s.Channel >= (uint)st.Processor.ChannelCount || !st.Alerts.ContainsKey(s.Channel))
            {
                Interlocked.Increment(ref _invalid);
                continue;
            }
            if (st.NextSequence.TryGetValue(s.Channel, out long expected))
            {
                if (s.Sequence < expected)
                {
                    Interlocked.Increment(ref _outOfOrder);
                    Interlocked.Increment(ref _invalid);
                    Publish(new EngineEvent(EngineEventKind.OutOfOrder, $"channel {s.Channel}: sequence {s.Sequence} < expected {expected}",
                        s.Channel, s.Sequence));
                    continue;
                }
                if (s.Sequence > expected)
                {
                    long missing = s.Sequence - expected;
                    Interlocked.Add(ref _gaps, missing);
                    st.Recorder?.WriteEvent("gap", ("channel", s.Channel), ("expected", expected), ("got", s.Sequence), ("missing", missing));
                    Publish(new EngineEvent(EngineEventKind.SequenceGap, $"channel {s.Channel}: {missing} samples missing before {s.Sequence}",
                        s.Channel, expected, missing));
                }
            }
            st.NextSequence[s.Channel] = s.Sequence + 1;
            st.Recorder?.Write(batch.AcquisitionId, s);
            st.Channels[m] = s.Channel;
            st.Values[m] = s.Quality == SampleQuality.Invalid ? double.NaN : s.Value;
            st.Accepted[i] = true;
            m++;
        }

        st.Processor.PushBatch(st.Channels.AsSpan(0, m), st.Values.AsSpan(0, m), st.Results.AsSpan(0, m));

        int k = 0;
        long processed = 0, invalid = 0, alerts = 0;
        var displayChunk = new ProcessedSample[m];
        List<AlertEvent>? newAlerts = null;
        for (int i = 0; i < n; i++)
        {
            if (!st.Accepted[i]) continue;
            ref readonly var s = ref samples[i];
            var r = st.Results[k];
            bool valid = !r.Rejected;
            if (!valid) invalid++;
            var flags = AlertFlags.None;
            if (valid)
            {
                double basis = st.Settings.AlertSource == AlertSource.Raw ? s.Value : (r.IsWarm ? r.Mean : double.NaN);
                flags = st.Alerts[s.Channel].Evaluate(basis);
                if (flags != AlertFlags.None)
                {
                    var thresholds = st.Settings.Alerts.GetValueOrDefault(s.Channel);
                    newAlerts ??= [];
                    if (flags.HasFlag(AlertFlags.HighRaised))
                        newAlerts.Add(new AlertEvent(s.Channel, AlertKind.High, basis, thresholds?.High ?? double.NaN, s.Sequence, s.ElapsedSeconds, DateTime.UtcNow));
                    if (flags.HasFlag(AlertFlags.LowRaised))
                        newAlerts.Add(new AlertEvent(s.Channel, AlertKind.Low, basis, thresholds?.Low ?? double.NaN, s.Sequence, s.ElapsedSeconds, DateTime.UtcNow));
                }
            }
            displayChunk[k] = new ProcessedSample(s.Channel, s.Sequence, s.ElapsedSeconds, s.Value, s.Quality, valid,
                r.Mean, r.Min, r.Max, r.Count, r.IsWarm, flags);
            k++;
            processed++;
        }
        if (newAlerts is not null)
        {
            alerts = newAlerts.Count;
            foreach (var a in newAlerts)
                _log.Info("alert", ("sessionId", SessionId), ("acquisitionId", batch.AcquisitionId), ("channel", a.Channel),
                    ("kind", a.Kind.ToString()), ("value", a.Value), ("threshold", a.Threshold), ("sequence", a.Sequence));
        }

        ProcessedObserver?.Invoke(displayChunk);
        lock (_displayGate)
        {
            foreach (var p in displayChunk) _display.Enqueue(p);
            int cap = st.Settings.DisplayBufferCapacity;
            while (_display.Count > cap)
            {
                _display.Dequeue();
                _displaySkipped++;
            }
            if (newAlerts is not null)
            {
                foreach (var a in newAlerts) _alerts.Enqueue(a);
                while (_alerts.Count > 1000) _alerts.Dequeue();
            }
        }
        Interlocked.Add(ref _processed, processed);
        Interlocked.Add(ref _invalid, invalid);
        Interlocked.Add(ref _alertCount, alerts);

        double latencyMs = Stopwatch.GetElapsedTime(batch.ReceivedTimestamp).TotalMilliseconds;
        lock (_latencyGate)
        {
            _latencyCount++;
            _latencySumMs += latencyMs;
            if (latencyMs > _latencyMaxMs) _latencyMaxMs = latencyMs;
        }
        LatencyObserver?.Invoke(latencyMs);
    }

    private void ResetCounters()
    {
        _received = _processed = _dropped = _gaps = _invalid = _outOfOrder = _alertCount = _displaySkipped = _ignored = 0;
        lock (_latencyGate)
        {
            _latencyCount = 0;
            _latencySumMs = 0;
            _latencyMaxMs = 0;
        }
        lock (_displayGate)
        {
            _display.Clear();
            _alerts.Clear();
            _events.Clear();
        }
        while (_sourceEvents.TryDequeue(out _)) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
