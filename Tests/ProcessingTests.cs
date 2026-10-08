using System.Globalization;
using System.Runtime.InteropServices;
using TempLab.Acquisition;
using TempLab.Acquisition.Processing;
using TempLab.Acquisition.Recording;
using TempLab.Protocol;

namespace TempLab.Tests;

public class RollingStatsTests
{
    [Fact]
    public void Native_struct_layout_matches_the_C_header() =>
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.NativeStats>());

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(20)]
    [InlineData(257)]
    public void Native_matches_managed_reference_across_the_boundary(int window)
    {
        using var native = new NativeRollingStatsProcessor(4, window);
        using var reference = new ManagedReferenceProcessor(4, window);
        var rng = new Random(window);
        const int n = 5000;
        var ch = new int[n];
        var v = new double[n];
        for (int i = 0; i < n; i++)
        {
            ch[i] = rng.Next(4);
            v[i] = rng.Next(60) == 0 ? double.NaN : rng.Next(80) == 0 ? 2500 : 25 + rng.NextDouble() * 10;
        }
        var a = new RollingStats[n];
        var b = new RollingStats[n];
        native.PushBatch(ch, v, a);
        reference.PushBatch(ch, v, b);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(b[i].Count, a[i].Count);
            Assert.Equal(b[i].Rejected, a[i].Rejected);
            Assert.Equal(b[i].IsWarm, a[i].IsWarm);
            Assert.Equal(b[i].Min, a[i].Min, 0.0);
            Assert.Equal(b[i].Max, a[i].Max, 0.0);
            Assert.Equal(b[i].Mean, a[i].Mean, 1e-9);
        }
    }

    [Fact]
    public void Known_sequence_window_warm_up_and_reset()
    {
        using var p = new NativeRollingStatsProcessor(1, 3);
        var r = new RollingStats[5];
        p.PushBatch([0, 0, 0, 0, 0], [1.0, 2.0, 6.0, 4.0, double.NaN], r);
        Assert.False(r[1].IsWarm);
        Assert.True(r[2].IsWarm);
        Assert.Equal(3.0, r[2].Mean, 1e-15);
        Assert.Equal(4.0, r[3].Mean, 1e-15);
        Assert.Equal(2.0, r[3].Min, 0.0);
        Assert.True(r[4].Rejected);
        Assert.Equal(4.0, r[4].Mean, 1e-15); // last valid statistics retained
        p.Reset();
        Assert.Equal(0, p.Get(0).Count);
    }

    [Fact]
    public void Native_errors_surface_as_exceptions_with_status_codes()
    {
        Assert.Throws<NativeProcessingException>(() => new NativeRollingStatsProcessor(0, 5));
        using var p = new NativeRollingStatsProcessor(2, 5);
        var ex = Assert.Throws<NativeProcessingException>(() => p.PushBatch([5], [1.0], new RollingStats[1]));
        Assert.Equal(3, ex.Status); // TL_ERR_CHANNEL_OUT_OF_RANGE
        Assert.Throws<NativeProcessingException>(() => p.Get(9));
    }

    [Fact]
    public void Handle_is_released_deterministically()
    {
        var p = new NativeRollingStatsProcessor(2, 5);
        p.Dispose();
        p.Dispose(); // idempotent
        Assert.Throws<ObjectDisposedException>(() => p.Get(0));
    }
}

public class AlertEvaluatorTests
{
    [Fact]
    public void Continuously_high_reading_raises_exactly_one_alert()
    {
        var e = new AlertEvaluator(new ChannelAlertSettings(null, 30), 0.5);
        Assert.Equal(AlertFlags.None, e.Evaluate(29));
        Assert.Equal(AlertFlags.HighRaised, e.Evaluate(31));
        for (int i = 0; i < 100; i++) Assert.Equal(AlertFlags.None, e.Evaluate(35));
    }

