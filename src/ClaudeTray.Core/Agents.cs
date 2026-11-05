using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClaudeTray.Core;

/// <summary>A model an agent is set up to use: its default, a backup, or another configured one.</summary>
public sealed record AgentModel(string Role, string Model, string Where);

/// <summary>One of an agent's own sessions (not Claude Code's).</summary>
public sealed record AgentSession(string Id, string Title, string Directory, long Updated, SessionGroup Group, string Model, long Tokens);

public sealed record AgentReport(List<AgentModel> Models, TokenReport Tokens, List<AgentSession> Sessions, string? Error = null);

/// <summary>One token record: when, which model, and the counts.</summary>
public sealed record TokenEntry(DateTimeOffset Time, string Model, long Input, long Output, long Cache);

/// <summary>
/// Another coding agent on this computer (OpenCode, Hermes Agent). Everything is read from the agent's
/// own files, read-only: its config for the models, its SQLite database for sessions and tokens.
/// An agent shows up in the panel only when its database exists.
/// </summary>
public abstract class Agent
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    /// <summary>The agent's command (looked up on PATH when opening it).</summary>
    public abstract string Command { get; }
    public abstract string DatabasePath { get; }
    public bool Installed => File.Exists(DatabasePath);

    public AgentReport Read(DateTimeOffset now, int days = 7)
    {
        var models = new List<AgentModel>();
        string? error = null;
        try { models = ReadModels(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            error = $"Couldn't read {Name}'s settings";
            Log.Error($"{Id} settings", e);
        }
        try
        {
            var midnight = new DateTimeOffset(now.ToLocalTime().Date, now.ToLocalTime().Offset);
            var first = midnight.AddDays(-(days - 1));
            using var db = Open();
            var tokens = TokenTotals.Build(ReadTokens(db, first), now, days, DisplayModel);
            var sessions = ReadSessions(db, first);
            return new AgentReport(models, tokens, sessions, error);
        }
        catch (SqliteException e)
        {
            Log.Error($"{Id} database", e);
            return new AgentReport(models, TokenTotals.Build([], now, days, DisplayModel), [],
                $"Couldn't read {Name}'s history (a newer {Name} version may have changed it)");
        }
    }

    /// <summary>Opens the database read-only. The agent may be writing to it at the same time (WAL).</summary>
    SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var db = new SqliteConnection(cs);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 2000;";
        cmd.ExecuteNonQuery();
        return db;
    }

    protected abstract List<AgentModel> ReadModels();
    protected abstract IEnumerable<TokenEntry> ReadTokens(SqliteConnection db, DateTimeOffset since);
    protected abstract List<AgentSession> ReadSessions(SqliteConnection db, DateTimeOffset since);

    /// <summary>Command that opens a session again (run in a terminal).</summary>
    public abstract List<string> ResumeArgs(AgentSession s);

    /// <summary>Model ids are shown as they are: local models have no friendly names.</summary>
    protected virtual string DisplayModel(string id) => string.IsNullOrEmpty(id) ? "?" : id;

    public static IReadOnlyList<Agent> All { get; } = [new OpenCodeAgent(), new HermesAgent()];

    protected static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v : Path.Combine(Paths.Home, fallback);

    protected static long L(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
    protected static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
    protected static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetValue(i).ToString() ?? "";

    /// <summary>"http://127.0.0.1:11434/v1" → "127.0.0.1:11434".</summary>
    internal static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}") : url;
}

/// <summary>Tokens per day and per model, like Claude Code's (TokenStats), from any agent's records.</summary>
public static class TokenTotals
{
    public static TokenReport Build(IEnumerable<TokenEntry> entries, DateTimeOffset now, int days, Func<string, string> modelName)
    {
        var midnight = new DateTimeOffset(now.ToLocalTime().Date, now.ToLocalTime().Offset);
        var first = midnight.AddDays(-(days - 1));
        var perDay = new Dictionary<DateOnly, long>();
        var models = new Dictionary<string, long[]>();   // total, input, output, cache
        long todayTotal = 0, todayOutput = 0;
        foreach (var e in entries)
        {
            if (e.Time < first) continue;
            long n = e.Input + e.Output + e.Cache;
            var day = DateOnly.FromDateTime(e.Time.ToLocalTime().DateTime);
            perDay[day] = perDay.GetValueOrDefault(day) + n;
            if (e.Time < midnight) continue;
            todayTotal += n;
            todayOutput += e.Output;
            var name = modelName(e.Model);
            if (!models.TryGetValue(name, out var m)) models[name] = m = new long[4];
            m[0] += n; m[1] += e.Input; m[2] += e.Output; m[3] += e.Cache;
        }
        var today = DateOnly.FromDateTime(midnight.DateTime);
        var dayList = Enumerable.Range(0, days)
            .Select(i => DateOnly.FromDateTime(first.AddDays(i).DateTime))
            .Select(d => new DayTotal(d, d.ToString("ddd", CultureInfo.CurrentCulture), d == today, perDay.GetValueOrDefault(d)))
            .ToList();
        var modelList = models.OrderByDescending(kv => kv.Value[0])
            .Select(kv => new ModelTotal(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]))
            .ToList();
        return new TokenReport(todayTotal, todayOutput, dayList, modelList);
    }
}

