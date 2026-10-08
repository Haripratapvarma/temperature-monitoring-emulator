namespace TempLab.Presentation;

/// <summary>
/// Fixed-capacity point history for one trace. Appending beyond capacity overwrites the oldest point, so a
/// long session keeps bounded memory. NaN Y values break the line (invalid samples / gaps).
/// Only touched on the UI thread.
/// </summary>
public sealed class ChartSeries
{
    private readonly double[] _x;
    private readonly double[] _y;
    private int _start;

    public ChartSeries(int capacity)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _x = new double[capacity];
        _y = new double[capacity];
    }

    public int Capacity => _x.Length;
    public int Count { get; private set; }

    /// <summary>Incremented on every change; the view redraws only when it differs.</summary>
    public long Version { get; private set; }

    public void Add(double x, double y)
    {
        int idx = (_start + Count) % Capacity;
        _x[idx] = x;
        _y[idx] = y;
        if (Count < Capacity) Count++;
        else _start = (_start + 1) % Capacity;
        Version++;
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
        Version++;
    }

    public (double X, double Y) this[int i]
    {
        get
        {
            if ((uint)i >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(i));
            int idx = (_start + i) % Capacity;
            return (_x[idx], _y[idx]);
        }
    }

    public IEnumerable<(double X, double Y)> Points()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
}
