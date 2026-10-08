using System.Diagnostics;
using TempLab.Acquisition.Recording;

namespace TempLab.Acquisition.Sources;

/// <summary>
/// Replays a recorded session through the same sink interface as live data.
/// <see cref="Speed"/> = 0 replays as fast as possible; 1 = original timing (by recorded elapsed time).
/// </summary>
public sealed class CsvReplaySource(string path) : IAcquisitionSource
{
    private CancellationTokenSource? _cts;
    private Task _pump = Task.CompletedTask;

    public double Speed { get; init; } = 1.0;
    public int SamplesPerBatch { get; init; } = 40;
    public string Path { get; } = path;
    public string Description => $"replay {System.IO.Path.GetFileName(Path)}";

    public RecordedSession? Session { get; private set; }

    public Task<SourceStartInfo> StartAsync(AcquisitionSettings settings, ISampleSink sink, CancellationToken ct)
    {
        var session = CsvSessionReader.Read(Path);
        Session = session;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _pump = Task.Run(() => PumpAsync(session, sink, token));
        var acq = session.Rows.Count > 0 ? session.Rows[0].AcquisitionId : "replay";
        return Task.FromResult(new SourceStartInfo(acq, Description, session.Metadata.Device));
    }

    private async Task PumpAsync(RecordedSession session, ISampleSink sink, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rows = session.Rows;
        int i = 0;
        string? previousAcq = null;
        try
        {
            while (i < rows.Count && !ct.IsCancellationRequested)
            {
                string acq = rows[i].AcquisitionId;
                if (acq != previousAcq) sw.Restart(); // elapsed time restarts with each recorded acquisition
                previousAcq = acq;
                int start = i;
                while (i < rows.Count && i - start < SamplesPerBatch && rows[i].AcquisitionId == acq) i++;
                var samples = new Sample[i - start];
                for (int k = 0; k < samples.Length; k++) samples[k] = rows[start + k].Sample;

                if (Speed > 0)
                {
                    double target = samples[^1].ElapsedSeconds / Speed;
                    var wait = TimeSpan.FromSeconds(target) - sw.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                sink.OnBatch(new SampleBatch(acq, samples, Stopwatch.GetTimestamp()));
            }
            if (!ct.IsCancellationRequested)
                sink.OnSourceEvent(new EngineEvent(EngineEventKind.SourceCompleted, $"replay finished ({rows.Count} samples)"));
        }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts?.Dispose();
        _cts = null;
    }
}
