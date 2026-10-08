using TempLab.Client;
using TempLab.Emulator;
using TempLab.Protocol;

namespace TempLab.Tests;

public class SignalGeneratorTests
{
    private static ChannelSignalGenerator Gen(ChannelSignal s, double rate = 10, int seed = 1) => new(s, rate, seed);

    [Fact]
    public void Baseline_without_noise_is_exact()
    {
        var g = Gen(new ChannelSignal { Baseline = 25, NoiseStd = 0 });
        for (long n = 0; n < 50; n++) Assert.Equal(25.0, g.ValueAt(n), 0.0);
    }

    [Fact]
    public void Ramp_is_linear_in_elapsed_time()
    {
        var g = Gen(new ChannelSignal { Baseline = 20, RampPerSecond = 0.5, NoiseStd = 0 }, rate: 4);
        Assert.Equal(20.0, g.ValueAt(0), 1e-12);
        Assert.Equal(20.5, g.ValueAt(4), 1e-12);   // t = 1 s
        Assert.Equal(25.0, g.ValueAt(40), 1e-12);  // t = 10 s
    }

    [Fact]
    public void Periodic_component_has_expected_values()
    {
        var g = Gen(new ChannelSignal { Baseline = 30, Amplitude = 2, PeriodSeconds = 20, NoiseStd = 0 }, rate: 10);
        Assert.Equal(30.0, g.Deterministic(0), 1e-12);
        Assert.Equal(32.0, g.Deterministic(50), 1e-12);  // quarter period (5 s)
        Assert.Equal(30.0, g.Deterministic(100), 1e-9);  // half period
        Assert.Equal(28.0, g.Deterministic(150), 1e-12); // three quarters
    }

    [Fact]
    public void Spikes_occur_at_configured_sequences_only()
    {
        var g = Gen(new ChannelSignal { Baseline = 22, SpikeEvery = 150, SpikeMagnitude = 15, NoiseStd = 0 });
        Assert.Equal(22.0, g.Deterministic(0), 0.0);
        Assert.Equal(22.0, g.Deterministic(149), 0.0);
        Assert.Equal(37.0, g.Deterministic(150), 0.0);
        Assert.Equal(22.0, g.Deterministic(151), 0.0);
        Assert.Equal(37.0, g.Deterministic(300), 0.0);
    }

    [Fact]
    public void Seeded_noise_is_reproducible_and_seed_dependent()
    {
        var s = new ChannelSignal { Channel = 2, Baseline = 0, NoiseStd = 1 };
        var a = Gen(s, seed: 42);
        var b = Gen(s, seed: 42);
        var c = Gen(s, seed: 43);
        var va = Enumerable.Range(0, 1000).Select(n => a.ValueAt(n)).ToArray();
        var vb = Enumerable.Range(0, 1000).Select(n => b.ValueAt(n)).ToArray();
        var vc = Enumerable.Range(0, 1000).Select(n => c.ValueAt(n)).ToArray();
        Assert.Equal(va, vb);
        Assert.NotEqual(va, vc);
        double mean = va.Average();
        double sd = Math.Sqrt(va.Select(v => (v - mean) * (v - mean)).Average());
        Assert.InRange(mean, -0.1, 0.1);
        Assert.InRange(sd, 0.9, 1.1);
    }

    [Fact]
    public void Skipped_sequences_do_not_shift_later_noise()
    {
        var s = new ChannelSignal { NoiseStd = 1 };
        var full = Gen(s);
        var skipping = Gen(s);
        double[] expected = Enumerable.Range(0, 100).Select(n => full.ValueAt(n)).ToArray();
        Assert.Equal(expected[10], skipping.ValueAt(10), 0.0);
        Assert.Equal(expected[60], skipping.ValueAt(60), 0.0); // jumped over 11..59
        Assert.Throws<InvalidOperationException>(() => skipping.ValueAt(5));
    }

    [Fact]
    public void Channels_have_independent_noise_streams()
    {
        Assert.NotEqual(ChannelSignalGenerator.SeedFor(42, 0), ChannelSignalGenerator.SeedFor(42, 1));
        Assert.NotEqual(ChannelSignalGenerator.SeedFor(42, 0), ChannelSignalGenerator.SeedFor(43, 0));
    }
}

public class DeviceStateMachineTests
{
    private readonly MemoryLogSink _logs = new();
    private readonly CapturingOutput _out = new();
    private readonly InstrumentDevice _device;
    private int _id;

    public DeviceStateMachineTests() =>
        _device = new InstrumentDevice(new StructuredLogger("device", _logs)) { BatchInterval = TimeSpan.FromMilliseconds(5) };

