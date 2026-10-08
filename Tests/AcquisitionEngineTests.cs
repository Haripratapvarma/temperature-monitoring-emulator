using System.Diagnostics;
using TempLab.Acquisition;
using TempLab.Acquisition.Export;
using TempLab.Acquisition.Recording;
using TempLab.Acquisition.Sources;
using TempLab.Protocol;

namespace TempLab.Tests;

/// <summary>Source driven by the test: pushes exactly the batches it is told to.</summary>
internal sealed class ScriptedSource : IAcquisitionSource
{
    public ISampleSink? Sink { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public string Description => "scripted";

    public Task<SourceStartInfo> StartAsync(AcquisitionSettings settings, ISampleSink sink, CancellationToken ct)
    {
        Sink = sink;
        Starts++;
        return Task.FromResult(new SourceStartInfo("acq-" + Starts, Description, null));
    }

    public Task StopAsync(CancellationToken ct)
    {
        Stops++;
        Sink = null;
        return Task.CompletedTask;
    }

    public void Push(string acq, params Sample[] samples) => Sink!.OnBatch(new SampleBatch(acq, samples, Stopwatch.GetTimestamp()));

    public static Sample[] Ramp(int channel, long from, int count, double start = 25) =>
        Enumerable.Range(0, count).Select(i => new Sample(channel, from + i, (from + i) / 10.0, start + i * 0.1, SampleQuality.Good)).ToArray();
}

public class AcquisitionEngineTests
{
    private static AcquisitionSettings Settings(int window = 5) => new() { WindowSize = window };

    private static async Task<List<ProcessedSample>> DrainAll(AcquisitionEngine e, long expected)
    {
        var all = new List<ProcessedSample>();
        await Wait.UntilAsync(() =>
        {
            e.DrainDisplay(all);
            return all.Count >= expected;
        });
        return all;
    }

    [Fact]
    public async Task Processes_through_native_statistics_in_order()
    {
        await using var engine = new AcquisitionEngine();
        var src = new ScriptedSource();
        await engine.StartAsync(src, Settings(window: 3));
        src.Push("acq-1", new Sample(0, 0, 0, 1, SampleQuality.Good), new Sample(0, 1, .1, 2, SampleQuality.Good),
            new Sample(0, 2, .2, 6, SampleQuality.Good), new Sample(0, 3, .3, 4, SampleQuality.Good));
        var p = await DrainAll(engine, 4);
        Assert.Equal(new[] { 1.0, 1.5, 3.0, 4.0 }, p.Select(x => x.Mean).ToArray());
        Assert.Equal(6.0, p[3].Max, 0.0);
        Assert.True(p[3].IsWarm);
        await engine.StopAsync();
        Assert.Equal(4L, engine.Counters.Processed);
    }

    [Fact]
    public async Task Duplicate_start_is_rejected()
    {
        await using var engine = new AcquisitionEngine();
        await engine.StartAsync(new ScriptedSource(), Settings());
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(new ScriptedSource(), Settings()));
        await engine.StopAsync();
    }

    [Fact]
    public async Task Repeated_start_stop_cycles_work_and_release_everything()
    {
        await using var engine = new AcquisitionEngine();
        var src = new ScriptedSource();
        for (int i = 0; i < 20; i++)
        {
            await engine.StartAsync(src, Settings());
            src.Push("acq-" + src.Starts, ScriptedSource.Ramp(0, 0, 10));
            await Wait.UntilAsync(() => engine.Counters.Processed == 10);
            await engine.StopAsync();
            Assert.Equal(EngineState.Idle, engine.State);
        }
        Assert.Equal(20, src.Starts);
        Assert.Equal(20, src.Stops);
        await engine.StopAsync(); // stop when idle is a no-op
    }

