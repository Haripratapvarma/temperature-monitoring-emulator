using System.Globalization;
using System.Text.Json;
using TempLab.Client;
using TempLab.Protocol;

// Console client: proves the protocol independently of WPF.
//   interactive: TempLab.ConsoleClient [--host h] [--port p]
//   scripted:    TempLab.ConsoleClient --script "connect; configure 10 42; start; sleep 2; status; stop; quit"
string host = "127.0.0.1";
int port = 5055;
string? script = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--script": script = args[++i]; break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}

var log = new StructuredLogger("console-client", new ConsoleLogSink()) { MinimumLevel = LogLevel.Warn };
await using var client = new InstrumentClient(new ClientOptions { Host = host, Port = port }, log);

long samples = 0, batches = 0;
var lastSeq = new Dictionary<int, long>();
var lastValue = new Dictionary<int, double>();
var statsLock = new object();
client.SamplesReceived += b =>
{
    lock (statsLock)
    {
        batches++;
        foreach (var s in b.Samples)
        {
            samples++;
            lastSeq[s.Channel] = s.Sequence;
            lastValue[s.Channel] = s.Value;
        }
    }
};
client.DeviceFaultReceived += f => Console.WriteLine($"! device fault {f.Code}: {f.Message}");
client.Disconnected += d =>
{
    if (d.Kind != DisconnectKind.ClientRequested) Console.WriteLine($"! disconnected ({d.Kind}): {d.Reason}");
};

void Help() => Console.WriteLine("""
    commands:
      connect                       connect and print device status
      reconnect                     reconnect with backoff, then reconcile
      configure [rateHz] [seed]     send default 4-channel configuration
      start | stop | status | reset
      faults <scenario.json>        load a fault scenario (Idle only)
      watch                         print received sample counters and latest values
      sleep <seconds>
      disconnect | quit
    """);

string? currentAcquisition = null;
var json = new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true };

async Task<bool> RunAsync(string line)
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) return true;
    try
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "help": Help(); break;
            case "connect":
                Console.WriteLine(JsonSerializer.Serialize(await client.ConnectAsync(), json));
                break;
            case "reconnect":
            {
                var st = await client.ReconnectAsync();
                Console.WriteLine($"reconnected; device state {st.State}");
                if (currentAcquisition is not null)
                    Console.WriteLine($"reconcile: {AcquisitionReconciler.Reconcile(st, currentAcquisition)}");
                break;
            }
            case "configure":
            {
                double rate = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 10;
                int seed = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 42;
                await client.ConfigureAsync(new DeviceConfiguration { RateHz = rate, Seed = seed });
                Console.WriteLine($"configured rate={rate} seed={seed}");
                break;
            }
            case "start":
                currentAcquisition = (await client.StartAsync()).AcquisitionId;
                Console.WriteLine($"started acquisition {currentAcquisition}");
                break;
            case "stop":
            {
                var r = await client.StopAsync();
                Console.WriteLine($"stopped (wasAcquiring={r.WasAcquiring}, samplesPerChannel={r.SamplesSentPerChannel})");
                break;
            }
            case "status":
                Console.WriteLine(JsonSerializer.Serialize(await client.GetStatusAsync(), json));
                break;
            case "reset":
                await client.ResetAsync();
                Console.WriteLine("reset");
                break;
            case "faults":
            {
                var sc = JsonSerializer.Deserialize<FaultScenario>(await File.ReadAllTextAsync(parts[1]), ProtocolJson.Options)!;
                await client.SetFaultsAsync(sc);
                Console.WriteLine($"loaded scenario '{sc.Name}' ({sc.Faults.Count} faults)");
                break;
            }
            case "watch":
                lock (statsLock)
                {
                    Console.WriteLine($"batches={batches} samples={samples}");
                    foreach (var ch in lastSeq.Keys.Order())
                        Console.WriteLine($"  ch{ch}: seq={lastSeq[ch]} value={lastValue[ch].ToString("F3", CultureInfo.InvariantCulture)} °C");
                }
                break;
            case "sleep":
                await Task.Delay(TimeSpan.FromSeconds(double.Parse(parts[1], CultureInfo.InvariantCulture)));
                break;
            case "disconnect":
                await client.DisconnectAsync();
                Console.WriteLine("disconnected");
                break;
            case "quit" or "exit":
                return false;
            default:
                Console.WriteLine($"unknown command '{parts[0]}' (try help)");
                break;
        }
    }
    catch (InstrumentException ex)
    {
        Console.WriteLine($"error: {ex.Message}");
        if (script is not null) throw;
    }
    return true;
}

if (script is not null)
{
    try
    {
        foreach (var cmd in script.Split(';'))
        {
            Console.WriteLine($"> {cmd.Trim()}");
            if (!await RunAsync(cmd)) break;
        }
        return 0;
    }
    catch (InstrumentException)
    {
        return 1;
    }
}

Console.WriteLine($"TempLab console client ({host}:{port}). Type 'help'.");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null || !await RunAsync(line)) break;
}
return 0;