    private async Task<ResponsePayload> Send(string type, object? payload = null)
    {
        var r = await _device.ExecuteAsync(ProtocolJson.Request(type, "t-" + ++_id, payload), _out);
        Assert.Equal("t-" + _id, r.Response.Id);
        return ProtocolJson.FromElement<ResponsePayload>(r.Response.Payload)!;
    }

    private static void AssertError(ResponsePayload r, string code)
    {
        Assert.False(r.Ok);
        Assert.Equal(code, r.Error!.Code);
    }

    [Fact]
    public async Task Idle_accepts_configure_status_stop_reset()
    {
        Assert.True((await Send(MessageTypes.Configure, new DeviceConfiguration { RateHz = 50 })).Ok);
        Assert.True((await Send(MessageTypes.Status)).Ok);
        var stop = await Send(MessageTypes.Stop);
        Assert.True(stop.Ok);
        Assert.False(ProtocolJson.FromElement<StopResult>(stop.Result)!.WasAcquiring);
        Assert.True((await Send(MessageTypes.Reset)).Ok);
        Assert.Equal(DeviceState.Idle, _device.State);
    }

    [Fact]
    public async Task Invalid_configuration_is_rejected_and_not_applied()
    {
        AssertError(await Send(MessageTypes.Configure, new DeviceConfiguration { RateHz = 5000 }), ErrorCodes.InvalidConfiguration);
        Assert.Equal(10.0, _device.GetStatus().Configuration.RateHz, 0.0);
        AssertError(await Send(MessageTypes.Configure), ErrorCodes.BadRequest);
    }

    [Fact]
    public async Task Acquiring_rejects_configure_duplicate_start_and_setFaults()
    {
        await Send(MessageTypes.Configure, new DeviceConfiguration { RateHz = 100 });
        var start = await Send(MessageTypes.Start);
        Assert.True(start.Ok);
        Assert.Equal(DeviceState.Acquiring, _device.State);
        AssertError(await Send(MessageTypes.Start), ErrorCodes.InvalidState);
        AssertError(await Send(MessageTypes.Configure, new DeviceConfiguration()), ErrorCodes.InvalidState);
        AssertError(await Send(MessageTypes.SetFaults, new FaultScenario()), ErrorCodes.InvalidState);
        Assert.True((await Send(MessageTypes.Status)).Ok);
        Assert.True((await Send(MessageTypes.Stop)).Ok);
    }

    [Fact]
    public async Task Stop_acknowledges_only_after_generation_has_completed()
    {
        await Send(MessageTypes.Configure, new DeviceConfiguration { RateHz = 200 });
        var acq = ProtocolJson.FromElement<StartResult>((await Send(MessageTypes.Start)).Result)!.AcquisitionId;
        await Wait.UntilAsync(() => _out.Samples.Count > 3);
        var stop = ProtocolJson.FromElement<StopResult>((await Send(MessageTypes.Stop)).Result)!;
        Assert.True(stop.WasAcquiring);
        int framesAtAck = _out.Samples.Count;
        await Task.Delay(150);
        Assert.Equal(framesAtAck, _out.Samples.Count); // nothing after the ack
        var samples = _out.AllSamples().ToList();
        Assert.Equal(stop.SamplesSentPerChannel * 4, samples.Count);
        Assert.All(samples.GroupBy(s => s.Channel), g =>
            Assert.Equal(Enumerable.Range(0, g.Count()).Select(i => (long)i), g.Select(s => s.Sequence)));
        Assert.Equal(DeviceState.Idle, _device.State);
        Assert.Contains(_logs.WithEvent("state_transition"), e => Equals(e.Fields["acquisitionId"], acq) && Equals(e.Fields["to"], "Idle"));
    }

