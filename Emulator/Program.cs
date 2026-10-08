using System.Net;
using System.Text.Json;
using TempLab.Emulator;
using TempLab.Protocol;

// Usage: TempLab.Emulator [--host 127.0.0.1] [--port 5055] [--scenario faults.json] [--log emulator.log] [--debug]
string host = "127.0.0.1";
int port = 5055;
string? scenarioPath = null;
string? logPath = null;
bool debug = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--scenario": scenarioPath = args[++i]; break;
        case "--log": logPath = args[++i]; break;
        case "--debug": debug = true; break;
        case "-h" or "--help":
            Console.WriteLine("TempLab.Emulator [--host 127.0.0.1] [--port 5055] [--scenario file.json] [--log file] [--debug]");
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]}");
            return 2;
    }
}

var sinks = new List<ILogSink> { new ConsoleLogSink() };
FileLogSink? fileSink = logPath is null ? null : new FileLogSink(logPath);
if (fileSink is not null) sinks.Add(fileSink);
var log = new StructuredLogger("emulator", sinks.ToArray()) { MinimumLevel = debug ? LogLevel.Debug : LogLevel.Info };

FaultScenario? scenario = null;
if (scenarioPath is not null)
{
    scenario = JsonSerializer.Deserialize<FaultScenario>(File.ReadAllText(scenarioPath), ProtocolJson.Options);
    var problem = scenario?.Validate() ?? "empty scenario file";
    if (scenario is null || scenario.Validate() is not null)
    {
        Console.Error.WriteLine($"invalid scenario: {problem}");
        return 2;
    }
    log.Info("fault_scenario_loaded", ("path", scenarioPath), ("scenario", scenario.Name), ("faults", scenario.Faults.Count));
}

var server = new EmulatorServer(IPAddress.Parse(host), port, log, scenario);
server.Start();
Console.WriteLine($"TempLab emulator listening on {server.EndPoint}. Press Ctrl+C to stop.");

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    done.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.TrySetResult();
await done.Task;
Console.WriteLine("shutting down...");
await server.DisposeAsync();
fileSink?.Dispose();
return 0;