    [Fact]
    public async Task Detects_gaps_out_of_order_and_invalid_samples()
    {
        await using var engine = new AcquisitionEngine();
        var src = new ScriptedSource();
        await engine.StartAsync(src, Settings());
        src.Push("acq-1",
            new Sample(1, 0, 0, 20, SampleQuality.Good),
            new Sample(1, 1, .1, 20, SampleQuality.Good),
            new Sample(1, 5, .5, 20, SampleQuality.Good),     // gap of 3
            new Sample(1, 4, .4, 20, SampleQuality.Good),     // out of order
            new Sample(1, 6, .6, double.NaN, SampleQuality.Invalid),
            new Sample(1, 7, .7, 5000, SampleQuality.Good),   // outside physical range
            new Sample(9, 0, 0, 20, SampleQuality.Good));     // channel not configured
        await Wait.UntilAsync(() => engine.Counters.Processed == 5);
        await engine.StopAsync();
        var c = engine.Counters;
        Assert.Equal(3L, c.GapSamples);
        Assert.Equal(1L, c.OutOfOrder);
        Assert.Equal(4L, c.Invalid); // out-of-order + NaN + 5000 + unknown channel
        var events = new List<EngineEvent>();
        engine.DrainEvents(events);
        Assert.Contains(events, e => e.Kind == EngineEventKind.SequenceGap && e.Count == 3);
        var shown = new List<ProcessedSample>();
        engine.DrainDisplay(shown);
        Assert.False(shown.Single(s => s.Sequence == 6).Valid);
        Assert.Equal(3, shown.Single(s => s.Sequence == 7).WindowCount); // invalid values not in the window
    }

    [Fact]
    public async Task Alerts_from_raw_values_fire_once_per_crossing()
    {
        await using var engine = new AcquisitionEngine();
        var src = new ScriptedSource();
        var settings = Settings() with { Channels = [0], Alerts = new Dictionary<int, ChannelAlertSettings> { [0] = new(null, 30) } };
        await engine.StartAsync(src, settings);
        double[] values = [25, 31, 32, 33, 29, 31, 25];
        src.Push("acq-1", values.Select((v, i) => new Sample(0, i, i / 10.0, v, SampleQuality.Good)).ToArray());
        await Wait.UntilAsync(() => engine.Counters.Processed == values.Length);
        await engine.StopAsync();
        var alerts = new List<AlertEvent>();
        engine.DrainAlerts(alerts);
        Assert.Equal(new long[] { 1, 5 }, alerts.Select(a => a.Sequence).ToArray());
    }

    [Fact]
    public async Task Mean_based_alerts_are_suppressed_during_warm_up()
    {
        await using var engine = new AcquisitionEngine();
        var src = new ScriptedSource();
        var settings = Settings(window: 4) with
        {
            Channels = [0],
            AlertSource = AlertSource.RollingMean,
            Alerts = new Dictionary<int, ChannelAlertSettings> { [0] = new(null, 30) },
        };
        await engine.StartAsync(src, settings);
        src.Push("acq-1", Enumerable.Range(0, 6).Select(i => new Sample(0, i, i, 40, SampleQuality.Good)).ToArray());
        await Wait.UntilAsync(() => engine.Counters.Processed == 6);
        await engine.StopAsync();
        var alerts = new List<AlertEvent>();
        engine.DrainAlerts(alerts);
        Assert.Equal(3L, Assert.Single(alerts).Sequence); // first warm sample
    }

    [Fact]
    public async Task Queue_overflow_drops_newest_and_counts_it()
    {
        await using var engine = new AcquisitionEngine();
        var gate = new SemaphoreSlim(0);
        int inHook = 0;
        engine.BeforeBatchHook = async _ =>
        {
            Interlocked.Increment(ref inHook);
            await gate.WaitAsync();
        };
        var src = new ScriptedSource();
        await engine.StartAsync(src, Settings() with { Channels = [0], QueueCapacityBatches = 2 });
        try
        {
            src.Push("acq-1", ScriptedSource.Ramp(0, 0, 10));
            await Wait.UntilAsync(() => Volatile.Read(ref inHook) == 1); // worker now holds batch 0
            for (int i = 1; i < 10; i++) src.Push("acq-1", ScriptedSource.Ramp(0, i * 10, 10));
            // queue holds batches 1 and 2; batches 3..9 are rejected (drop newest)
            Assert.Equal(100L, engine.Counters.Received);
            Assert.Equal(70L, engine.Counters.Dropped);
        }
        finally
        {
            gate.Release(100);
        }
        await engine.StopAsync();
        var c = engine.Counters;
        Assert.Equal(30L, c.Processed);
        Assert.Equal(c.Received, c.Processed + c.Dropped);
        Assert.Equal(0L, c.GapSamples); // batches 1,2 contiguous with 0; nothing after
    }

