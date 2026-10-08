using System.Text.Json;
using System.Text.Json.Serialization;

namespace TempLab.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
}

public static class MessageTypes
{
    public const string Configure = "configure";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Status = "status";
    public const string Reset = "reset";
    public const string SetFaults = "setFaults";
    public const string Response = "response";
    public const string Samples = "samples";
    public const string DeviceFault = "deviceFault";
}

public static class ErrorCodes
{
    public const string InvalidState = "InvalidState";
    public const string InvalidConfiguration = "InvalidConfiguration";
    public const string UnknownCommand = "UnknownCommand";
    public const string UnsupportedVersion = "UnsupportedVersion";
    public const string BadRequest = "BadRequest";
    public const string Busy = "Busy";
    public const string Internal = "Internal";
}

/// <summary>Wire envelope. <see cref="Payload"/> is kept as raw JSON and decoded by type.</summary>
public sealed record Envelope
{
    [JsonPropertyName("v")] public int Version { get; init; } = ProtocolInfo.Version;
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }
}

public sealed record ErrorInfo(string Code, string Message);

public sealed record ResponsePayload
{
    public bool Ok { get; init; }
    public ErrorInfo? Error { get; init; }
    public JsonElement? Result { get; init; }
}

public enum DeviceState
{
    Idle,
    Acquiring,
    Faulted,
}

public enum SampleQuality
{
    Good,
    Suspect,
    Invalid,
}

public sealed record ChannelSignal
{
    public int Channel { get; init; }
    public double Baseline { get; init; } = 25.0;
    public double RampPerSecond { get; init; }
    public double Amplitude { get; init; }
    public double PeriodSeconds { get; init; } = 10.0;
    public double NoiseStd { get; init; } = 0.05;
    public int SpikeEvery { get; init; }
    public double SpikeMagnitude { get; init; }
}

public sealed record DeviceConfiguration
{
    public double RateHz { get; init; } = 10.0;
    public int Seed { get; init; } = 42;
    public IReadOnlyList<ChannelSignal> Channels { get; init; } = DefaultChannels();

    public const int MaxChannels = 16;
    public const double MaxRateHz = 1000.0;

    /// <summary>Default four-channel profile from Docs/requirements.md.</summary>
    public static IReadOnlyList<ChannelSignal> DefaultChannels() =>
    [
        new ChannelSignal { Channel = 0, Baseline = 25.0 },
        new ChannelSignal { Channel = 1, Baseline = 20.0, RampPerSecond = 0.05 },
        new ChannelSignal { Channel = 2, Baseline = 30.0, Amplitude = 2.0, PeriodSeconds = 20.0 },
        new ChannelSignal { Channel = 3, Baseline = 22.0, SpikeEvery = 150, SpikeMagnitude = 15.0 },
    ];

    /// <summary>Returns null when valid, otherwise a human readable reason.</summary>
    public string? Validate()
    {
        if (double.IsNaN(RateHz) || RateHz < 1 || RateHz > MaxRateHz)
            return $"rateHz must be between 1 and {MaxRateHz}";
        if (Channels is null || Channels.Count == 0 || Channels.Count > MaxChannels)
            return $"between 1 and {MaxChannels} channels are required";
        var seen = new HashSet<int>();
        foreach (var c in Channels)
        {
            if (c is null) return "channel entry is null";
            if (c.Channel < 0 || c.Channel >= MaxChannels) return $"channel id {c.Channel} out of range 0..{MaxChannels - 1}";
            if (!seen.Add(c.Channel)) return $"duplicate channel id {c.Channel}";
            if (!double.IsFinite(c.Baseline) || !double.IsFinite(c.RampPerSecond) || !double.IsFinite(c.Amplitude)
                || !double.IsFinite(c.SpikeMagnitude))
                return $"channel {c.Channel}: signal values must be finite";
            if (!double.IsFinite(c.NoiseStd) || c.NoiseStd < 0) return $"channel {c.Channel}: noiseStd must be >= 0";
            if (c.Amplitude != 0 && (!double.IsFinite(c.PeriodSeconds) || c.PeriodSeconds <= 0))
                return $"channel {c.Channel}: periodSeconds must be > 0 when amplitude is non-zero";
            if (c.SpikeEvery < 0) return $"channel {c.Channel}: spikeEvery must be >= 0";
        }
        return null;
    }
}

public sealed record FaultScenarioStatus(string? Name, int FaultCount);

public sealed record DeviceStatus
{
    public DeviceState State { get; init; }
    public string? AcquisitionId { get; init; }
    public long SamplesSentPerChannel { get; init; }
    public DeviceConfiguration Configuration { get; init; } = new();
    public ErrorInfo? Fault { get; init; }
    public FaultScenarioStatus? Scenario { get; init; }
}

public sealed record StartResult(string AcquisitionId);

public sealed record StopResult(bool WasAcquiring, long SamplesSentPerChannel);

/// <summary>One sample on the wire. Short names keep sample frames compact.</summary>
public sealed record SampleDto
{
    [JsonPropertyName("ch")] public int Channel { get; init; }
    [JsonPropertyName("seq")] public long Sequence { get; init; }
    [JsonPropertyName("t")] public double ElapsedSeconds { get; init; }
    [JsonPropertyName("v")] public double Value { get; init; }
    [JsonPropertyName("q")] public SampleQuality Quality { get; init; }
}

public sealed record SampleBatchPayload
{
    public string AcquisitionId { get; init; } = "";
    public IReadOnlyList<SampleDto> Samples { get; init; } = [];
}

public sealed record DeviceFaultPayload(string AcquisitionId, string Code, string Message);

public enum FaultKind
{
    DelayReply,
    Disconnect,
    AcquisitionFault,
    MalformedFrame,
    OversizedFrame,
    InvalidReadings,
    SkipSequences,
}

public sealed record FaultSpec
{
    public FaultKind Kind { get; init; }
    /// <summary>Trigger: once this many samples per channel have been generated.</summary>
    public long AfterSequence { get; init; }
    public string? Command { get; init; }
    public int DelayMs { get; init; }
    public int Count { get; init; } = 1;
    public int Channel { get; init; }
    public string? Code { get; init; }
}

public sealed record FaultScenario
{
    public string Name { get; init; } = "none";
    public int Seed { get; init; }
    public IReadOnlyList<FaultSpec> Faults { get; init; } = [];

    public string? Validate()
    {
        foreach (var f in Faults ?? [])
        {
            if (f is null) return "fault entry is null";
            if (!Enum.IsDefined(f.Kind)) return $"unknown fault kind {f.Kind}";
            if (f.AfterSequence < 0) return "afterSequence must be >= 0";
            if (f.Count < 1) return "count must be >= 1";
            if (f.Kind == FaultKind.DelayReply && (string.IsNullOrEmpty(f.Command) || f.DelayMs < 0))
                return "DelayReply requires command and delayMs >= 0";
        }
        return null;
    }
}
