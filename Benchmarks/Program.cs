using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using TempLab.Acquisition;
using TempLab.Acquisition.Processing;
using TempLab.Acquisition.Sources;
using TempLab.Client;
using TempLab.Emulator;
using TempLab.Protocol;

// Sustained-run benchmark. Measures throughput, sample loss, processing latency and memory trend, and prints a
// Markdown report including machine, workload and configuration.
//
//   TempLab.Benchmarks sustained [--channels 4] [--rate 10] [--seconds 60] [--processor Native|ManagedReference]
//                                [--queue 256] [--host h --port p]   (external emulator; default: in-process on localhost)
//   TempLab.Benchmarks processors [--samples 10000000] [--window 20]
var inv = CultureInfo.InvariantCulture;
string mode = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "sustained";
string Opt(string name, string def)
{
    int i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

static double Percentile(List<double> sorted, double p) =>
    sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];

static string Machine() =>
    $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; {Environment.ProcessorCount} logical CPUs; " +
    $".NET {Environment.Version}; GC {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}";

if (mode == "processors")
{
    int samples = int.Parse(Opt("samples", "10000000"), inv);
    int window = int.Parse(Opt("window", "20"), inv);
    const int channels = 4, batch = 4096;
    var ch = new int[batch];
    var vals = new double[batch];
    var res = new RollingStats[batch];
    var rng = new Random(1);
    for (int i = 0; i < batch; i++)
    {
        ch[i] = i % channels;
        vals[i] = 25 + rng.NextDouble();
    }
    Console.WriteLine($"## Processor throughput (window {window}, batch {batch}, {samples:N0} samples)\n");
    Console.WriteLine($"Machine: {Machine()}\n");
    Console.WriteLine("| Processor | Samples/s | ns/sample |\n|---|---:|---:|");
    foreach (var kind in new[] { ProcessorKind.Native, ProcessorKind.ManagedReference })
    {
        using var p = RollingStatsProcessorFactory.Create(kind, channels, window);
        int n = kind == ProcessorKind.Native ? samples : Math.Min(samples, 2_000_000);
        p.PushBatch(ch, vals, res); // warm-up
        var timer = Stopwatch.StartNew();
        for (int done = 0; done < n; done += batch) p.PushBatch(ch, vals, res);
        double secs = timer.Elapsed.TotalSeconds;
        Console.WriteLine($"| {kind} | {n / secs:N0} | {secs * 1e9 / n:F1} |");
    }
    return 0;
}

if (mode == "record")
{
    // Produces an example session: live acquisition from an in-process emulator, recorded to CSV,
    // then exported through the same pipeline. Used for Docs/examples.
    string outPath = Opt("out", "session.csv");
    double secs = double.Parse(Opt("seconds", "30"), inv);
    await using var emu = new EmulatorServer(IPAddress.Loopback, 0, StructuredLogger.Null);
    emu.Start();
    await using var cl = new InstrumentClient(new ClientOptions { Port = emu.EndPoint.Port });
    await cl.ConnectAsync();
    await using var eng = new AcquisitionEngine();
    await eng.StartAsync(new EmulatorSource(cl), new AcquisitionSettings { RecordingPath = outPath });
    await Task.Delay(TimeSpan.FromSeconds(secs));
    await eng.StopAsync();
    var exported = Path.ChangeExtension(outPath, null) + "-processed.csv";
    var r = await TempLab.Acquisition.Export.ProcessedExporter.ExportAsync(outPath, exported);
    Console.WriteLine($"recorded {eng.Counters.Processed} samples to {outPath}; exported {r.Rows} rows ({r.Alerts} alert rows) to {exported}");
    return 0;
}

int channelsArg = int.Parse(Opt("channels", "4"), inv);
double rate = double.Parse(Opt("rate", "10"), inv);
double seconds = double.Parse(Opt("seconds", "60"), inv);
var processor = Enum.Parse<ProcessorKind>(Opt("processor", "Native"));
int queue = int.Parse(Opt("queue", "256"), inv);
string? host = Opt("host", "");
int port = int.Parse(Opt("port", "0"), inv);

EmulatorServer? server = null;
if (string.IsNullOrEmpty(host))
{
    server = new EmulatorServer(IPAddress.Loopback, 0, StructuredLogger.Null);
    server.Start();
    host = "127.0.0.1";
    port = server.EndPoint.Port;
}