    [Fact]
    public void Re_arms_only_below_threshold_minus_hysteresis()
    {
        var e = new AlertEvaluator(new ChannelAlertSettings(null, 30), 0.5);
        e.Evaluate(31);
        Assert.Equal(AlertFlags.None, e.Evaluate(29.8)); // inside hysteresis band: still disarmed
        Assert.Equal(AlertFlags.None, e.Evaluate(30.1));
        Assert.False(e.HighArmed);
        e.Evaluate(29.4);                                 // below 29.5: re-armed
        Assert.True(e.HighArmed);
        Assert.Equal(AlertFlags.HighRaised, e.Evaluate(30.01));
    }

    [Fact]
    public void Low_threshold_is_symmetric()
    {
        var e = new AlertEvaluator(new ChannelAlertSettings(20, null), 1.0);
        Assert.Equal(AlertFlags.LowRaised, e.Evaluate(19.9));
        Assert.Equal(AlertFlags.None, e.Evaluate(15));
        Assert.Equal(AlertFlags.None, e.Evaluate(20.5));
        e.Evaluate(21.1);
        Assert.Equal(AlertFlags.LowRaised, e.Evaluate(19));
    }

    [Fact]
    public void Value_exactly_at_threshold_does_not_alert_and_nan_is_ignored()
    {
        var e = new AlertEvaluator(new ChannelAlertSettings(10, 30), 0.5);
        Assert.Equal(AlertFlags.None, e.Evaluate(30));
        Assert.Equal(AlertFlags.None, e.Evaluate(10));
        Assert.Equal(AlertFlags.None, e.Evaluate(double.NaN));
        Assert.True(e.HighArmed && e.LowArmed);
    }
}

public class CsvSessionTests
{
    [Fact]
    public void Round_trips_exactly_under_a_comma_decimal_culture()
    {
        using var dir = new TempDir();
        var path = dir.File("s.csv");
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // "1,5" decimal separator
            var meta = new SessionMetadata { Source = "unit test", Settings = new AcquisitionSettings { WindowSize = 7, RateHz = 12.5 } };
            var samples = new[]
            {
                new Sample(0, 0, 0.0, 25.123456789012345, SampleQuality.Good),
                new Sample(3, 1, 0.08, -273.15, SampleQuality.Suspect),
                new Sample(1, 2, 0.16, double.NaN, SampleQuality.Invalid),
                new Sample(2, 3, 1e-7, 1999.9999999999998, SampleQuality.Good),
            };
            using (var w = new CsvSessionWriter(path, meta))
            {
                foreach (var s in samples) w.Write("acq1", s);
                w.WriteEvent("gap", ("channel", 1), ("missing", 4));
            }
            Assert.DoesNotContain(";", File.ReadAllText(path));
            var back = CsvSessionReader.Read(path);
            Assert.Equal(meta.SessionId, back.Metadata.SessionId);
            Assert.Equal(7, back.Metadata.Settings.WindowSize);
            Assert.Equal(12.5, back.Metadata.Settings.RateHz, 0.0);
            Assert.Equal(samples.Length, back.Rows.Count);
            for (int i = 0; i < samples.Length; i++)
            {
                var r = back.Rows[i].Sample;
                Assert.Equal(samples[i].Channel, r.Channel);
                Assert.Equal(samples[i].Sequence, r.Sequence);
                Assert.Equal(samples[i].Quality, r.Quality);
                Assert.True(samples[i].Value.Equals(r.Value), $"row {i}: {r.Value:R}"); // bit-exact, NaN included
                Assert.True(samples[i].ElapsedSeconds.Equals(r.ElapsedSeconds));
            }
            var gap = Assert.Single(back.Events);
            Assert.Equal("gap", gap.Kind);
            Assert.Equal("4", gap.Fields["missing"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Malformed_rows_report_the_line_number()
    {
        var text = "# templab-session v1\n" + "session_id,acquisition_id,channel_id,sequence,elapsed_s,temperature_c,quality\n"
                   + "00000000-0000-0000-0000-000000000000,a,0,0,0,25,Good\n"
                   + "00000000-0000-0000-0000-000000000000,a,0,x,0,25,Good\n";
        var ex = Assert.Throws<CsvFormatException>(() => CsvSessionReader.Read(new StringReader(text)));
        Assert.Equal(4, ex.Line);
        Assert.Throws<CsvFormatException>(() => CsvSessionReader.Read(new StringReader("# nothing\n")));
    }
}
