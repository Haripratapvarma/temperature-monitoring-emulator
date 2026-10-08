using TempLab.Protocol;

namespace TempLab.Acquisition.Sources;

/// <summary>Receives data from a source. Called on the source's own thread; implementations must not block.</summary>
public interface ISampleSink
{
    void OnBatch(SampleBatch batch);
    void OnSourceEvent(EngineEvent evt);
}

public sealed record SourceStartInfo(string AcquisitionId, string Description, DeviceConfiguration? Device);

/// <summary>
/// A producer of sample batches: the emulator over TCP or a recorded CSV session.
/// Both feed the same engine, so replay goes through exactly the same processing path as live data.
/// </summary>
public interface IAcquisitionSource
{
    string Description { get; }

    /// <summary>Starts delivering batches to <paramref name="sink"/>. Returns once delivery has begun.</summary>
    Task<SourceStartInfo> StartAsync(AcquisitionSettings settings, ISampleSink sink, CancellationToken ct);

    /// <summary>Stops delivery. When this completes, no further OnBatch calls are made.</summary>
    Task StopAsync(CancellationToken ct);
}
