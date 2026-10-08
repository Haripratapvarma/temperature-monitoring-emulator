using TempLab.Protocol;

namespace TempLab.Emulator;

/// <summary>
/// Evaluates a <see cref="FaultScenario"/>. Sequence-triggered faults fire at most once per acquisition;
/// DelayReply counts down across commands. Re-armed on every start so a scenario repeats identically.
/// </summary>
public sealed class FaultInjector
{
    private readonly object _gate = new();
    private FaultScenario _scenario = new();
    private readonly Dictionary<FaultSpec, int> _delayRemaining = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<FaultSpec> _fired = new(ReferenceEqualityComparer.Instance);

    public FaultScenario Scenario
    {
        get { lock (_gate) return _scenario; }
    }

    public void Load(FaultScenario scenario)
    {
        lock (_gate)
        {
            _scenario = scenario;
            _delayRemaining.Clear();
            foreach (var f in scenario.Faults.Where(f => f.Kind == FaultKind.DelayReply))
                _delayRemaining[f] = f.Count;
            _fired.Clear();
        }
    }

    /// <summary>Called on start: sequence-triggered faults may fire again in the new acquisition.</summary>
    public void ArmForAcquisition()
    {
        lock (_gate) _fired.Clear();
    }

    public TimeSpan ReplyDelayFor(string command)
    {
        lock (_gate)
        {
            foreach (var f in _scenario.Faults)
            {
                if (f.Kind != FaultKind.DelayReply || !string.Equals(f.Command, command, StringComparison.Ordinal)) continue;
                if (_delayRemaining.TryGetValue(f, out int left) && left > 0)
                {
                    _delayRemaining[f] = left - 1;
                    return TimeSpan.FromMilliseconds(f.DelayMs);
                }
            }
            return TimeSpan.Zero;
        }
    }

    /// <summary>Faults whose trigger is exactly <paramref name="sequence"/> and have not fired yet.</summary>
    public IReadOnlyList<FaultSpec> TakeTriggered(long sequence)
    {
        lock (_gate)
        {
            List<FaultSpec>? hits = null;
            foreach (var f in _scenario.Faults)
            {
                if (f.Kind is FaultKind.DelayReply or FaultKind.InvalidReadings) continue;
                if (f.AfterSequence == sequence && _fired.Add(f)) (hits ??= []).Add(f);
            }
            return (IReadOnlyList<FaultSpec>?)hits ?? [];
        }
    }

    public bool IsInvalidReading(int channel, long sequence)
    {
        lock (_gate)
        {
            foreach (var f in _scenario.Faults)
            {
                if (f.Kind == FaultKind.InvalidReadings && f.Channel == channel
                    && sequence >= f.AfterSequence && sequence < f.AfterSequence + f.Count)
                    return true;
            }
            return false;
        }
    }
}