var settings = new AcquisitionSettings
{
    Channels = Enumerable.Range(0, channelsArg).ToArray(),
    RateHz = rate,
    Seed = 42,
    WindowSize = 20,
    QueueCapacityBatches = queue,
    Processor = processor,
    Alerts = new Dictionary<int, ChannelAlertSettings>(),
};

await using var client = new InstrumentClient(new ClientOptions { Host = host, Port = port });
await client.ConnectAsync();
var latencies = new List<double>(1 << 16);
await using var engine = new AcquisitionEngine { LatencyObserver = ms => latencies.Add(ms) };
using var proc = Process.GetCurrentProcess();

var memory = new List<(double T, long Managed, long WorkingSet)>();
GC.Collect();
var sw = Stopwatch.StartNew();
await engine.StartAsync(new EmulatorSource(client), settings);
var drain = new List<ProcessedSample>(100_000);
while (sw.Elapsed.TotalSeconds < seconds)
{
    await Task.Delay(100);
    drain.Clear();
    engine.DrainDisplay(drain); // behave like the UI tick
    if (memory.Count == 0 || sw.Elapsed.TotalSeconds - memory[^1].T >= 1.0)
    {
        proc.Refresh();
        memory.Add((sw.Elapsed.TotalSeconds, GC.GetTotalMemory(false), proc.WorkingSet64));
    }
}
await engine.StopAsync();
double elapsed = sw.Elapsed.TotalSeconds;
var c = engine.Counters;
latencies.Sort();

// Memory trend: least-squares slope of managed heap over the second half of the run (ignores start-up).
var half = memory.Where(m => m.T >= elapsed / 2).ToList();
double slope = 0;
if (half.Count >= 3)
{
    double mx = half.Average(m => m.T), my = half.Average(m => (double)m.Managed);
    slope = half.Sum(m => (m.T - mx) * (m.Managed - my)) / half.Sum(m => (m.T - mx) * (m.T - mx));
}

Console.WriteLine($"## Sustained run: {channelsArg} ch × {rate.ToString(inv)} Hz × {seconds.ToString(inv)} s ({processor})\n");
Console.WriteLine($"- Machine: {Machine()}");
Console.WriteLine($"- Emulator: {(server is null ? $"external {host}:{port}" : "in-process, localhost TCP")}; batch interval 50 ms; queue {queue} batches; window {settings.WindowSize}");
Console.WriteLine($"- UI-like drain every 100 ms; no recording\n");
Console.WriteLine("| Metric | Value |\n|---|---:|");
Console.WriteLine($"| Elapsed | {elapsed:F1} s |");
Console.WriteLine($"| Samples received | {c.Received:N0} |");
Console.WriteLine($"| Samples processed | {c.Processed:N0} |");
Console.WriteLine($"| Throughput (processed) | {c.Processed / elapsed:N0} samples/s |");
Console.WriteLine($"| Expected rate | {channelsArg * rate:N0} samples/s |");
Console.WriteLine($"| Dropped (queue full) | {c.Dropped:N0} |");
Console.WriteLine($"| Sequence gaps | {c.GapSamples:N0} |");
Console.WriteLine($"| Batches measured | {latencies.Count:N0} |");
Console.WriteLine($"| Processing latency p50 / p99 / max | {Percentile(latencies, 50):F3} / {Percentile(latencies, 99):F3} / {(latencies.Count > 0 ? latencies[^1] : double.NaN):F3} ms |");
Console.WriteLine($"| Managed heap start → end | {memory[0].Managed / 1048576.0:F1} → {memory[^1].Managed / 1048576.0:F1} MiB |");
Console.WriteLine($"| Managed heap slope (2nd half) | {slope / 1024:F1} KiB/s |");
Console.WriteLine($"| Working set start → end | {memory[0].WorkingSet / 1048576.0:F1} → {memory[^1].WorkingSet / 1048576.0:F1} MiB |");
Console.WriteLine($"| Gen0/1/2 collections | {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} |");
Console.WriteLine();
Console.WriteLine("Latency = time from a batch being received by the client to the processing worker finishing it (queue wait + processing).");

if (server is not null) await server.DisposeAsync();
return c.Dropped == 0 && c.GapSamples == 0 ? 0 : 1;
