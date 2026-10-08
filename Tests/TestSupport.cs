using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using TempLab.Client;
using TempLab.Emulator;
using TempLab.Protocol;

namespace TempLab.Tests;

internal static class Wait
{
    /// <summary>Polls until the condition holds. Tests assert outcomes, not exact timer scheduling.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"condition not met within {timeoutMs} ms: {what}");
            await Task.Delay(10);
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "templab-tests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

/// <summary>An in-process emulator on an ephemeral localhost port, with captured logs.</summary>
internal sealed class EmulatorFixture : IAsyncDisposable
{
    public MemoryLogSink Logs { get; } = new();
    public EmulatorServer Server { get; }
    public int Port => Server.EndPoint.Port;

    public EmulatorFixture(FaultScenario? scenario = null, int port = 0)
    {
        Server = new EmulatorServer(IPAddress.Loopback, port, new StructuredLogger("emulator", Logs), scenario);
        Server.Start();
    }

    public InstrumentClient CreateClient(TimeSpan? requestTimeout = null, ReconnectPolicy? reconnect = null) =>
        new(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = Port,
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3),
            Reconnect = reconnect ?? new ReconnectPolicy { MaxAttempts = 5, InitialDelay = TimeSpan.FromMilliseconds(50) },
        });

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

/// <summary>Device output that records everything; optionally refuses sample frames (slow consumer).</summary>
internal sealed class CapturingOutput : IDeviceOutput
{
    public ConcurrentQueue<Envelope> Samples { get; } = new();
    public ConcurrentQueue<Envelope> Events { get; } = new();
    public ConcurrentQueue<byte[]> Raw { get; } = new();
    public volatile bool RejectSamples;
    public volatile string? AbortReason;

    public bool TrySendSamples(Envelope envelope)
    {
        if (RejectSamples) return false;
        Samples.Enqueue(envelope);
        return true;
    }

    public void SendEvent(Envelope envelope) => Events.Enqueue(envelope);
    public void SendRawFrame(byte[] frame) => Raw.Enqueue(frame);
    public void Abort(string reason) => AbortReason = reason;

    public IEnumerable<SampleDto> AllSamples() =>
        Samples.SelectMany(e => ProtocolJson.FromElement<SampleBatchPayload>(e.Payload)!.Samples);
}

/// <summary>Stream that returns at most N bytes per read, to exercise partial-read handling.</summary>
internal sealed class TricklingStream(byte[] data, int maxChunk) : Stream
{
    private int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = Math.Min(Math.Min(count, maxChunk), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
