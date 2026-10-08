using System.Globalization;
using System.Text;
using System.Text.Json;
using TempLab.Protocol;

namespace TempLab.Acquisition.Recording;

/// <summary>Metadata written as <c># key=value</c> lines before the CSV header.</summary>
public sealed record SessionMetadata
{
    public const string FormatTag = "templab-session v1";

    public Guid SessionId { get; init; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public string Source { get; init; } = "";
    public string ToolVersion { get; init; } = typeof(SessionMetadata).Assembly.GetName().Version?.ToString() ?? "0";
    public AcquisitionSettings Settings { get; init; } = new();
    public DeviceConfiguration? Device { get; init; }
}

public sealed record RecordedRow(Guid SessionId, string AcquisitionId, Sample Sample);

public sealed record RecordedEvent(string Kind, IReadOnlyDictionary<string, string> Fields);

public sealed record RecordedSession(SessionMetadata Metadata, IReadOnlyList<RecordedRow> Rows, IReadOnlyList<RecordedEvent> Events);

internal static class CsvFormat
{
    public const string Header = "session_id,acquisition_id,channel_id,sequence,elapsed_s,temperature_c,quality";
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly JsonSerializerOptions Json = new(ProtocolJson.Options) { WriteIndented = false };

    public static string D(double v) => v.ToString("R", Inv);
}

/// <summary>
/// Writes raw samples. Owned by the processing worker thread; not thread-safe.
/// Numbers use the invariant culture with round-trip formatting, so files are identical on every locale.
/// </summary>
public sealed class CsvSessionWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Guid _sessionId;
    private readonly StringBuilder _line = new(96);
    private long _rowsSinceFlush;

    public string Path { get; }
    public long RowsWritten { get; private set; }

    public CsvSessionWriter(string path, SessionMetadata metadata)
    {
        Path = path;
        _sessionId = metadata.SessionId;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024),
            new UTF8Encoding(false)) { NewLine = "\n" };
        _writer.WriteLine($"# {SessionMetadata.FormatTag}");
        _writer.WriteLine($"# session_id={metadata.SessionId:D}");
        _writer.WriteLine($"# created_utc={metadata.CreatedUtc.ToString("O", CsvFormat.Inv)}");
        _writer.WriteLine($"# source={Sanitize(metadata.Source)}");
        _writer.WriteLine($"# tool_version={metadata.ToolVersion}");
        _writer.WriteLine($"# settings={JsonSerializer.Serialize(metadata.Settings with { RecordingPath = null }, CsvFormat.Json)}");
        if (metadata.Device is not null)
            _writer.WriteLine($"# device={JsonSerializer.Serialize(metadata.Device, CsvFormat.Json)}");
        _writer.WriteLine(CsvFormat.Header);
    }

    private static string Sanitize(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

    public void Write(string acquisitionId, in Sample s)
    {
        _line.Clear();
        _line.Append(_sessionId.ToString("D")).Append(',')
            .Append(acquisitionId).Append(',')
            .Append(s.Channel.ToString(CsvFormat.Inv)).Append(',')
            .Append(s.Sequence.ToString(CsvFormat.Inv)).Append(',')
            .Append(CsvFormat.D(s.ElapsedSeconds)).Append(',')
            .Append(CsvFormat.D(s.Value)).Append(',')
            .Append(s.Quality.ToString());
        _writer.WriteLine(_line);
        RowsWritten++;
        if (++_rowsSinceFlush >= 2000)
        {
            _writer.Flush();
            _rowsSinceFlush = 0;
        }
    }

    /// <summary>Event lines are comments so other CSV tools skip them.</summary>
    public void WriteEvent(string kind, params (string Key, object? Value)[] fields)
    {
        var sb = new StringBuilder("# event=").Append(kind);
        foreach (var (k, v) in fields)
            sb.Append(' ').Append(k).Append('=').Append(Sanitize(Convert.ToString(v, CsvFormat.Inv) ?? "").Replace(' ', '_'));
        _writer.WriteLine(sb.ToString());
    }

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }
}

public sealed class CsvFormatException(string message, int line) : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}

public static class CsvSessionReader
{
    public static RecordedSession Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return Read(reader);
    }

    public static RecordedSession Read(TextReader reader)
    {
        var meta = new Dictionary<string, string>();
        var rows = new List<RecordedRow>();
        var events = new List<RecordedEvent>();
        bool header = false;
        int lineNo = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                var body = line.TrimStart('#').Trim();
                if (body.StartsWith("event=", StringComparison.Ordinal))
                {
                    var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var kind = parts[0]["event=".Length..];
                    var fields = parts.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                        .ToDictionary(p => p[0], p => p[1]);
                    events.Add(new RecordedEvent(kind, fields));
                }
                else if (!header)
                {
                    int eq = body.IndexOf('=');
                    if (eq > 0) meta[body[..eq]] = body[(eq + 1)..];
                }
                continue;
            }
            if (!header)
            {
                if (line != CsvFormat.Header) throw new CsvFormatException("unexpected CSV header", lineNo);
                header = true;
                continue;
            }
            rows.Add(ParseRow(line, lineNo));
        }
        if (!header) throw new CsvFormatException("missing CSV header", lineNo);

        var md = new SessionMetadata
        {
            SessionId = meta.TryGetValue("session_id", out var sid) && Guid.TryParse(sid, out var g) ? g : Guid.Empty,
            CreatedUtc = meta.TryGetValue("created_utc", out var c)
                ? DateTime.Parse(c, CsvFormat.Inv, DateTimeStyles.RoundtripKind) : default,
            Source = meta.GetValueOrDefault("source", ""),
            ToolVersion = meta.GetValueOrDefault("tool_version", ""),
            Settings = meta.TryGetValue("settings", out var s)
                ? JsonSerializer.Deserialize<AcquisitionSettings>(s, CsvFormat.Json) ?? new() : new(),
            Device = meta.TryGetValue("device", out var d) ? JsonSerializer.Deserialize<DeviceConfiguration>(d, CsvFormat.Json) : null,
        };
        return new RecordedSession(md, rows, events);
    }

    private static RecordedRow ParseRow(string line, int lineNo)
    {
        var f = line.Split(',');
        if (f.Length != 7) throw new CsvFormatException($"expected 7 fields, got {f.Length}", lineNo);
        try
        {
            var sample = new Sample(
                int.Parse(f[2], NumberStyles.Integer, CsvFormat.Inv),
                long.Parse(f[3], NumberStyles.Integer, CsvFormat.Inv),
                double.Parse(f[4], NumberStyles.Float, CsvFormat.Inv),
                double.Parse(f[5], NumberStyles.Float, CsvFormat.Inv),
                Enum.Parse<SampleQuality>(f[6]));
            return new RecordedRow(Guid.Parse(f[0]), f[1], sample);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new CsvFormatException(ex.Message, lineNo);
        }
    }
}
