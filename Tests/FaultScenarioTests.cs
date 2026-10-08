using System.Diagnostics;
using System.Net;
using TempLab.Acquisition;
using TempLab.Acquisition.Sources;
using TempLab.Client;
using TempLab.Emulator;
using TempLab.Protocol;

namespace TempLab.Tests;

/// <summary>
/// The six required fault scenarios from the build plan. Each uses a deterministic, sequence-triggered fault and
/// asserts the recovery outcome rather than exact timing.
/// </summary>
public class FaultScenarioTests
{
    private static FaultScenario One(string name, FaultSpec f) => new() { Name = name, Seed = 1, Faults = [f] };

    private static AcquisitionSettings Fast(string? recording = null) => new()
    {
        RateHz = 100,
        Seed = 3,
        RecordingPath = recording,
    };

    // 1 ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Late_reply_is_counted_and_dropped_on_one_connection()
    {
        await using var emu = new EmulatorFixture();
        await using var client = emu.CreateClient(requestTimeout: TimeSpan.FromMilliseconds(300));
        await client.ConnectAsync();
        await client.SetFaultsAsync(One("delay-status", new FaultSpec
        {
            Kind = FaultKind.DelayReply, Command = MessageTypes.Status, DelayMs = 800, Count = 1,
        }));

        var t0 = Stopwatch.StartNew();
        var timedOut = await Assert.ThrowsAsync<RequestTimeoutException>(() => client.GetStatusAsync());
        // Sent while the late reply is still in flight. If matching were by order instead of ID,
        // this request would be completed by the late reply (~800 ms) with the wrong ID.
        var status = await client.SendAsync(MessageTypes.Status, timeout: TimeSpan.FromSeconds(3));
        Assert.True(status.Ok);
        Assert.True(t0.ElapsedMilliseconds < 800, "the undelayed request got its own prompt reply");
        await Wait.UntilAsync(() => client.LateResponses == 1, 3000, "late reply dropped");
        Assert.Contains(emu.Logs.WithEvent("fault_injected"), e => Equals(e.Fields["requestId"], timedOut.RequestId));
        Assert.True((await client.GetStatusAsync()).State == DeviceState.Idle);
    }

    // 2 ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Disconnect_mid_acquisition_marks_interrupted_records_gap_and_reconciles()
    {
        using var dir = new TempDir();
        await using var emu = new EmulatorFixture(One("drop-link", new FaultSpec { Kind = FaultKind.Disconnect, AfterSequence = 20 }));
        await using var client = emu.CreateClient();
        await client.ConnectAsync();
        await using var engine = new AcquisitionEngine();
        var events = new List<EngineEvent>();
        var path = dir.File("interrupted.csv");

        await engine.StartAsync(new EmulatorSource(client), Fast(path));
        await Wait.UntilAsync(() =>
        {
            engine.DrainEvents(events);
            return events.Any(e => e.Kind == EngineEventKind.SourceReconciled);
        }, 8000, "reconciled");

        var interrupted = Assert.Single(events, e => e.Kind == EngineEventKind.SourceInterrupted);
        Assert.Contains("RemoteClosed", interrupted.Message);
        Assert.Contains("Interrupted", events.Single(e => e.Kind == EngineEventKind.SourceReconciled).Message);
        Assert.Equal(ConnectionState.Connected, client.State);               // reconnected with backoff
        Assert.Equal(DeviceState.Idle, (await client.GetStatusAsync()).State); // device abandoned the acquisition
        await engine.StopAsync();

        Assert.Equal(80L, engine.Counters.Processed); // exactly 20 samples × 4 channels before the link dropped
        Assert.Single(emu.Logs.WithEvent("command"), e => Equals(e.Fields["command"], "start")); // never restarted
        Assert.Contains(File.ReadLines(path), l => l.StartsWith("# event=SourceInterrupted", StringComparison.Ordinal));
    }

    // 3 ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Device_fault_enters_Faulted_sends_error_stops_streaming_and_accepts_reset()
    {
        await using var emu = new EmulatorFixture(One("sensor", new FaultSpec { Kind = FaultKind.AcquisitionFault, AfterSequence = 15, Code = "SensorFault" }));
        await using var client = emu.CreateClient();
        DeviceFaultPayload? fault = null;
        long samples = 0;
        client.DeviceFaultReceived += f => fault = f;
        client.SamplesReceived += b => Interlocked.Add(ref samples, b.Samples.Count);
        await client.ConnectAsync();
        await client.ConfigureAsync(new DeviceConfiguration { RateHz = 100 });
        var acq = (await client.StartAsync()).AcquisitionId;

        await Wait.UntilAsync(() => fault is not null, 5000, "deviceFault event");
        Assert.Equal("SensorFault", fault!.Code);
        Assert.Equal(acq, fault.AcquisitionId);
        var status = await client.GetStatusAsync();
        Assert.Equal(DeviceState.Faulted, status.State);
        Assert.Equal("SensorFault", status.Fault!.Code);
        long atFault = Interlocked.Read(ref samples);
        Assert.Equal(15L * 4, atFault);
        await Task.Delay(200);
        Assert.Equal(atFault, Interlocked.Read(ref samples)); // streaming stopped

        var ex = await Assert.ThrowsAsync<DeviceCommandException>(() => client.StartAsync());
        Assert.Equal(ErrorCodes.InvalidState, ex.Error.Code);
        await client.ResetAsync();
        Assert.Equal(DeviceState.Idle, (await client.GetStatusAsync()).State);
        Assert.NotEqual(acq, (await client.StartAsync()).AcquisitionId); // documented recovery: reset, then start
        await client.StopAsync();
    }

