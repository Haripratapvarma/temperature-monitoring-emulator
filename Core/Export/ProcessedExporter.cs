using System.Globalization;
using System.Text;
using TempLab.Acquisition.Recording;
using TempLab.Acquisition.Sources;

namespace TempLab.Acquisition.Export;

public sealed record ExportResult(int Rows, EngineCounters Counters, int Alerts);

/// <summary>
/// Exports processed results for a recorded session. It replays the file through a real
/// <see cref="AcquisitionEngine"/> (same queue, same native processing, same alert rules) rather than a
/// separate code path, so exported numbers are exactly what the live pipeline computes.
/// </summary>
public static class ProcessedExporter
{
    public const string Header =
        "channel_id,sequence,elapsed_s,temperature_c,quality,valid,rolling_mean,rolling_min,rolling_max,window_count,is_warm,alert";

    public static async Task<ExportResult> ExportAsync(string recordedCsv, string outputCsv,
        AcquisitionSettings? settingsOverride = null, CancellationToken ct = default)
    {
        var session = CsvSessionReader.Read(recordedCsv);
        var settings = (settingsOverride ?? session.Metadata.Settings) with
        {
            RecordingPath = null,
            QueueCapacityBatches = 1_000_000, // offline: never drop
        };
        var inv = CultureInfo.InvariantCulture;
        int rows = 0, alerts = 0;
        await using var engine = new AcquisitionEngine();
        using var writer = new StreamWriter(outputCsv, false, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine($"# exported_from={Path.GetFileName(recordedCsv)} session_id={session.Metadata.SessionId:D} window={settings.WindowSize}");
        writer.WriteLine(Header);
        var line = new StringBuilder(128);
        engine.ProcessedObserver = batch =>
        {
            foreach (var p in batch)
            {
                line.Clear();
                line.Append(p.Channel.ToString(inv)).Append(',')
                    .Append(p.Sequence.ToString(inv)).Append(',')
                    .Append(p.ElapsedSeconds.ToString("R", inv)).Append(',')
                    .Append(p.Raw.ToString("R", inv)).Append(',')
                    .Append(p.Quality).Append(',')
                    .Append(p.Valid ? '1' : '0').Append(',')
                    .Append(p.Mean.ToString("R", inv)).Append(',')
                    .Append(p.Min.ToString("R", inv)).Append(',')
                    .Append(p.Max.ToString("R", inv)).Append(',')
                    .Append(p.WindowCount.ToString(inv)).Append(',')
                    .Append(p.IsWarm ? '1' : '0').Append(',')
                    .Append(p.Alerts == AlertFlags.None ? "" : p.Alerts.ToString().Replace(", ", "|"));
                writer.WriteLine(line);
                rows++;
                if (p.Alerts != AlertFlags.None) alerts++;
            }
        };

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new CsvReplaySource(recordedCsv) { Speed = 0, SamplesPerBatch = 512 };
        var sink = new CompletionWatcher(engine, done);
        await engine.StartAsync(new SinkTap(source, sink), settings, ct).ConfigureAwait(false);
        await done.Task.WaitAsync(ct).ConfigureAwait(false);
        await engine.StopAsync(ct).ConfigureAwait(false);
        return new ExportResult(rows, engine.Counters, alerts);
    }

    /// <summary>Wraps the source so the exporter learns when replay has finished.</summary>
    private sealed class SinkTap(IAcquisitionSource inner, CompletionWatcher watcher) : IAcquisitionSource
    {
        public string Description => inner.Description;
        public Task<SourceStartInfo> StartAsync(AcquisitionSettings settings, ISampleSink sink, CancellationToken ct)
        {
            watcher.Inner = sink;
            return inner.StartAsync(settings, watcher, ct);
        }
        public Task StopAsync(CancellationToken ct) => inner.StopAsync(ct);
    }

    private sealed class CompletionWatcher(AcquisitionEngine engine, TaskCompletionSource done) : ISampleSink
    {
        public ISampleSink Inner { get; set; } = engine;
        public void OnBatch(SampleBatch batch) => Inner.OnBatch(batch);
        public void OnSourceEvent(EngineEvent evt)
        {
            Inner.OnSourceEvent(evt);
            if (evt.Kind == EngineEventKind.SourceCompleted) done.TrySetResult();
        }
    }
}
