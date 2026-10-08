using TempLab.Protocol;

namespace TempLab.Acquisition;

public readonly record struct Sample(int Channel, long Sequence, double ElapsedSeconds, double Value, SampleQuality Quality);

/// <summary>A batch as handed from a source to the engine. <see cref="ReceivedTimestamp"/> is Stopwatch ticks.</summary>
public sealed record SampleBatch(string AcquisitionId, Sample[] Samples, long ReceivedTimestamp);

public readonly record struct RollingStats(double Mean, double Min, double Max, int Count, int WindowSize, bool IsWarm, bool Rejected);

[Flags]
public enum AlertFlags
{
    None = 0,
    HighRaised = 1,
    LowRaised = 2,
}

/// <summary>One sample after processing: raw value plus rolling statistics for its channel.</summary>
public readonly record struct ProcessedSample(
    int Channel,
    long Sequence,
    double ElapsedSeconds,
    double Raw,
    SampleQuality Quality,
    bool Valid,
    double Mean,
    double Min,
    double Max,
    int WindowCount,
    bool IsWarm,
    AlertFlags Alerts);

public enum AlertKind
{
    High,
    Low,
}

public sealed record AlertEvent(int Channel, AlertKind Kind, double Value, double Threshold, long Sequence, double ElapsedSeconds,
    DateTime TimestampUtc);

public enum EngineEventKind
{
    SequenceGap,
    OutOfOrder,
    SourceInterrupted,
    SourceReconciled,
    DeviceFault,
    SourceCompleted,
    WorkerFailed,
}

public sealed record EngineEvent(EngineEventKind Kind, string Message, int? Channel = null, long? Sequence = null, long? Count = null)
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

public enum EngineState
{
    Idle,
    Starting,
    Running,
    Stopping,
    Faulted,
}

public sealed record EngineCounters(
    long Received,
    long Processed,
    long Dropped,
    long GapSamples,
    long Invalid,
    long OutOfOrder,
    long Alerts,
    long DisplaySkipped,
    long IgnoredWhileStopped,
    int QueueDepth,
    double MaxLatencyMs,
    double MeanLatencyMs);