    // 4 ---------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(FaultKind.MalformedFrame, "InvalidMessage")]
    [InlineData(FaultKind.OversizedFrame, "Oversized")]
    public async Task Malformed_or_oversized_frame_reports_protocol_failure_and_closes(FaultKind kind, string expectedKind)
    {
        await using var emu = new EmulatorFixture(One(kind.ToString(), new FaultSpec { Kind = kind, AfterSequence = 10 }));
        await using var client = emu.CreateClient();
        DisconnectInfo? info = null;
        client.Disconnected += d => info = d;
        await client.ConnectAsync();
        long before = GC.GetTotalAllocatedBytes(precise: false);
        await client.StartAsync();

        await Wait.UntilAsync(() => info is not null, 5000, "disconnect");
        Assert.Equal(DisconnectKind.ProtocolError, info!.Kind);
        Assert.Contains(expectedKind, info.Reason);
        Assert.Equal(1L, client.ProtocolErrors);
        Assert.Equal(ConnectionState.Disconnected, client.State);
        // The oversized header announced 64 MiB; nothing close to that was allocated.
        Assert.True(GC.GetTotalAllocatedBytes(false) - before < 48L * 1024 * 1024);
        await Wait.UntilAsync(() => emu.Server.Device.State == DeviceState.Idle, 3000, "device idle after close");
    }

    // 5 ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Slow_consumer_drops_newest_batches_with_counters_and_conserves_samples()
    {
        await using var emu = new EmulatorFixture();
        await using var client = emu.CreateClient();
        await client.ConnectAsync();
        await using var engine = new AcquisitionEngine
        {
            BeforeBatchHook = async _ => await Task.Delay(200), // ~5 batches/s processed vs ~20 batches/s arriving
        };
        await engine.StartAsync(new EmulatorSource(client), Fast() with { RateHz = 400, QueueCapacityBatches = 2 });
        await Wait.UntilAsync(() => engine.Counters.Dropped > 0, 8000, "drops");
        await engine.StopAsync();
        var c = engine.Counters;
        Assert.True(c.Dropped > 0);
        Assert.Equal(c.Received, c.Processed + c.Dropped); // every received sample is processed or counted as dropped
        Assert.Equal(EngineState.Idle, engine.State);
    }

    // 6 ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Cancellation_and_shutdown_leave_no_stranded_resources_and_restart_succeeds()
    {
        using var dir = new TempDir();
        int port;
        await using (var emu = new EmulatorFixture())
        {
            port = emu.Port;
            await using var client = emu.CreateClient();
            await client.ConnectAsync();
            await using var engine = new AcquisitionEngine();
            for (int i = 0; i < 10; i++)
            {
                var file = dir.File($"run{i}.csv");
                await engine.StartAsync(new EmulatorSource(client), Fast(file));
                await Wait.UntilAsync(() => engine.Counters.Processed > 0);
                await engine.StopAsync();
                Assert.Equal(EngineState.Idle, engine.State);
                Assert.Null(engine.RecordingPath);
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } // file closed
                Assert.Equal(DeviceState.Idle, (await client.GetStatusAsync()).State);
            }

            // Server shutdown while acquiring: client notices, server disposal completes.
            await engine.StartAsync(new EmulatorSource(client) { AutoReconnect = false }, Fast());
            await Wait.UntilAsync(() => engine.Counters.Processed > 0);
            var dispose = emu.Server.DisposeAsync().AsTask();
            Assert.True(await Task.WhenAny(dispose, Task.Delay(5000)) == dispose, "server shut down promptly");
            await Wait.UntilAsync(() => client.State == ConnectionState.Disconnected, 3000);
            await engine.StopAsync();
            Assert.Equal(EngineState.Idle, engine.State);
        }

        // A fresh emulator on the same port accepts a new session.
        await using var again = new EmulatorFixture(port: port);
        await using var c2 = again.CreateClient();
        Assert.Equal(DeviceState.Idle, (await c2.ConnectAsync()).State);
        await c2.StartAsync();
        await c2.StopAsync();
    }
}
