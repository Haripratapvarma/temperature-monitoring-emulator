using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TempLab.Presentation;

namespace TempLab.DesktopApp.Controls;

/// <summary>
/// Lightweight multi-channel trend chart. Draws each selected channel's raw trace (thin) and rolling mean
/// (thick) from the bounded <see cref="ChartSeries"/> buffers, one StreamGeometry per trace. It redraws only
/// when <see cref="Refresh"/> is called and a series version changed, so render cost is decoupled from the
/// sample rate.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty ChannelsProperty = DependencyProperty.Register(
        nameof(Channels), typeof(IEnumerable), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowRawProperty = DependencyProperty.Register(
        nameof(ShowRaw), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowMeanProperty = DependencyProperty.Register(
        nameof(ShowMean), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x1F, 0x77, 0xB4),
        Color.FromRgb(0xD6, 0x27, 0x28),
        Color.FromRgb(0x2C, 0xA0, 0x2C),
        Color.FromRgb(0x94, 0x67, 0xBD),
        Color.FromRgb(0xFF, 0x7F, 0x0E),
        Color.FromRgb(0x17, 0xBE, 0xCF),
    ];

    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA)), 1));
    private static readonly Pen AxisPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA6, 0xB0)), 1));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private readonly Dictionary<int, (Pen Raw, Pen Mean)> _pens = new();
    private long _lastVersionSum = -1;

    public IEnumerable? Channels
    {
        get => (IEnumerable?)GetValue(ChannelsProperty);
        set => SetValue(ChannelsProperty, value);
    }

    public bool ShowRaw
    {
        get => (bool)GetValue(ShowRawProperty);
        set => SetValue(ShowRawProperty, value);
    }

    public bool ShowMean
    {
        get => (bool)GetValue(ShowMeanProperty);
        set => SetValue(ShowMeanProperty, value);
    }

    private static Pen Frozen(Pen p)
    {
        p.Freeze();
        return p;
    }

    private (Pen Raw, Pen Mean) PensFor(int channel)
    {
        if (_pens.TryGetValue(channel, out var p)) return p;
        var c = Palette[channel % Palette.Length];
        var raw = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, c.R, c.G, c.B)), 1));
        var mean = Frozen(new Pen(new SolidColorBrush(c), 2.2));
        p = (raw, mean);
        _pens[channel] = p;
        return p;
    }

    private IEnumerable<ChannelViewModel> Visible() =>
        Channels?.OfType<ChannelViewModel>().Where(c => c.IsSelected) ?? Enumerable.Empty<ChannelViewModel>();

    /// <summary>Called by the window's UI tick.</summary>
    public void Refresh()
    {
        long sum = 0;
        foreach (var c in Visible()) sum += c.Raw.Version + c.Mean.Version + c.Id * 7919;
        if (sum == _lastVersionSum) return;
        _lastVersionSum = sum;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.White, null, bounds);
        const double left = 52, right = 12, top = 10, bottom = 26;
        var plot = new Rect(left, top, Math.Max(1, ActualWidth - left - right), Math.Max(1, ActualHeight - top - bottom));

        var channels = Visible().ToList();
        double xMin = double.MaxValue, xMax = double.MinValue, yMin = double.MaxValue, yMax = double.MinValue;
        foreach (var c in channels)
        {
            foreach (var s in new[] { c.Raw, c.Mean })
            {
                for (int i = 0; i < s.Count; i++)
                {
                    var (x, y) = s[i];
                    if (x < xMin) xMin = x;
                    if (x > xMax) xMax = x;
                    if (double.IsFinite(y))
                    {
                        if (y < yMin) yMin = y;
                        if (y > yMax) yMax = y;
                    }
                }
            }
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (xMin > xMax || yMin > yMax)
        {
            DrawLabel(dc, "No data — connect and start acquisition, or replay a recorded session.",
                new Point(plot.Left + 8, plot.Top + 8), dpi);
            dc.DrawRectangle(null, AxisPen, plot);
            return;
        }
        if (xMax - xMin < 1) xMax = xMin + 1;
        double pad = Math.Max(0.5, (yMax - yMin) * 0.08);
        yMin -= pad;
        yMax += pad;

        // grid + labels
        for (int i = 0; i <= 5; i++)
        {
            double yv = yMin + (yMax - yMin) * i / 5;
            double py = plot.Bottom - plot.Height * i / 5;
            dc.DrawLine(GridPen, new Point(plot.Left, py), new Point(plot.Right, py));
            DrawLabel(dc, yv.ToString("F1", CultureInfo.CurrentCulture) + " °C", new Point(2, py - 8), dpi);
        }
        // Whole seconds repeat labels on short spans (e.g. 0 s, 1 s, 2 s, 2 s, 3 s); use tenths below 12 s.
        string xFormat = xMax - xMin < 12 ? "F1" : "F0";
        for (int i = 0; i <= 6; i++)
        {
            double xv = xMin + (xMax - xMin) * i / 6;
            double px = plot.Left + plot.Width * i / 6;
            dc.DrawLine(GridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
            DrawLabel(dc, xv.ToString(xFormat, CultureInfo.CurrentCulture) + " s", new Point(px - 10, plot.Bottom + 4), dpi);
        }
        dc.DrawRectangle(null, AxisPen, plot);

        Point Map(double x, double y) => new(
            plot.Left + (x - xMin) / (xMax - xMin) * plot.Width,
            plot.Bottom - (y - yMin) / (yMax - yMin) * plot.Height);

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var c in channels)
        {
            var pens = PensFor(c.Id);
            if (ShowRaw) DrawSeries(dc, c.Raw, pens.Raw, Map);
            if (ShowMean) DrawSeries(dc, c.Mean, pens.Mean, Map);
        }
        dc.Pop();

        // legend
        double lx = plot.Left + 8;
        foreach (var c in channels)
        {
            var pens = PensFor(c.Id);
            dc.DrawLine(pens.Mean, new Point(lx, plot.Top + 12), new Point(lx + 18, plot.Top + 12));
            DrawLabel(dc, c.Name, new Point(lx + 22, plot.Top + 4), dpi);
            lx += 70;
        }
    }

    private static void DrawSeries(DrawingContext dc, ChartSeries s, Pen pen, Func<double, double, Point> map)
    {
        if (s.Count < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool open = false;
            for (int i = 0; i < s.Count; i++)
            {
                var (x, y) = s[i];
                if (!double.IsFinite(y))
                {
                    open = false; // invalid sample or gap breaks the line
                    continue;
                }
                var p = map(x, y);
                if (!open)
                {
                    ctx.BeginFigure(p, false, false);
                    open = true;
                }
                else
                {
                    ctx.LineTo(p, true, false);
                }
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static void DrawLabel(DrawingContext dc, string text, Point at, double pixelsPerDip)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11,
            Brushes.DimGray, pixelsPerDip);
        dc.DrawText(ft, at);
    }
}
