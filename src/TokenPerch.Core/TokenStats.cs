using System.Text;
using System.Text.Json;

namespace TokenPerch.Core;

public sealed record DayTotal(DateOnly Date, string Name, bool Today, long Total);
public sealed record ModelTotal(string Name, long Total, long Input, long Output, long Cache);
public sealed record TokenReport(long TodayTotal, long TodayOutput, List<DayTotal> Days, List<ModelTotal> Models);

/// <summary>
/// Tokens per local day and per model, summed from Claude Code's session logs (projects/**/*.jsonl),
/// de-duplicated per message. Files are re-read only when they change.
/// </summary>
public sealed class TokenStats
{
    sealed record Entry(string Key, DateTimeOffset Time, string Model, long Input, long Output, long CacheCreate, long CacheRead);

    readonly Dictionary<string, (DateTime Modified, long Length, List<Entry> Entries)> _files = new();
    readonly string _projects;

    public TokenStats(string? projectsDir = null) => _projects = projectsDir ?? Paths.Projects;

    public TokenReport Compute(DateTimeOffset now, int days = 7)
    {
        var midnight = new DateTimeOffset(now.ToLocalTime().Date, now.ToLocalTime().Offset);
        var first = midnight.AddDays(-(days - 1));
        var seen = new HashSet<string>();
        var perDay = new Dictionary<DateOnly, long>();
        var models = new Dictionary<string, long[]>();   // total, input, output, cache
        long todayTotal = 0, todayOutput = 0;

        foreach (var e in Entries(first))
        {
            if (e.Time < first || !seen.Add(e.Key)) continue;
            long n = e.Input + e.Output + e.CacheCreate + e.CacheRead;
            var day = DateOnly.FromDateTime(e.Time.ToLocalTime().DateTime);
            perDay[day] = perDay.GetValueOrDefault(day) + n;
            if (e.Time < midnight) continue;
            todayTotal += n;
            todayOutput += e.Output;
            var name = Format.ModelName(e.Model);
            if (!models.TryGetValue(name, out var m)) models[name] = m = new long[4];
            m[0] += n; m[1] += e.Input; m[2] += e.Output; m[3] += e.CacheCreate + e.CacheRead;
        }

        var today = DateOnly.FromDateTime(midnight.DateTime);
        var dayList = Enumerable.Range(0, days)
            .Select(i => DateOnly.FromDateTime(first.AddDays(i).DateTime))
            .Select(d => new DayTotal(d, d.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture), d == today, perDay.GetValueOrDefault(d)))
            .ToList();
        var modelList = models.OrderByDescending(kv => kv.Value[0])
            .Select(kv => new ModelTotal(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]))
            .ToList();
        return new TokenReport(todayTotal, todayOutput, dayList, modelList);
    }

    IEnumerable<Entry> Entries(DateTimeOffset cutoff)
    {
        var current = new HashSet<string>();
        foreach (var path in LogFiles())
        {
            FileInfo info;
            try { info = new FileInfo(path); if (info.LastWriteTimeUtc < cutoff.UtcDateTime) continue; }
            catch (IOException) { continue; }
            current.Add(path);
            if (!_files.TryGetValue(path, out var cached) || cached.Modified != info.LastWriteTimeUtc || cached.Length != info.Length)
                _files[path] = cached = (info.LastWriteTimeUtc, info.Length, Parse(path, cutoff));
            foreach (var e in cached.Entries) yield return e;
        }
        foreach (var gone in _files.Keys.Where(k => !current.Contains(k)).ToList()) _files.Remove(gone);
    }

    IEnumerable<string> LogFiles()
    {
        if (!Directory.Exists(_projects)) return [];
        try
        {
            return Directory.EnumerateFiles(_projects, "*.jsonl", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static List<Entry> Parse(string path, DateTimeOffset cutoff)
    {
        var list = new List<Entry>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                if (ParseLine(line) is { } e && e.Time >= cutoff) list.Add(e);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    static Entry? ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), out var time)) return null;
            if (!r.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;
            var model = msg.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : "";
            if (model == "<synthetic>") return null;
            var id = msg.TryGetProperty("id", out var i) ? i.ToString() : "";
            var req = r.TryGetProperty("requestId", out var rq) ? rq.ToString() : "";
            return new Entry(id + "|" + req, time, model, N(u, "input_tokens"), N(u, "output_tokens"),
                N(u, "cache_creation_input_tokens"), N(u, "cache_read_input_tokens"));
        }
        catch (JsonException) { return null; }
    }

    static long N(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>Model of the newest assistant message in the most recently written session logs.</summary>
    public string CurrentModel()
    {
        var newest = LogFiles().Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).Take(3);
        foreach (var f in newest)
        {
            var lines = Tail(f.FullName, 512 * 1024);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (!line.Contains("\"model\"", StringComparison.Ordinal) || !line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("model", out var m)
                        && m.GetString() is { Length: > 0 } id && id != "<synthetic>")
                        return id;
                }
                catch (JsonException) { }
            }
        }
        return "";
    }

    static List<string> Tail(string path, int bytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > bytes) fs.Seek(-bytes, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = new List<string>();
            if (fs.Position > 0) reader.ReadLine();   // partial first line
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