/// <summary>
/// OpenCode (opencode.ai): ~/.local/share/opencode/opencode.db and ~/.config/opencode/opencode.json(c).
/// Tokens come from each assistant message, so days and models are exact.
/// </summary>
public sealed class OpenCodeAgent : Agent
{
    readonly string? _dataDir, _configDir;
    public OpenCodeAgent(string? dataDir = null, string? configDir = null) { _dataDir = dataDir; _configDir = configDir; }

    public override string Id => "opencode";
    public override string Name => "OpenCode";
    public override string Command => "opencode";
    string DataDir => _dataDir ?? Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "opencode");
    string ConfigDir => _configDir ?? Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "opencode");
    public override string DatabasePath => Path.Combine(DataDir, "opencode.db");

    protected override List<AgentModel> ReadModels()
    {
        var path = new[] { "opencode.jsonc", "opencode.json", "config.json" }.Select(f => Path.Combine(ConfigDir, f)).FirstOrDefault(File.Exists);
        return path is null ? [] : ParseConfig(File.ReadAllText(path));
    }

    /// <summary>The default model first ("provider/model"), then the other models of custom providers.</summary>
    internal static List<AgentModel> ParseConfig(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var providers = new Dictionary<string, (string Name, string Url, List<(string Id, string Name)> Models)>();
        if (root.TryGetProperty("provider", out var ps) && ps.ValueKind == JsonValueKind.Object)
            foreach (var p in ps.EnumerateObject())
            {
                var name = p.Value.TryGetProperty("name", out var n) ? n.GetString() ?? p.Name : p.Name;
                var url = p.Value.TryGetProperty("options", out var o) && o.TryGetProperty("baseURL", out var b) ? b.GetString() ?? "" : "";
                var ms = new List<(string, string)>();
                if (p.Value.TryGetProperty("models", out var mm) && mm.ValueKind == JsonValueKind.Object)
                    foreach (var m in mm.EnumerateObject())
                        ms.Add((m.Name, m.Value.TryGetProperty("name", out var mn) ? mn.GetString() ?? m.Name : m.Name));
                providers[p.Name] = (name, url, ms);
            }

        string Where(string provider) => providers.TryGetValue(provider, out var p)
            ? (p.Url.Length > 0 ? $"{p.Name} · {Host(p.Url)}" : p.Name) : provider;

        var list = new List<AgentModel>();
        var def = root.TryGetProperty("model", out var dm) ? dm.GetString() ?? "" : "";
        var (defProvider, defModel) = SplitModel(def);
        if (def.Length > 0) list.Add(new AgentModel("Default", defModel, Where(defProvider)));
        foreach (var (pid, p) in providers)
            foreach (var (mid, _) in p.Models)
                if (!(pid == defProvider && mid == defModel))
                    list.Add(new AgentModel("Also set up", mid, Where(pid)));
        return list;
    }

    static (string Provider, string Model) SplitModel(string s)
    {
        int i = s.IndexOf('/');
        return i < 0 ? ("", s) : (s[..i], s[(i + 1)..]);
    }

    protected override IEnumerable<TokenEntry> ReadTokens(SqliteConnection db, DateTimeOffset since)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT time_created, json_extract(data,'$.model.id'),
                   json_extract(data,'$.tokens.input'), json_extract(data,'$.tokens.output'), json_extract(data,'$.tokens.reasoning'),
                   json_extract(data,'$.tokens.cache.read'), json_extract(data,'$.tokens.cache.write')
            FROM session_message WHERE type = 'assistant' AND time_created >= $since
            """;
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        var list = new List<TokenEntry>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                list.Add(new TokenEntry(DateTimeOffset.FromUnixTimeMilliseconds(L(r, 0)), S(r, 1),
                    L(r, 2), L(r, 3) + L(r, 4), L(r, 5) + L(r, 6)));

        // A session's own totals also count calls that leave no message (titles, summaries): add what
        // the messages don't explain, on the session's last-active day, under its newest model.
        using var rest = db.CreateCommand();
        rest.CommandText = """
            SELECT s.time_updated,
                   (SELECT json_extract(m.data,'$.model.id') FROM session_message m
                     WHERE m.session_id = s.id AND m.type = 'assistant' ORDER BY m.seq DESC LIMIT 1),
                   s.tokens_input - COALESCE(t.i, 0),
                   s.tokens_output + s.tokens_reasoning - COALESCE(t.o, 0),
                   s.tokens_cache_read + s.tokens_cache_write - COALESCE(t.c, 0)
            FROM session_v2 s
            LEFT JOIN (SELECT session_id,
                              SUM(COALESCE(json_extract(data,'$.tokens.input'),0)) AS i,
                              SUM(COALESCE(json_extract(data,'$.tokens.output'),0) + COALESCE(json_extract(data,'$.tokens.reasoning'),0)) AS o,
                              SUM(COALESCE(json_extract(data,'$.tokens.cache.read'),0) + COALESCE(json_extract(data,'$.tokens.cache.write'),0)) AS c
                       FROM session_message WHERE type = 'assistant' GROUP BY session_id) t ON t.session_id = s.id
            WHERE s.time_updated >= $since
            """;
        rest.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        using (var r = rest.ExecuteReader())
            while (r.Read())
            {
                long i = Math.Max(0, L(r, 2)), o = Math.Max(0, L(r, 3)), c = Math.Max(0, L(r, 4));
                if (i + o + c > 0)
                    list.Add(new TokenEntry(DateTimeOffset.FromUnixTimeMilliseconds(L(r, 0)), S(r, 1), i, o, c));
            }
        return list;
    }

    protected override List<AgentSession> ReadSessions(SqliteConnection db, DateTimeOffset since)
    {
        using var cmd = db.CreateCommand();
        // Top-level sessions only (subagent sessions have a parent); the model of the newest reply.
        cmd.CommandText = """
            SELECT s.id, s.title, s.directory, s.time_updated, s.time_busy, s.time_idle, s.idle_outcome,
                   s.tokens_input + s.tokens_output + s.tokens_reasoning + s.tokens_cache_read + s.tokens_cache_write,
                   (SELECT json_extract(m.data,'$.model.id') FROM session_message m
                     WHERE m.session_id = s.id AND m.type = 'assistant' ORDER BY m.seq DESC LIMIT 1)
            FROM session_v2 s
            WHERE s.parent_id IS NULL AND s.time_archived IS NULL AND s.time_updated >= $since
            ORDER BY s.time_updated DESC LIMIT 40
            """;
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        var list = new List<AgentSession>();
        while (r.Read())
        {
            long busy = L(r, 4), idle = L(r, 5);
            var outcome = S(r, 6);
            var group = busy > idle ? SessionGroup.Working
                : outcome.Contains("fail", StringComparison.OrdinalIgnoreCase) || outcome.Contains("error", StringComparison.OrdinalIgnoreCase)
                    ? SessionGroup.Failed : SessionGroup.Done;
            var title = S(r, 1);
            list.Add(new AgentSession(S(r, 0), title.Length > 0 ? title : "(untitled)", S(r, 2), L(r, 3), group, S(r, 8), L(r, 7)));
        }
        return list;
    }

    public override List<string> ResumeArgs(AgentSession s) =>
        s.Directory.Length > 0 && Directory.Exists(s.Directory) ? [s.Directory, "--session", s.Id] : ["--session", s.Id];
}

/// <summary>
/// Hermes Agent (Nous Research): $HERMES_HOME (default ~/.hermes)/state.db and config.yaml.
/// Hermes keeps tokens per session and model (not per message), so a session counts on the day it was
/// last active.
/// </summary>
public sealed class HermesAgent : Agent
{
    readonly string? _home;
    public HermesAgent(string? home = null) => _home = home;

    public override string Id => "hermes";
    public override string Name => "Hermes";
    public override string Command => "hermes";
    string Home => _home ?? (Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } h ? h : Path.Combine(Paths.Home, ".hermes"));
    public override string DatabasePath => Path.Combine(Home, "state.db");

    protected override List<AgentModel> ReadModels()
    {
        var path = Path.Combine(Home, "config.yaml");
        return File.Exists(path) ? ParseConfig(File.ReadAllText(path)) : [];
    }

    /// <summary>
    /// The `model:` block (default, provider, base_url) and the `fallback_providers:` list. A small
    /// reader for just these keys, not a YAML parser.
    /// </summary>
    internal static List<AgentModel> ParseConfig(string yaml)
    {
        var list = new List<AgentModel>();
        string section = "";
        Dictionary<string, string>? current = null;
        var model = new Dictionary<string, string>();
        var fallbacks = new List<Dictionary<string, string>>();
        foreach (var raw in yaml.Split('\n'))
        {
            var line = StripComment(raw.TrimEnd('\r'));
            if (line.Trim().Length == 0) continue;
            int indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();
            if (indent == 0)
            {
                section = text.EndsWith(':') ? text[..^1] : "";
                current = null;
                continue;
            }
            if (section == "model" && KeyValue(text) is { } kv) model[kv.Key] = kv.Value;
            else if (section == "fallback_providers")
            {
                if (text.StartsWith("- ", StringComparison.Ordinal)) { fallbacks.Add(current = []); text = text[2..].Trim(); }
                if (current is not null && KeyValue(text) is { } fkv) current[fkv.Key] = fkv.Value;
            }
        }
        if (model.GetValueOrDefault("default") is { Length: > 0 } def)
            list.Add(new AgentModel("Default", def, Where(model)));
        foreach (var f in fallbacks)
            if (f.GetValueOrDefault("model") is { Length: > 0 } fm)
                list.Add(new AgentModel("Backup", fm, Where(f)));
        return list;
    }

    static string Where(Dictionary<string, string> d) =>
        d.GetValueOrDefault("base_url") is { Length: > 0 } url ? Host(url) : d.GetValueOrDefault("provider") ?? "";

    static (string Key, string Value)? KeyValue(string text)
    {
        int i = text.IndexOf(':');
        if (i <= 0) return null;
        var v = text[(i + 1)..].Trim().Trim('"', '\'');
        return (text[..i].Trim(), v);
    }

    static string StripComment(string line)
    {
        bool inS = false, inD = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && !inD) inS = !inS;
            else if (c == '"' && !inS) inD = !inD;
            else if (c == '#' && !inS && !inD && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line[..i];
        }
        return line;
    }

    protected override IEnumerable<TokenEntry> ReadTokens(SqliteConnection db, DateTimeOffset since)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(last_seen, first_seen), model, input_tokens, output_tokens, reasoning_tokens,
                   cache_read_tokens, cache_write_tokens
            FROM session_model_usage WHERE COALESCE(last_seen, first_seen) >= $since
            """;
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var list = new List<TokenEntry>();
        while (r.Read())
            list.Add(new TokenEntry(DateTimeOffset.FromUnixTimeMilliseconds((long)(D(r, 0) * 1000)), S(r, 1),
                L(r, 2), L(r, 3) + L(r, 4), L(r, 5) + L(r, 6)));
        return list;
    }

    protected override List<AgentSession> ReadSessions(SqliteConnection db, DateTimeOffset since)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, cwd, COALESCE(ended_at, last_activity_at, started_at), ended_at, end_reason, model,
                   COALESCE(input_tokens,0) + COALESCE(output_tokens,0) + COALESCE(reasoning_tokens,0)
                   + COALESCE(cache_read_tokens,0) + COALESCE(cache_write_tokens,0)
            FROM sessions
            WHERE parent_session_id IS NULL AND COALESCE(ended_at, last_activity_at, started_at) >= $since
            ORDER BY COALESCE(ended_at, last_activity_at, started_at) DESC LIMIT 40
            """;
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        var list = new List<AgentSession>();
        while (r.Read())
        {
            var reason = S(r, 5);
            long updated = (long)(D(r, 3) * 1000);
            // No end time: still open, unless it has been quiet for half a day (Hermes was killed).
            bool open = r.IsDBNull(4) && DateTimeOffset.Now.ToUnixTimeMilliseconds() - updated < 12 * 3600_000L;
            var group = open ? SessionGroup.Working
                : reason.Contains("error", StringComparison.OrdinalIgnoreCase) || reason.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    ? SessionGroup.Failed : SessionGroup.Done;
            var title = S(r, 1);
            list.Add(new AgentSession(S(r, 0), title.Length > 0 ? title : "(untitled)", S(r, 2),
                updated, group, S(r, 6), L(r, 7)));
        }
        return list;
    }

    public override List<string> ResumeArgs(AgentSession s) =>
        s.Directory.Length > 0 && Directory.Exists(s.Directory) ? ["--resume", s.Id, "--in", s.Directory] : ["--resume", s.Id];
}