    [Fact]
    public async Task Each_start_issues_a_new_acquisition_id()
    {
        var a = ProtocolJson.FromElement<StartResult>((await Send(MessageTypes.Start)).Result)!.AcquisitionId;
        await Send(MessageTypes.Stop);
        var b = ProtocolJson.FromElement<StartResult>((await Send(MessageTypes.Start)).Result)!.AcquisitionId;
        await Send(MessageTypes.Stop);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Unknown_command_and_bad_version_are_rejected()
    {
        AssertError(await Send("selfDestruct"), ErrorCodes.UnknownCommand);
        var r = await _device.ExecuteAsync(new Envelope { Version = 2, Type = "status", Id = "x" }, _out);
        AssertError(ProtocolJson.FromElement<ResponsePayload>(r.Response.Payload)!, ErrorCodes.UnsupportedVersion);
    }

    [Fact]
    public async Task Same_configuration_produces_identical_samples()
    {
        var cfg = new DeviceConfiguration { RateHz = 500, Seed = 99 };
        async Task<List<SampleDto>> Run()
        {
            var o = new CapturingOutput();
            var d = new InstrumentDevice(StructuredLogger.Null) { BatchInterval = TimeSpan.FromMilliseconds(3) };
            await d.ExecuteAsync(ProtocolJson.Request("configure", "1", cfg), o);
            await d.ExecuteAsync(ProtocolJson.Request("start", "2"), o);
            await Wait.UntilAsync(() => o.AllSamples().Count() >= 400);
            await d.ExecuteAsync(ProtocolJson.Request("stop", "3"), o);
            return o.AllSamples().Take(400).ToList();
        }
        var a = await Run();
        var b = await Run();
        Assert.Equal(a, b); // timer jitter changes batching, never values
    }

    [Fact]
    public async Task Device_buffer_overflow_faults_and_stops_explicitly()
    {
        _out.RejectSamples = true;
        await Send(MessageTypes.Configure, new DeviceConfiguration { RateHz = 100 });
        await Send(MessageTypes.Start);
        await Wait.UntilAsync(() => _device.State == DeviceState.Faulted);
        Assert.Equal("BufferOverflow", _device.GetStatus().Fault!.Code);
        var evt = Assert.Single(_out.Events);
        Assert.Equal(MessageTypes.DeviceFault, evt.Type);
        AssertError(await Send(MessageTypes.Start), ErrorCodes.InvalidState);
        Assert.True((await Send(MessageTypes.Reset)).Ok);
        Assert.Equal(DeviceState.Idle, _device.State);
        Assert.Null(_device.GetStatus().Fault);
    }

    [Fact]
    public async Task Invalid_readings_and_skipped_sequences_are_injected_deterministically()
    {
        await Send(MessageTypes.SetFaults, new FaultScenario
        {
            Name = "data-faults",
            Faults =
            [
                new FaultSpec { Kind = FaultKind.InvalidReadings, Channel = 1, AfterSequence = 5, Count = 3 },
                new FaultSpec { Kind = FaultKind.SkipSequences, AfterSequence = 20, Count = 4 },
            ],
        });
        await Send(MessageTypes.Configure, new DeviceConfiguration { RateHz = 500 });
        await Send(MessageTypes.Start);
        await Wait.UntilAsync(() => _out.AllSamples().Count() >= 4 * 40);
        await Send(MessageTypes.Stop);
        var ch1 = _out.AllSamples().Where(s => s.Channel == 1).ToList();
        Assert.Equal(new long[] { 5, 6, 7 }, ch1.Where(s => s.Quality == SampleQuality.Invalid).Select(s => s.Sequence));
        Assert.All(ch1.Where(s => s.Quality == SampleQuality.Invalid), s => Assert.True(double.IsNaN(s.Value)));
        var seqs = ch1.Select(s => s.Sequence).ToList();
        Assert.DoesNotContain(seqs, s => s >= 20 && s < 24);
        Assert.Contains(24L, seqs);
    }
}

public class EmulatorIntegrationTests : IAsyncLifetime
{
    private EmulatorFixture _emu = null!;

