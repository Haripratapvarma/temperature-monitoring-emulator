using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TempLab.Acquisition.Processing;

/// <summary>Per-channel rolling statistics. Not thread-safe: owned by the processing worker.</summary>
public interface IRollingStatsProcessor : IDisposable
{
    int ChannelCount { get; }
    int WindowSize { get; }

    /// <summary>
    /// Pushes values in order. Values that violate the invalid-data policy (NaN, ±∞, outside
    /// [-273.15, 2000] °C) are rejected and the previous statistics are returned with Rejected = true.
    /// </summary>
    void PushBatch(ReadOnlySpan<int> channels, ReadOnlySpan<double> values, Span<RollingStats> results);

    RollingStats Get(int channel);
    void Reset(int channel = -1);
}

public static class RollingStatsProcessorFactory
{
    public static IRollingStatsProcessor Create(ProcessorKind kind, int channels, int window) => kind switch
    {
        ProcessorKind.Native => new NativeRollingStatsProcessor(channels, window),
        ProcessorKind.ManagedReference => new ManagedReferenceProcessor(channels, window),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed class NativeProcessingException(int status, string operation)
    : Exception($"{operation} failed: {NativeMethods.Describe(status)} (status {status})")
{
    public int Status { get; } = status;
}

/// <summary>Owns a native tl_processor* — a real native handle, so a SafeHandle is appropriate here.</summary>
internal sealed class RollingStatsSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public RollingStatsSafeHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => NativeMethods.tl_destroy(handle) == NativeMethods.TL_OK;
}

internal static class NativeMethods
{
    private const string Lib = "templab_native";

