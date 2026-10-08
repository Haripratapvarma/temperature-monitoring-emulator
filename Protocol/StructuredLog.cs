using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace TempLab.Protocol;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public sealed record LogEntry(DateTime TimestampUtc, LogLevel Level, string Component, string Event,
    IReadOnlyDictionary<string, object?> Fields)
{
    public string ToJsonLine()
    {
        var d = new Dictionary<string, object?>
        {
            ["ts"] = TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["level"] = Level.ToString().ToLowerInvariant(),
            ["component"] = Component,
            ["event"] = Event,
        };
        foreach (var kv in Fields) d[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(d, ProtocolJson.Options);
    }
}

public interface ILogSink
{
    void Write(LogEntry entry);
}

/// <summary>
/// Small structured logger: one JSON object per line with UTC timestamp, component, event and correlation
/// fields (requestId, acquisitionId, sessionId ...). Thread-safe; sinks must be thread-safe.
/// </summary>
public sealed class StructuredLogger(string component, params ILogSink[] sinks)
{
    public static readonly StructuredLogger Null = new("null");

    public string Component { get; } = component;
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public StructuredLogger ForComponent(string component) => new(component, sinks) { MinimumLevel = MinimumLevel };

    public void Log(LogLevel level, string evt, params (string Key, object? Value)[] fields)
    {
        if (level < MinimumLevel || sinks.Length == 0) return;
        var dict = new Dictionary<string, object?>(fields.Length);
        foreach (var (k, v) in fields) dict[k] = v;
        var entry = new LogEntry(DateTime.UtcNow, level, Component, evt, dict);
        foreach (var s in sinks)
        {
            try { s.Write(entry); }
            catch { /* logging must never break the instrument */ }
        }
    }

    public void Debug(string evt, params (string, object?)[] f) => Log(LogLevel.Debug, evt, f);
    public void Info(string evt, params (string, object?)[] f) => Log(LogLevel.Info, evt, f);
    public void Warn(string evt, params (string, object?)[] f) => Log(LogLevel.Warn, evt, f);
    public void Error(string evt, params (string, object?)[] f) => Log(LogLevel.Error, evt, f);
}

public sealed class ConsoleLogSink : ILogSink
{
    private readonly object _gate = new();
    public void Write(LogEntry entry)
    {
        lock (_gate) Console.Error.WriteLine(entry.ToJsonLine());
    }
}

public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLogSink(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Write(LogEntry entry)
    {
        lock (_gate) _writer.WriteLine(entry.ToJsonLine());
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}

/// <summary>Keeps entries in memory; used by tests to assert on correlated log events.</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    public void Write(LogEntry entry) => _entries.Enqueue(entry);
    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();
    public IEnumerable<LogEntry> WithEvent(string evt) => _entries.Where(e => e.Event == evt);
}
