using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TokenPerch.Core;

/// <summary>
/// OpenAI Codex CLI: $CODEX_HOME (default ~/.codex). Sessions ("threads") and their totals come from
/// state_N.sqlite; tokens per reply from each session's rollout file (sessions/YYYY/MM/DD/rollout-*.jsonl,
/// one token_usage_record line per model response), so days and models are exact. Models from config.toml.
/// Checked against Codex CLI 0.166.0.
/// </summary>
public sealed class CodexAgent : Agent
{
    readonly string? _home;
    readonly Dictionary<string, (DateTime Modified, long Length, List<TokenEntry> Entries)> _rollouts = new();

    public CodexAgent(string? home = null) => _home = home;

    public override string Id => "codex";
    public override string Name => "Codex";
    public override string Command => "codex";
    string Home => _home ?? (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(Paths.Home, ".codex"));

    /// <summary>The SQLite files live in config.toml's sqlite_home, else $CODEX_SQLITE_HOME, else the Codex home.</summary>
    string SqliteHome
    {
        get
        {
            if (_home is null && Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME") is { Length: > 0 } env) return env;
            var cfg = Path.Combine(Home, "config.toml");
            if (File.Exists(cfg) && Toml.Parse(File.ReadAllText(cfg)).GetValueOrDefault("sqlite_home") is { Length: > 0 } sh)
                return sh.StartsWith('~') ? Paths.Home + sh[1..] : sh;
            return Home;
        }
    }

    /// <summary>state_5.sqlite today; the number goes up with Codex's schema versions, so take the highest.</summary>
    string? StateDb()
    {
        try
        {
            var dir = SqliteHome;
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "state_*.sqlite")
                .Select(p => (p, n: int.TryParse(Path.GetFileNameWithoutExtension(p)["state_".Length..], out var n) ? n : -1))
                .Where(x => x.n >= 0).OrderByDescending(x => x.n).Select(x => x.p).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Codex creates ~/.codex/sessions even before first use, so only the state database counts.</summary>
    public override string DataPath => StateDb() ?? Path.Combine(SqliteHome, "state_5.sqlite");

    protected override List<AgentModel> ReadModels()
    {
        var path = Path.Combine(Home, "config.toml");
        return File.Exists(path) ? ParseConfig(File.ReadAllText(path)) : [];
    }

    /// <summary>`model` + `model_provider` (default "openai"), with the provider's name and base_url from
    /// [model_providers.&lt;id&gt;].</summary>
    internal static List<AgentModel> ParseConfig(string toml)
    {
        var t = Toml.Parse(toml);
        if (t.GetValueOrDefault("model") is not { Length: > 0 } model) return [];
        var provider = t.GetValueOrDefault("model_provider") is { Length: > 0 } p ? p : "openai";
        var name = t.GetValueOrDefault($"model_providers.{provider}.name") is { Length: > 0 } n ? n : provider;
        var url = t.GetValueOrDefault($"model_providers.{provider}.base_url") ?? "";
        return [new AgentModel("Default", model, url.Length > 0 ? $"{name} · {Host(url)}" : name)];
    }

    protected override (IEnumerable<TokenEntry> Tokens, List<AgentSession> Sessions) ReadHistory(DateTimeOffset since)
    {
        if (StateDb() is not { } path) return ([], []);
        var sessions = new List<AgentSession>();
        var rollouts = new List<string>();
        using (var db = OpenDb(path))
        {
            var cols = Columns(db, "threads");
            string Col(string c, string fallback = "NULL") => cols.Contains(c) ? c : fallback;
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, {Col("name")}, {Col("title")}, {Col("first_user_message")}, {Col("cwd")}, {Col("updated_at_ms")},
                       {Col("model")}, {Col("tokens_used", "0")}, {Col("rollout_path")}
                FROM threads
                WHERE {Col("updated_at_ms", "0")} >= $since {(cols.Contains("archived") ? "AND archived = 0" : "")}
                ORDER BY {Col("updated_at_ms", "0")} DESC
                """;
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var title = new[] { S(r, 1), S(r, 2), S(r, 3) }.FirstOrDefault(x => x.Length > 0) ?? "(untitled)";
                if (sessions.Count < 40)
                    sessions.Add(new AgentSession(S(r, 0), Format.OneLine(title, 120), S(r, 4), L(r, 5), SessionGroup.Done, S(r, 6), L(r, 7)));
                if (S(r, 8) is { Length: > 0 } rollout) rollouts.Add(rollout);
            }
        }
        MarkStatus(sessions);
        var entries = new List<TokenEntry>();
        var current = new HashSet<string>();
        foreach (var file in rollouts)
        {
            FileInfo info;
            try { info = new FileInfo(file); if (!info.Exists) continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            current.Add(file);
            if (!_rollouts.TryGetValue(file, out var c) || c.Modified != info.LastWriteTimeUtc || c.Length != info.Length)
                _rollouts[file] = c = (info.LastWriteTimeUtc, info.Length, ParseRollout(file));
            entries.AddRange(c.Entries);
        }
        foreach (var gone in _rollouts.Keys.Where(k => !current.Contains(k)).ToList()) _rollouts.Remove(gone);
        return (entries, sessions);
    }

    /// <summary>Working / failed from the newest turn in thread_history_N.sqlite (paginated threads only;
    /// older threads have no turns there and stay "done").</summary>
    void MarkStatus(List<AgentSession> sessions)
    {
        if (sessions.Count == 0) return;
        string? path;
        try
        {
            path = Directory.EnumerateFiles(SqliteHome, "thread_history_*.sqlite").OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (path is null) return;
        try
        {
            using var db = OpenDb(path);
            var cols = Columns(db, "thread_turns");
            if (!cols.Contains("thread_id") || !cols.Contains("status") || !cols.Contains("started_at")) return;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT status FROM thread_turns WHERE thread_id = $id ORDER BY started_at DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$id", sessions[i].Id);
                var status = cmd.ExecuteScalar() as string ?? "";
                // A turn left "inProgress" by a crash would show as working forever: only trust it for 12 h.
                bool recent = DateTimeOffset.Now.ToUnixTimeMilliseconds() - sessions[i].Updated < 12 * 3600_000L;
                var group = status switch
                {
                    "inProgress" when recent => SessionGroup.Working,
                    "failed" => SessionGroup.Failed,
                    _ => SessionGroup.Done,
                };
                if (group != SessionGroup.Done) sessions[i] = sessions[i] with { Group = group };
            }
        }
        catch (SqliteException e) { Log.Error("codex thread history", e); }
    }

    /// <summary>
    /// One entry per token_usage_record line (a delta per model response), timed by the line's UTC
    /// timestamp, under the model of the latest turn_context before it. Codex's input_tokens include the
    /// cached ones, so input = input − cached; reasoning is part of output_tokens. The repeated
    /// event_msg/token_count lines are ignored.
    /// </summary>
    internal static List<TokenEntry> ParseRollout(string path)
    {
        var list = new List<TokenEntry>();
        var model = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                bool ctx = line.Contains("\"turn_context\"", StringComparison.Ordinal);
                if (!ctx && !line.Contains("\"token_usage_record\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                    if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                    if (type == "turn_context")
                    {
                        if (p.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } id) model = id;
                        continue;
                    }
                    if (type != "token_usage_record" || !p.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), out var time)) continue;
                    long input = N(u, "input_tokens"), cached = N(u, "cached_input_tokens"), output = N(u, "output_tokens");
                    list.Add(new TokenEntry(time, model, Math.Max(0, input - cached), output, cached + N(u, "cache_write_input_tokens")));
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    static long N(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public override List<string> ResumeArgs(AgentSession s) => ["resume", s.Id];
}

/// <summary>
/// Just enough TOML for agent configs: `key = value` lines, keyed "table.key" under [table] headers.
/// Strings (basic and literal), numbers and booleans as text; arrays and inline tables are skipped.
/// </summary>
internal static class Toml
{
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var table = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("[[", StringComparison.Ordinal)) { table = "\u0000"; continue; }   // arrays of tables: ignored
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                table = end > 1 ? string.Join('.', line[1..end].Split('.').Select(k => k.Trim().Trim('"', '\''))) : "";
                continue;
            }
            if (table == "\u0000") continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().Trim('"', '\'');
            var value = Value(line[(eq + 1)..].Trim());
            if (value is null) continue;
            d[table.Length > 0 ? $"{table}.{key}" : key] = value;
        }
        return d;
    }

    static string? Value(string v)
    {
        if (v.Length == 0) return null;
        if (v[0] == '"' || v[0] == '\'')
        {
            char q = v[0];
            var sb = new StringBuilder();
            for (int i = 1; i < v.Length; i++)
            {
                char c = v[i];
                if (c == q) return sb.ToString();
                if (q == '"' && c == '\\' && i + 1 < v.Length) { i++; sb.Append(v[i] switch { 'n' => '\n', 't' => '\t', _ => v[i] }); continue; }
                sb.Append(c);
            }
            return null;
        }
        if (v[0] is '[' or '{') return null;
        int hash = v.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? v[..hash] : v).Trim();
    }
}
