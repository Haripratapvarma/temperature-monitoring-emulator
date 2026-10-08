using System.Globalization;
using TempLab.Acquisition;

namespace TempLab.Presentation;

public sealed class ChannelViewModel(int id, int historyCapacity) : ObservableObject
{
    private bool _isSelected = true;
    private double? _low;
    private double? _high;
    private string _raw = "—";
    private string _mean = "—";
    private string _min = "—";
    private string _max = "—";
    private bool _isWarm;
    private bool _isAlerting;

    public int Id { get; } = id;
    public string Name => $"Ch {Id}";
    public ChartSeries Raw { get; } = new(historyCapacity);
    public ChartSeries Mean { get; } = new(historyCapacity);

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public double? Low { get => _low; set => Set(ref _low, value); }
    public double? High { get => _high; set => Set(ref _high, value); }

    public string RawText { get => _raw; private set => Set(ref _raw, value); }
    public string MeanText { get => _mean; private set => Set(ref _mean, value); }
    public string MinText { get => _min; private set => Set(ref _min, value); }
    public string MaxText { get => _max; private set => Set(ref _max, value); }
    public bool IsWarm { get => _isWarm; private set => Set(ref _isWarm, value); }
    public bool IsAlerting { get => _isAlerting; private set => Set(ref _isAlerting, value); }

    private static string F(double v) => double.IsNaN(v) ? "—" : v.ToString("F2", CultureInfo.CurrentCulture);

    /// <summary>Applies a run of processed samples for this channel (UI thread).</summary>
    public void Apply(IReadOnlyList<ProcessedSample> samples)
    {
        if (samples.Count == 0) return;
        foreach (var s in samples)
        {
            Raw.Add(s.ElapsedSeconds, s.Valid ? s.Raw : double.NaN);
            Mean.Add(s.ElapsedSeconds, s.Mean);
        }
        var last = samples[^1];
        RawText = last.Valid ? F(last.Raw) : "invalid";
        MeanText = F(last.Mean);
        MinText = F(last.Min);
        MaxText = F(last.Max);
        IsWarm = last.IsWarm;
        IsAlerting = last.Valid && ((High is double hi && last.Raw > hi) || (Low is double lo && last.Raw < lo));
    }

    public void ClearHistory()
    {
        Raw.Clear();
        Mean.Clear();
        RawText = MeanText = MinText = MaxText = "—";
        IsWarm = false;
        IsAlerting = false;
    }
}

public sealed record AlertViewModel(string Time, string Channel, string Kind, string Value, string Threshold, string Sequence)
{
    public static AlertViewModel From(AlertEvent a) => new(
        a.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture),
        $"Ch {a.Channel}",
        a.Kind.ToString(),
        a.Value.ToString("F2", CultureInfo.CurrentCulture),
        a.Threshold.ToString("F2", CultureInfo.CurrentCulture),
        a.Sequence.ToString(CultureInfo.CurrentCulture));
}
