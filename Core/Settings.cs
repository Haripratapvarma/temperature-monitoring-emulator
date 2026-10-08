using TempLab.Protocol;

namespace TempLab.Acquisition;

public enum AlertSource
{
    Raw,
    RollingMean,
}

public sealed record ChannelAlertSettings(double? Low, double? High);

public enum ProcessorKind
{
    Native,
    ManagedReference,
}

public sealed record AcquisitionSettings
{
    public IReadOnlyList<int> Channels { get; init; } = [0, 1, 2, 3];
    public double RateHz { get; init; } = 10.0;
    public int Seed { get; init; } = 42;
    public int WindowSize { get; init; } = 20;
    public IReadOnlyDictionary<int, ChannelAlertSettings> Alerts { get; init; } = new Dictionary<int, ChannelAlertSettings>
    {
        [0] = new(20.0, 30.0),
        [1] = new(15.0, 30.0),
        [2] = new(26.0, 34.0),
        [3] = new(15.0, 30.0),
    };
    public AlertSource AlertSource { get; init; } = AlertSource.Raw;
    public double Hysteresis { get; init; } = 0.5;

    /// <summary>Bounded hand-off queue between the source thread and the processing worker, in batches.</summary>
    public int QueueCapacityBatches { get; init; } = 256;

    /// <summary>Processed samples kept for the UI between ticks; older display points are skipped (not lost data).</summary>
    public int DisplayBufferCapacity { get; init; } = 20_000;

    public string? RecordingPath { get; init; }
    public ProcessorKind Processor { get; init; } = ProcessorKind.Native;

    /// <summary>Optional device signal overrides; defaults come from <see cref="DeviceConfiguration.DefaultChannels"/>.</summary>
    public IReadOnlyList<ChannelSignal>? Signals { get; init; }

    public string? Validate()
    {
        if (Channels.Count == 0) return "select at least one channel";
        if (Channels.Distinct().Count() != Channels.Count) return "duplicate channel";
        if (Channels.Any(c => c < 0 || c >= DeviceConfiguration.MaxChannels)) return "channel out of range";
        if (WindowSize < 1 || WindowSize > 100_000) return "window size must be 1..100000";
        if (QueueCapacityBatches < 1) return "queue capacity must be >= 1";
        if (DisplayBufferCapacity < 1) return "display buffer must be >= 1";
        if (!(Hysteresis >= 0)) return "hysteresis must be >= 0";
        foreach (var (ch, a) in Alerts)
            if (a.Low is double lo && a.High is double hi && lo >= hi) return $"channel {ch}: low threshold must be below high";
        return ToDeviceConfiguration().Validate();
    }

    public DeviceConfiguration ToDeviceConfiguration()
    {
        var defaults = (Signals ?? DeviceConfiguration.DefaultChannels()).ToDictionary(s => s.Channel);
        return new DeviceConfiguration
        {
            RateHz = RateHz,
            Seed = Seed,
            Channels = Channels.Order().Select(c => defaults.TryGetValue(c, out var s) ? s : new ChannelSignal { Channel = c }).ToArray(),
        };
    }

    public int ProcessorChannelCount => Channels.Count == 0 ? 1 : Channels.Max() + 1;
}