    public const int TL_OK = 0;
    public const int TL_ERR_BUFFER_TOO_SMALL = 4;
    public const int TL_INVALID_VALUE = 7;

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeStats
    {
        public double Mean;
        public double Min;
        public double Max;
        public int Count;
        public int WindowSize;
        public int IsWarm;
        public int LastStatus;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int tl_create(int channelCount, int windowSize, out RollingStatsSafeHandle processor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int tl_destroy(IntPtr processor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int tl_push_batch(RollingStatsSafeHandle processor, int[] channels, double[] values, int count,
        [Out] NativeStats[] outStats, int outCapacity, out int invalidCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int tl_get_stats(RollingStatsSafeHandle processor, int channel, out NativeStats stats);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int tl_reset(RollingStatsSafeHandle processor, int channel);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr tl_status_message(int status);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int tl_abi_version();

    public static string Describe(int status)
    {
        try { return Marshal.PtrToStringUTF8(tl_status_message(status)) ?? "unknown"; }
        catch (DllNotFoundException) { return "native library not found"; }
    }
}

/// <summary>
/// P/Invoke wrapper around templab_native. Uses tl_push_batch with reusable pinned-by-marshaller arrays,
/// so one batch costs one native transition. Every status code is checked; no exception crosses into C++.
/// </summary>
public sealed class NativeRollingStatsProcessor : IRollingStatsProcessor
{
    private readonly RollingStatsSafeHandle _handle;
    private int[] _channels = new int[256];
    private double[] _values = new double[256];
    private NativeMethods.NativeStats[] _out = new NativeMethods.NativeStats[256];

    public const int ExpectedAbiVersion = 1;

    public NativeRollingStatsProcessor(int channels, int window)
    {
        int abi;
        try
        {
            abi = NativeMethods.tl_abi_version();
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "templab_native was not found. Build NativeProcessing with CMake first (see README).", ex);
        }
        if (abi != ExpectedAbiVersion) throw new InvalidOperationException($"templab_native ABI {abi}, expected {ExpectedAbiVersion}");
        int st = NativeMethods.tl_create(channels, window, out _handle);
        if (st != NativeMethods.TL_OK)
        {
            _handle.Dispose();
            throw new NativeProcessingException(st, "tl_create");
        }
        ChannelCount = channels;
        WindowSize = window;
    }

    public int ChannelCount { get; }
    public int WindowSize { get; }

    public void PushBatch(ReadOnlySpan<int> channels, ReadOnlySpan<double> values, Span<RollingStats> results)
    {
        int n = channels.Length;
        if (values.Length != n || results.Length < n) throw new ArgumentException("span lengths do not match");
        if (n == 0) return;
        if (_channels.Length < n)
        {
            int size = Math.Max(n, _channels.Length * 2);
            _channels = new int[size];
            _values = new double[size];
            _out = new NativeMethods.NativeStats[size];
        }
        channels.CopyTo(_channels);
        values.CopyTo(_values);
        int st = NativeMethods.tl_push_batch(_handle, _channels, _values, n, _out, _out.Length, out _);
        if (st != NativeMethods.TL_OK) throw new NativeProcessingException(st, "tl_push_batch");
        for (int i = 0; i < n; i++) results[i] = Convert(_out[i]);
    }

    public RollingStats Get(int channel)
    {
        int st = NativeMethods.tl_get_stats(_handle, channel, out var s);
        if (st != NativeMethods.TL_OK) throw new NativeProcessingException(st, "tl_get_stats");
        return Convert(s);
    }

    public void Reset(int channel = -1)
    {
        int st = NativeMethods.tl_reset(_handle, channel);
        if (st != NativeMethods.TL_OK) throw new NativeProcessingException(st, "tl_reset");
    }

    private static RollingStats Convert(in NativeMethods.NativeStats s) =>
        new(s.Mean, s.Min, s.Max, s.Count, s.WindowSize, s.IsWarm != 0, s.LastStatus == NativeMethods.TL_INVALID_VALUE);

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// Deliberately naive O(window) implementation used as a reference in tests and benchmarks.
/// Same invalid-data policy as the native library.
/// </summary>
public sealed class ManagedReferenceProcessor : IRollingStatsProcessor
{
    public const double MinValidC = -273.15;
    public const double MaxValidC = 2000.0;

    private readonly Queue<double>[] _windows;

    public ManagedReferenceProcessor(int channels, int window)
    {
        if (channels < 1 || channels > 64) throw new ArgumentOutOfRangeException(nameof(channels));
        if (window < 1 || window > 100_000) throw new ArgumentOutOfRangeException(nameof(window));
        _windows = Enumerable.Range(0, channels).Select(_ => new Queue<double>(window)).ToArray();
        WindowSize = window;
    }

    public int ChannelCount => _windows.Length;
    public int WindowSize { get; }

    public static bool IsValid(double v) => double.IsFinite(v) && v >= MinValidC && v <= MaxValidC;

    public void PushBatch(ReadOnlySpan<int> channels, ReadOnlySpan<double> values, Span<RollingStats> results)
    {
        for (int i = 0; i < channels.Length; i++)
        {
            if ((uint)channels[i] >= (uint)_windows.Length) throw new ArgumentOutOfRangeException(nameof(channels));
        }
        for (int i = 0; i < channels.Length; i++)
        {
            var w = _windows[channels[i]];
            bool ok = IsValid(values[i]);
            if (ok)
            {
                w.Enqueue(values[i]);
                if (w.Count > WindowSize) w.Dequeue();
            }
            results[i] = Compute(w) with { Rejected = !ok };
        }
    }

    private RollingStats Compute(Queue<double> w)
    {
        if (w.Count == 0) return new RollingStats(double.NaN, double.NaN, double.NaN, 0, WindowSize, false, false);
        double sum = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in w)
        {
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return new RollingStats(sum / w.Count, min, max, w.Count, WindowSize, w.Count == WindowSize, false);
    }

    public RollingStats Get(int channel) => Compute(_windows[channel]);

    public void Reset(int channel = -1)
    {
        if (channel == -1)
            foreach (var w in _windows) w.Clear();
        else
            _windows[channel].Clear();
    }

    public void Dispose() { }
}
