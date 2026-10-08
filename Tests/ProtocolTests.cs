using System.Buffers.Binary;
using System.Text;
using TempLab.Protocol;

namespace TempLab.Tests;

public class FrameCodecTests
{
    private static byte[] Frames(params Envelope[] envs) => envs.SelectMany(FrameCodec.EncodeEnvelope).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Reads_frames_across_partial_reads(int chunk)
    {
        var a = ProtocolJson.Request(MessageTypes.Status, "c-1");
        var b = ProtocolJson.Request(MessageTypes.Configure, "c-2", new DeviceConfiguration { RateHz = 25, Seed = 7 });
        var stream = new TricklingStream(Frames(a, b), chunk);

        var e1 = await FrameCodec.ReadEnvelopeAsync(stream, FrameCodec.DefaultMaxFrameBytes, default);
        var e2 = await FrameCodec.ReadEnvelopeAsync(stream, FrameCodec.DefaultMaxFrameBytes, default);
        var end = await FrameCodec.ReadEnvelopeAsync(stream, FrameCodec.DefaultMaxFrameBytes, default);

        Assert.Equal("c-1", e1!.Id);
        Assert.Equal(MessageTypes.Configure, e2!.Type);
        Assert.Equal(25.0, ProtocolJson.FromElement<DeviceConfiguration>(e2.Payload)!.RateHz, 0.0);
        Assert.Null(end); // clean EOF between frames
    }

    [Fact]
    public void Decoder_handles_multiple_frames_in_one_chunk_and_split_frames()
    {
        var bytes = Frames(ProtocolJson.Request("status", "1"), ProtocolJson.Request("stop", "2"), ProtocolJson.Request("reset", "3"));
        var dec = new FrameCodec.Decoder();
        int split = bytes.Length - 5;
        var first = dec.Push(bytes.AsSpan(0, split));
        Assert.Equal(2, first.Count);
        Assert.True(dec.BufferedBytes > 0);
        var second = dec.Push(bytes.AsSpan(split));
        Assert.Single(second);
        Assert.Equal(0, dec.BufferedBytes);
        Assert.Equal("reset", ProtocolJson.DeserializeEnvelope(second[0]).Type);
    }

    [Fact]
    public async Task Oversized_length_is_rejected_before_allocating_the_body()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FrameCodec.ReadFrameAsync(new MemoryStream(header), 1024, default));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(ProtocolErrorKind.Oversized, ex.Kind);
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public async Task Zero_length_frame_is_a_protocol_error()
    {
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FrameCodec.ReadFrameAsync(new MemoryStream(new byte[4]), 1024, default));
        Assert.Equal(ProtocolErrorKind.InvalidLength, ex.Kind);
    }

    [Theory]
    [InlineData(2)]   // EOF inside header
    [InlineData(10)]  // EOF inside body
    public async Task Truncated_frame_is_a_protocol_error(int keep)
    {
        var bytes = Frames(ProtocolJson.Request("status", "c-1"));
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FrameCodec.ReadFrameAsync(new MemoryStream(bytes[..keep]), 1024, default));
        Assert.Equal(ProtocolErrorKind.Truncated, ex.Kind);
    }

    [Theory]
    [InlineData("{this is not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"v\":1}")]
    public void Invalid_envelope_is_a_protocol_error(string body)
    {
        var ex = Assert.Throws<ProtocolException>(() => ProtocolJson.DeserializeEnvelope(Encoding.UTF8.GetBytes(body)));
        Assert.Equal(ProtocolErrorKind.InvalidMessage, ex.Kind);
    }

    [Fact]
    public void Samples_round_trip_including_nan_and_quality()
    {
        var env = ProtocolJson.Event(MessageTypes.Samples, new SampleBatchPayload
        {
            AcquisitionId = "a1",
            Samples = [new SampleDto { Channel = 2, Sequence = 9, ElapsedSeconds = 0.9, Value = double.NaN, Quality = SampleQuality.Invalid }],
        });
        var back = ProtocolJson.DeserializeEnvelope(ProtocolJson.SerializeEnvelope(env));
        var s = Assert.Single(ProtocolJson.FromElement<SampleBatchPayload>(back.Payload)!.Samples);
        Assert.True(double.IsNaN(s.Value));
        Assert.Equal(SampleQuality.Invalid, s.Quality);
        Assert.Equal(9L, s.Sequence);
    }
}

public class ConfigurationValidationTests
{
    [Fact]
    public void Default_configuration_is_valid() => Assert.Null(new DeviceConfiguration().Validate());

    [Theory]
    [InlineData(0.5)]
    [InlineData(1001.0)]
    [InlineData(double.NaN)]
    public void Rate_out_of_range_is_rejected(double rate) =>
        Assert.NotNull(new DeviceConfiguration { RateHz = rate }.Validate());

    [Fact]
    public void Duplicate_and_out_of_range_channels_are_rejected()
    {
        Assert.Contains("duplicate", new DeviceConfiguration
        {
            Channels = [new ChannelSignal { Channel = 1 }, new ChannelSignal { Channel = 1 }],
        }.Validate());
        Assert.NotNull(new DeviceConfiguration { Channels = [new ChannelSignal { Channel = 16 }] }.Validate());
        Assert.NotNull(new DeviceConfiguration { Channels = [] }.Validate());
    }

    [Fact]
    public void Signal_parameters_are_validated()
    {
        Assert.NotNull(new DeviceConfiguration { Channels = [new ChannelSignal { NoiseStd = -1 }] }.Validate());
        Assert.NotNull(new DeviceConfiguration { Channels = [new ChannelSignal { Amplitude = 1, PeriodSeconds = 0 }] }.Validate());
        Assert.NotNull(new DeviceConfiguration { Channels = [new ChannelSignal { SpikeEvery = -5 }] }.Validate());
        Assert.NotNull(new DeviceConfiguration { Channels = [new ChannelSignal { Baseline = double.PositiveInfinity }] }.Validate());
    }

    [Fact]
    public void Fault_scenarios_are_validated()
    {
        Assert.Null(new FaultScenario { Faults = [new FaultSpec { Kind = FaultKind.Disconnect, AfterSequence = 3 }] }.Validate());
        Assert.NotNull(new FaultScenario { Faults = [new FaultSpec { Kind = FaultKind.DelayReply }] }.Validate());
        Assert.NotNull(new FaultScenario { Faults = [new FaultSpec { Kind = FaultKind.SkipSequences, Count = 0 }] }.Validate());
    }
}
