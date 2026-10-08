using TempLab.Protocol;

namespace TempLab.Emulator;

/// <summary>SplitMix64: tiny, fast, fully specified PRNG so seeded noise is identical on every platform/runtime.</summary>
public struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in (0, 1]; never 0 so Log is safe.</summary>
    public double NextUnitOpen() => ((NextUInt64() >> 11) + 1) * (1.0 / 9007199254740992.0);
}

/// <summary>
/// Deterministic signal for one channel. Values are a pure function of (configuration, seed, channel, sequence)
/// provided samples are requested in increasing sequence order: each sequence consumes exactly two uniforms
/// (Box–Muller, no cached second value), and skipped sequences are still consumed so that a gap does not shift
/// the noise of later samples.
/// </summary>
public sealed class ChannelSignalGenerator
{
    private readonly ChannelSignal _signal;
    private readonly double _rateHz;
    private SplitMix64 _rng;
    private long _nextSequence;

    public ChannelSignalGenerator(ChannelSignal signal, double rateHz, int seed)
    {
        _signal = signal;
        _rateHz = rateHz;
        _rng = new SplitMix64(SeedFor(seed, signal.Channel));
    }

    public int Channel => _signal.Channel;

    public static ulong SeedFor(int seed, int channel) =>
        ((ulong)(uint)seed << 32) ^ ((ulong)(uint)channel * 0x9E3779B97F4A7C15UL) ^ 0xD1B54A32D192ED03UL;

    /// <summary>Noise-free component, exposed for tests.</summary>
    public double Deterministic(long sequence)
    {
        double t = sequence / _rateHz;
        double v = _signal.Baseline + _signal.RampPerSecond * t;
        if (_signal.Amplitude != 0)
            v += _signal.Amplitude * Math.Sin(2 * Math.PI * t / _signal.PeriodSeconds);
        if (_signal.SpikeEvery > 0 && sequence > 0 && sequence % _signal.SpikeEvery == 0)
            v += _signal.SpikeMagnitude;
        return v;
    }

    public double ValueAt(long sequence)
    {
        if (sequence < _nextSequence)
            throw new InvalidOperationException($"sequence {sequence} already generated (next is {_nextSequence})");
        double noise = 0;
        while (_nextSequence <= sequence)
        {
            double u1 = _rng.NextUnitOpen();
            double u2 = _rng.NextUnitOpen();
            noise = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            _nextSequence++;
        }
        return Deterministic(sequence) + _signal.NoiseStd * noise;
    }
}

public sealed class SignalGenerator
{
    private readonly ChannelSignalGenerator[] _channels;

    public SignalGenerator(DeviceConfiguration config)
    {
        RateHz = config.RateHz;
        _channels = config.Channels.OrderBy(c => c.Channel)
            .Select(c => new ChannelSignalGenerator(c, config.RateHz, config.Seed)).ToArray();
    }

    public double RateHz { get; }
    public IReadOnlyList<ChannelSignalGenerator> Channels => _channels;
}