    public Task InitializeAsync()
    {
        _emu = new EmulatorFixture();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _emu.DisposeAsync();

    [Fact]
    public async Task Console_style_session_configure_start_stop_status_reset()
    {
        await using var client = _emu.CreateClient();
        long samples = 0;
        client.SamplesReceived += b => Interlocked.Add(ref samples, b.Samples.Count);

        var status = await client.ConnectAsync();
        Assert.Equal(DeviceState.Idle, status.State);
        await client.ConfigureAsync(new DeviceConfiguration { RateHz = 100, Seed = 5 });
        var start = await client.StartAsync();
        await Wait.UntilAsync(() => Interlocked.Read(ref samples) >= 40);
        var during = await client.GetStatusAsync(); // status stays usable while streaming
        Assert.Equal(DeviceState.Acquiring, during.State);
        Assert.Equal(start.AcquisitionId, during.AcquisitionId);
        var stop = await client.StopAsync();
        Assert.True(stop.WasAcquiring);
        await Task.Delay(100);
        Assert.Equal(stop.SamplesSentPerChannel * 4, Interlocked.Read(ref samples));
        await client.ResetAsync();
        Assert.Equal(DeviceState.Idle, (await client.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Duplicate_start_returns_InvalidState()
    {
        await using var client = _emu.CreateClient();
        await client.ConnectAsync();
        await client.StartAsync();
        var ex = await Assert.ThrowsAsync<DeviceCommandException>(() => client.StartAsync());
        Assert.Equal(ErrorCodes.InvalidState, ex.Error.Code);
        await client.StopAsync();
    }

    [Fact]
    public async Task Second_client_is_rejected_as_busy()
    {
        await using var first = _emu.CreateClient();
        await first.ConnectAsync();
        await using var second = _emu.CreateClient();
        var ex = await Assert.ThrowsAnyAsync<InstrumentException>(() => second.ConnectAsync());
        Assert.True(ex is DeviceCommandException { Error.Code: ErrorCodes.Busy } or ConnectionLostException, ex.ToString());
        Assert.Equal(DeviceState.Idle, (await first.GetStatusAsync()).State); // first client unaffected
    }

    [Fact]
    public async Task Concurrent_requests_are_matched_to_their_own_responses()
    {
        await using var client = _emu.CreateClient();
        await client.ConnectAsync();
        await client.StartAsync();
        var tasks = Enumerable.Range(0, 50).Select(_ => client.GetStatusAsync()).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(DeviceState.Acquiring, r.State));
        await client.StopAsync();
        Assert.Equal(0L, client.LateResponses);
        Assert.Equal(0L, client.ProtocolErrors);
    }

    [Fact]
    public async Task Client_disconnect_stops_acquisition_and_device_accepts_new_client()
    {
        var c1 = _emu.CreateClient();
        await c1.ConnectAsync();
        var acq = (await c1.StartAsync()).AcquisitionId;
        await c1.DisposeAsync();
        await Wait.UntilAsync(() => _emu.Logs.WithEvent("acquisition_abandoned").Any(e => Equals(e.Fields["acquisitionId"], acq)));
        Assert.Equal(DeviceState.Idle, _emu.Server.Device.State);

        await using var c2 = _emu.CreateClient();
        var status = await ConnectWithRetry(c2);
        Assert.Equal(DeviceState.Idle, status.State);
    }

    private static async Task<DeviceStatus> ConnectWithRetry(InstrumentClient c)
    {
        for (int i = 0; ; i++)
        {
            try { return await c.ConnectAsync(); }
            catch (InstrumentException) when (i < 20) { await Task.Delay(25); }
        }
    }
}

public class ReconnectPolicyTests
{
    [Fact]
    public void Backoff_is_exponential_and_capped()
    {
        var p = new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(100), Multiplier = 2, MaxDelay = TimeSpan.FromMilliseconds(500) };
        Assert.Equal(TimeSpan.Zero, p.DelayBeforeAttempt(1));
        Assert.Equal(TimeSpan.FromMilliseconds(100), p.DelayBeforeAttempt(2));
        Assert.Equal(TimeSpan.FromMilliseconds(200), p.DelayBeforeAttempt(3));
        Assert.Equal(TimeSpan.FromMilliseconds(400), p.DelayBeforeAttempt(4));
        Assert.Equal(TimeSpan.FromMilliseconds(500), p.DelayBeforeAttempt(5));
        Assert.Equal(TimeSpan.FromMilliseconds(500), p.DelayBeforeAttempt(50));
    }

    [Fact]
    public async Task Reconnect_gives_up_after_max_attempts()
    {
        int port;
        await using (var emu = new EmulatorFixture()) port = emu.Port; // now closed
        var client = new InstrumentClient(new ClientOptions
        {
            Port = port,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            Reconnect = new ReconnectPolicy { MaxAttempts = 3, InitialDelay = TimeSpan.FromMilliseconds(10) },
        });
        var ex = await Assert.ThrowsAsync<ConnectionLostException>(() => client.ReconnectAsync());
        Assert.Contains("3 attempts", ex.Message);
    }

    [Theory]
    [InlineData(DeviceState.Acquiring, "a1", ReconcileOutcome.StillAcquiring)]
    [InlineData(DeviceState.Acquiring, "other", ReconcileOutcome.Interrupted)]
    [InlineData(DeviceState.Idle, "a1", ReconcileOutcome.Interrupted)]
    [InlineData(DeviceState.Faulted, "a1", ReconcileOutcome.DeviceFaulted)]
    public void Reconciliation_never_assumes_an_uncertain_acquisition_is_running(DeviceState state, string acq, ReconcileOutcome expected) =>
        Assert.Equal(expected, AcquisitionReconciler.Reconcile(new DeviceStatus { State = state, AcquisitionId = acq }, "a1"));
}
