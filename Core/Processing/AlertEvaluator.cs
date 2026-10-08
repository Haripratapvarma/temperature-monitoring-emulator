namespace TempLab.Acquisition.Processing;

/// <summary>
/// Threshold crossing with hysteresis re-arm (see Docs/requirements.md, "Alerts").
/// High: raised on the transition to value &gt; high while armed; re-armed once value &lt; high − hysteresis.
/// Low:  raised on the transition to value &lt; low while armed;  re-armed once value &gt; low + hysteresis.
/// A reading that stays beyond a threshold therefore raises exactly one alert.
/// </summary>
public sealed class AlertEvaluator
{
    private readonly ChannelAlertSettings _settings;
    private readonly double _hysteresis;
    private bool _highArmed = true;
    private bool _lowArmed = true;

    public AlertEvaluator(ChannelAlertSettings settings, double hysteresis)
    {
        if (!(hysteresis >= 0)) throw new ArgumentOutOfRangeException(nameof(hysteresis));
        _settings = settings;
        _hysteresis = hysteresis;
    }

    public bool HighArmed => _highArmed;
    public bool LowArmed => _lowArmed;

    /// <summary>Evaluates one valid value. NaN is ignored (no state change).</summary>
    public AlertFlags Evaluate(double value)
    {
        if (double.IsNaN(value)) return AlertFlags.None;
        var flags = AlertFlags.None;
        if (_settings.High is double hi)
        {
            if (_highArmed && value > hi)
            {
                flags |= AlertFlags.HighRaised;
                _highArmed = false;
            }
            else if (!_highArmed && value < hi - _hysteresis)
            {
                _highArmed = true;
            }
        }
        if (_settings.Low is double lo)
        {
            if (_lowArmed && value < lo)
            {
                flags |= AlertFlags.LowRaised;
                _lowArmed = false;
            }
            else if (!_lowArmed && value > lo + _hysteresis)
            {
                _lowArmed = true;
            }
        }
        return flags;
    }

    public void Reset()
    {
        _highArmed = true;
        _lowArmed = true;
    }
}