    [Fact]
    public async Task Display_buffer_is_bounded()
    {
        await using var engine = new AcquisitionEngine();
        var src = new ScriptedSource();
        await engine.StartAsync(src, Settings() with { Channels = [0], DisplayBufferCapacity = 50 });
        src.Push("acq-1", ScriptedSource.Ramp(0, 0, 500));
        await Wait.UntilAsync(() => engine.Counters.Processed == 500);
        var shown = new List<ProcessedSample>();
        engine.DrainDisplay(shown);
        Assert.Equal(50, shown.Count);
        Assert.Equal(450L, engine.Counters.DisplaySkipped);
        Assert.Equal(499L, shown[^1].Sequence); // newest kept
        await engine.StopAsync();
    }

    [Fact]
    public async Task Batches_after_stop_are_ignored()
    {
        await using var engine = new AcquisitionEngine();
        var src = new ScriptedSource();
        await engine.StartAsync(src, Settings());
        var sink = src.Sink!;
        await engine.StopAsync();
        sink.OnBatch(new SampleBatch("late", ScriptedSource.Ramp(0, 0, 5), Stopwatch.GetTimestamp()));
        Assert.Equal(5L, engine.Counters.IgnoredWhileStopped);
        Assert.Equal(0L, engine.Counters.Processed);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_before_anything_starts()
    {
        await using var engine = new AcquisitionEngine();
        var src = new ScriptedSource();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync(src, Settings(window: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync(src, Settings() with { Channels = [] }));
        Assert.Equal(0, src.Starts);
        Assert.Equal(EngineState.Idle, engine.State);
    }
}

public class ReplayTests
{
    [Fact]
    public async Task Live_session_recorded_then_replayed_reproduces_statistics_exactly()
    {
        using var dir = new TempDir();
        var path = dir.File("live.csv");
        await using var emu = new EmulatorFixture();
        await using var client = emu.CreateClient();
        await client.ConnectAsync();

        var settings = new AcquisitionSettings { RateHz = 200, Seed = 11, WindowSize = 16, RecordingPath = path };
        var live = new List<ProcessedSample>();
        await using (var engine = new AcquisitionEngine { ProcessedObserver = b => { lock (live) live.AddRange(b); } })
        {
            await engine.StartAsync(new EmulatorSource(client), settings);
            await Wait.UntilAsync(() => engine.Counters.Processed >= 4 * 300, 10000);
            await engine.StopAsync();
            Assert.Equal(0L, engine.Counters.Dropped);
        }

        var replayed = new List<ProcessedSample>();
        await using (var engine = new AcquisitionEngine { ProcessedObserver = b => replayed.AddRange(b) })
        {
            var done = new TaskCompletionSource();
            var events = new List<EngineEvent>();
            await engine.StartAsync(new CsvReplaySource(path) { Speed = 0 }, settings with { RecordingPath = null });
            await Wait.UntilAsync(() =>
            {
                engine.DrainEvents(events);
                return events.Any(e => e.Kind == EngineEventKind.SourceCompleted);
            });
            await engine.StopAsync();
        }

        Assert.Equal(live.Count, replayed.Count);
        Assert.Equal(live, replayed); // record equality: raw, mean, min, max, count, warm, alerts — bit for bit
        Assert.Contains(live, p => p.Alerts.HasFlag(AlertFlags.HighRaised)); // ch3 spike at sequence 150
    }

    [Fact]
    public async Task Exporter_writes_processed_rows_through_the_same_pipeline()
    {
        using var dir = new TempDir();
        var rec = dir.File("rec.csv");
        var meta = new SessionMetadata { Settings = new AcquisitionSettings { Channels = [0], WindowSize = 2 } };
        using (var w = new CsvSessionWriter(rec, meta))
        {
            w.Write("a", new Sample(0, 0, 0.0, 25, SampleQuality.Good));
            w.Write("a", new Sample(0, 1, 0.1, 27, SampleQuality.Good));
            w.Write("a", new Sample(0, 2, 0.2, 40, SampleQuality.Good));
        }
        var outPath = dir.File("out.csv");
        var result = await ProcessedExporter.ExportAsync(rec, outPath);
        Assert.Equal(3, result.Rows);
        var lines = File.ReadAllLines(outPath);
        Assert.Equal(ProcessedExporter.Header, lines[1]);
        Assert.StartsWith("0,2,0.2,40,Good,1,33.5,27,40,2,1,", lines[4]);
        Assert.Equal(1, result.Alerts); // 40 > default ch0 high threshold of 30
    }
}
