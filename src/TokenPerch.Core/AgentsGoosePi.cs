using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TokenPerch.Core;

/// <summary>
/// Goose (aaif-goose/goose, formerly block/goose): &lt;data&gt;/sessions/sessions.db, shared by the CLI and the
/// desktop app. Tokens from usage_ledger (one row per model call, schema v15+), else the sessions'
/// accumulated totals. Models from &lt;config&gt;/config.yaml (+ GOOSE_* environment). Written from the
/// v1.57.0 source.
/// </summary>
public sealed class GooseAgent : SqliteAgent
{
    readonly string? _root;
    public GooseAgent(string? root = null) => _root = root;

    public override string Id => "goose";
    public override string Name => "Goose";
    public override string Command => "goose";

    /// <summary>GOOSE_PATH_ROOT (absolute) → &lt;root&gt;/data and &lt;root&gt;/config; else XDG on Linux,
    /// %APPDATA%\Block\goose\{data,config} on Windows.</summary>
    string Dir(string kind)
    {
        var root = _root ?? Environment.GetEnvironmentVariable("GOOSE_PATH_ROOT");
        if (root is { Length: > 0 } && Path.IsPathRooted(root)) return Path.Combine(root, kind);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Block", "goose", kind);
        return kind == "data"
            ? Path.Combine(AbsXdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "goose")
            : Path.Combine(AbsXdg("XDG_CONFIG_HOME", ".config"), "goose");
    }

    static string AbsXdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : Path.Combine(Paths.Home, fallback);

    public override string DatabasePath => Path.Combine(Dir("data"), "sessions", "sessions.db");

    protected override List<AgentModel> ReadModels()
    {
        var path = Path.Combine(Dir("config"), "config.yaml");
        var cfg = File.Exists(path) ? MiniYaml.Parse(File.ReadAllText(path)) : new Dictionary<string, string>();
        return ParseConfig(cfg, Environment.GetEnvironmentVariable);
    }

    /// <summary>Provider: GOOSE_PROVIDER env, active_provider, GOOSE_PROVIDER key. Model: GOOSE_MODEL env,
    /// providers.&lt;active&gt;.model, GOOSE_MODEL key. OpenAI-compatible host: OPENAI_HOST / OPENAI_BASE_URL.</summary>
    internal static List<AgentModel> ParseConfig(Dictionary<string, string> cfg, Func<string, string?> env)
    {
        string? Get(string key) => env(key) is { Length: > 0 } e ? e : cfg.GetValueOrDefault(key) is { Length: > 0 } v ? v : null;
        var provider = env("GOOSE_PROVIDER") is { Length: > 0 } ep ? ep
            : cfg.GetValueOrDefault("active_provider") is { Length: > 0 } ap ? ap
            : cfg.GetValueOrDefault("GOOSE_PROVIDER") ?? "";
        var model = env("GOOSE_MODEL") is { Length: > 0 } em ? em
            : cfg.GetValueOrDefault($"providers.{provider}.model") is { Length: > 0 } pm ? pm
            : cfg.GetValueOrDefault("GOOSE_MODEL") ?? "";
        if (model.Length == 0) return [];
        var where = provider;
        if (provider == "openai" && (Get("OPENAI_HOST") ?? Get("OPENAI_BASE_URL")) is { } host) where = $"openai · {Host(host)}";
        return [new AgentModel("Default", model, where)];
    }

    protected override IEnumerable<TokenEntry> ReadTokens(SqliteConnection db, DateTimeOffset since)
    {
        var list = new List<TokenEntry>();
        var ledger = Columns(db, "usage_ledger");
        if (ledger.Count > 0)
        {
            using var cmd = db.CreateCommand();
            // carried_forward rows back-fill usage from before the ledger existed, stamped with the time of
            // the back-fill: they would land on the wrong day, so they are left out.
            cmd.CommandText = $"""
                SELECT created_timestamp, model, input_tokens, output_tokens, total_tokens, cache_read_tokens, cache_write_tokens
                FROM usage_ledger
                WHERE created_timestamp >= $since {(ledger.Contains("cost_source") ? "AND COALESCE(cost_source,'') <> 'carried_forward'" : "")}
                """;
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(Entry(Seconds(L(r, 0)), S(r, 1), L(r, 2), L(r, 3), L(r, 4), L(r, 5) + L(r, 6)));
            return list;
        }
        // Before the ledger (schema < 15): a session's accumulated totals, on the day it was last updated.
        var cols = Columns(db, "sessions");
        if (!cols.Contains("accumulated_input_tokens")) return list;
        using var old = db.CreateCommand();
        old.CommandText = """
            SELECT updated_at, json_extract(model_config_json,'$.model_name'), accumulated_input_tokens, accumulated_output_tokens,
                   accumulated_total_tokens, COALESCE(accumulated_cache_read_tokens,0) + COALESCE(accumulated_cache_write_tokens,0)
            FROM sessions WHERE updated_at >= $since
            """;
        old.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        using var o = old.ExecuteReader();
        while (o.Read())
            list.Add(Entry(UtcText(S(o, 0)), S(o, 1), L(o, 2), L(o, 3), L(o, 4), L(o, 5)));
        return list;
    }

    /// <summary>Whether a provider's input count already includes cached tokens differs per provider. When
    /// total = input + output, the cache is inside input (or uncounted): take it out of input so cached
    /// tokens are counted once.</summary>
    internal static TokenEntry Entry(DateTimeOffset time, string model, long input, long output, long total, long cache)
    {
        if (cache > 0 && total > 0 && input + output == total) input = Math.Max(0, input - cache);
        return new TokenEntry(time, model, input, output, cache);
    }

    /// <summary>Goose stores seconds, but some rows hold milliseconds (Goose's own rule: above 1e10 → ms).</summary>
    static DateTimeOffset Seconds(long t) => t > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(t) : DateTimeOffset.FromUnixTimeSeconds(t);

    static DateTimeOffset UtcText(string s) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTimeOffset.MinValue;

    protected override List<AgentSession> ReadSessions(SqliteConnection db, DateTimeOffset since)
    {
        var cols = Columns(db, "sessions");
        string Has(string c, string sql) => cols.Contains(c) ? sql : "";
        using var cmd = db.CreateCommand();
        // The session list as Goose orders it: newest message, else updated_at. Only the user's own
        // sessions (no subagents, schedules or hidden ones), not archived.
        cmd.CommandText = $"""
            SELECT s.id, s.name, s.description, s.working_dir, s.updated_at,
                   (SELECT MAX(m.created_timestamp) FROM messages m WHERE m.session_id = s.id),
                   {(cols.Contains("model_config_json") ? "json_extract(s.model_config_json,'$.model_name')" : "NULL")},
                   COALESCE({(cols.Contains("accumulated_total_tokens") ? "s.accumulated_total_tokens, " : "")}s.total_tokens, 0)
            FROM sessions s
            WHERE 1=1 {Has("session_type", "AND COALESCE(s.session_type,'user') = 'user'")}
                      {Has("parent_session_id", "AND s.parent_session_id IS NULL")}
                      {Has("archived_at", "AND s.archived_at IS NULL")}
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<AgentSession>();
        while (r.Read())
        {
            var updated = r.IsDBNull(5) ? UtcText(S(r, 4)) : Seconds(L(r, 5));
            if (updated < since) continue;
            var title = new[] { S(r, 1), S(r, 2) }.FirstOrDefault(x => x.Length > 0) ?? "(untitled)";
            list.Add(new AgentSession(S(r, 0), Format.OneLine(title, 120), S(r, 3), updated.ToUnixTimeMilliseconds(), SessionGroup.Done, S(r, 6), L(r, 7)));
        }
        list = list.OrderByDescending(s => s.Updated).Take(40).ToList();
        MarkWaiting(db, list);
        return list;
    }

    /// <summary>
    /// Goose stores a tool confirmation request as an assistant message part
    /// {"type":"actionRequired","data":{"actionType":"toolConfirmation","id":…}} before asking, and the
    /// answer as {"actionType":"toolConfirmationResponse","id":…} (or the tool's {"type":"toolResponse","id":…}).
    /// A request without an answer means the session waits for the user. Only sessions active in the last
    /// 30 minutes are checked, so a request left behind by a crash doesn't wait forever.
    /// </summary>
    static void MarkWaiting(SqliteConnection db, List<AgentSession> sessions)
    {
        long fresh = DateTimeOffset.Now.AddMinutes(-30).ToUnixTimeMilliseconds();
        for (int i = 0; i < sessions.Count; i++)
        {
            if (sessions[i].Updated < fresh) continue;
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT content_json FROM messages
                WHERE session_id = $id AND (content_json LIKE '%actionRequired%' OR content_json LIKE '%toolResponse%')
                ORDER BY id
                """;
            cmd.Parameters.AddWithValue("$id", sessions[i].Id);
            var pending = new HashSet<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    using var doc = JsonDocument.Parse(r.GetString(0));
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in doc.RootElement.EnumerateArray())
                    {
                        var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (type == "toolResponse" && part.TryGetProperty("id", out var rid)) pending.Remove(rid.ToString());
                        if (type != "actionRequired" || !part.TryGetProperty("data", out var d)) continue;
                        var action = d.TryGetProperty("actionType", out var a) ? a.GetString() : null;
                        var id = d.TryGetProperty("id", out var di) ? di.ToString() : "";
                        if (action == "toolConfirmation") pending.Add(id);
                        else if (action == "toolConfirmationResponse") pending.Remove(id);
                    }
                }
                catch (JsonException) { }
            }
            if (pending.Count > 0) sessions[i] = sessions[i] with { Group = SessionGroup.Needs };
        }
    }

    public override List<string> ResumeArgs(AgentSession s) => ["session", "--resume", "--session-id", s.Id];
}

/// <summary>
/// Pi coding agent (earendil-works/pi, formerly badlogic/pi-mono): one JSONL file per session under
/// &lt;agent dir&gt;/sessions (agent dir: $PI_CODING_AGENT_DIR, default ~/.pi/agent). Tokens from the usage
/// of each reply (plus usage / compaction entries), so days and models are exact; forked sessions copy
/// entries verbatim, so entries are counted once across files. Models from settings.json + models.json.
/// Written from the v1.5.0 source.
/// </summary>
public sealed class PiAgent : Agent
{
    readonly string? _agentDir;
    readonly Dictionary<string, (DateTime Modified, long Length, PiFile Data)> _files = new();

    public PiAgent(string? agentDir = null) => _agentDir = agentDir;

    public override string Id => "pi";
    public override string Name => "Pi";
    public override string Command => "pi";

    string AgentDir => _agentDir
        ?? (Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } d
            ? (d.StartsWith('~') ? Paths.Home + d[1..] : d)
            : Path.Combine(Paths.Home, ".pi", "agent"));

    JsonElement? Settings() => ReadJson(Path.Combine(AgentDir, "settings.json"));

    /// <summary>PI_CODING_AGENT_SESSION_DIR, else settings.json sessionDir, else &lt;agent dir&gt;/sessions.</summary>
    string SessionsDir
    {
        get
        {
            if (_agentDir is null && Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR") is { Length: > 0 } e) return e;
            if (Settings() is { } s && s.TryGetProperty("sessionDir", out var sd) && sd.GetString() is { Length: > 0 } dir)
                return dir.StartsWith('~') ? Paths.Home + dir[1..] : dir;
            return Path.Combine(AgentDir, "sessions");
        }
    }

    /// <summary>The sessions folder appears with the first conversation.</summary>
    public override string DataPath => SessionsDir;

    static JsonElement? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.Clone();
    }

    protected override List<AgentModel> ReadModels() =>
        ParseConfig(Settings(), ReadJson(Path.Combine(AgentDir, "models.json")));

    /// <summary>settings.json defaultProvider/defaultModel; base URLs of custom providers from models.json
    /// (providers.&lt;id&gt;.baseUrl), whose other models are listed too.</summary>
    internal static List<AgentModel> ParseConfig(JsonElement? settings, JsonElement? models)
    {
        string Str(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var providers = new Dictionary<string, (string Url, List<string> Models)>();
        if (models is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty("providers", out var ps) && ps.ValueKind == JsonValueKind.Object)
            foreach (var p in ps.EnumerateObject())
            {
                var ids = new List<string>();
                if (p.Value.TryGetProperty("models", out var ml) && ml.ValueKind == JsonValueKind.Array)
                    foreach (var x in ml.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s) ids.Add(s);
                providers[p.Name] = (Str(p.Value, "baseUrl"), ids);
            }
        string Where(string provider) =>
            providers.TryGetValue(provider, out var p) && p.Url.Length > 0 ? $"{provider} · {Host(p.Url)}" : provider;

        var list = new List<AgentModel>();
        var defProvider = Str(settings, "defaultProvider");
        var defModel = Str(settings, "defaultModel");
        if (defModel.Length > 0) list.Add(new AgentModel("Default", defModel, Where(defProvider)));
        foreach (var (pid, p) in providers)
            foreach (var id in p.Models)
                if (!(pid == defProvider && id == defModel)) list.Add(new AgentModel("Also set up", id, Where(pid)));
        return list;
    }

    internal sealed record PiEntry(string Key, TokenEntry Tokens);
    internal sealed record PiFile(string Id, string Title, string Cwd, long Updated, string Model, SessionGroup Group, List<PiEntry> Usage);

    protected override (IEnumerable<TokenEntry> Tokens, List<AgentSession> Sessions) ReadHistory(DateTimeOffset since)
    {
        var dir = SessionsDir;
        if (!Directory.Exists(dir)) return ([], []);
        List<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(dir, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ([], []); }

        var seen = new HashSet<string>();
        var entries = new List<TokenEntry>();
        var sessions = new List<AgentSession>();
        var current = new HashSet<string>();
        foreach (var path in paths)
        {
            FileInfo info;
            try { info = new FileInfo(path); if (info.LastWriteTimeUtc < since.UtcDateTime) continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            current.Add(path);
            if (!_files.TryGetValue(path, out var c) || c.Modified != info.LastWriteTimeUtc || c.Length != info.Length)
                _files[path] = c = (info.LastWriteTimeUtc, info.Length, ParseFile(path, info.LastWriteTimeUtc));
            var f = c.Data;
            long tokens = 0;
            foreach (var u in f.Usage)
            {
                tokens += u.Tokens.Input + u.Tokens.Output + u.Tokens.Cache;
                if (seen.Add(u.Key)) entries.Add(u.Tokens);    // forks copy entries: count each once
            }
            if (f.Updated >= since.ToUnixTimeMilliseconds())
                sessions.Add(new AgentSession(path, f.Title, f.Cwd, f.Updated, f.Group, f.Model, tokens));
        }
        foreach (var gone in _files.Keys.Where(k => !current.Contains(k)).ToList()) _files.Remove(gone);
        return (entries, sessions.OrderByDescending(s => s.Updated).Take(40).ToList());
    }

    internal static PiFile ParseFile(string path, DateTime modifiedUtc)
    {
        string id = "", cwd = "", name = "", firstUser = "", model = "", lastStop = "";
        long updated = 0, header = 0;
        bool nameSet = false;
        var usage = new List<PiEntry>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                var type = Str(e, "type");
                var entryId = Str(e, "id");
                var ts = Iso(Str(e, "timestamp"));
                switch (type)
                {
                    case "session":
                        id = entryId; cwd = Str(e, "cwd"); header = ts;
                        break;
                    case "session_info":
                        name = Str(e, "name"); nameSet = true;
                        break;
                    case "model_change":
                        if (Str(e, "modelId") is { Length: > 0 } mc) model = mc;
                        break;
                    case "usage":
                        if (e.TryGetProperty("usage", out var uu))
                            usage.Add(Usage($"{entryId}|{ts}|{Str(e, "model")}", ts, Str(e, "model") is { Length: > 0 } um ? um : model, uu));
                        break;
                    case "compaction" or "branch_summary":
                        // No model of their own: counted under the model in use at that point.
                        if (e.TryGetProperty("usage", out var cu))
                            usage.Add(Usage($"{entryId}|{ts}|{type}", ts, model, cu));
                        break;
                    case "message":
                        if (!e.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) break;
                        var role = Str(m, "role");
                        long mts = m.TryGetProperty("timestamp", out var mt) && mt.TryGetInt64(out var ms) ? ms : ts;
                        if (role is "user" or "assistant") updated = Math.Max(updated, mts);
                        if (role == "user" && firstUser.Length == 0) firstUser = Text(m);
                        if (role == "assistant")
                        {
                            if (Str(m, "model") is { Length: > 0 } am) model = am;
                            lastStop = Str(m, "stopReason");
                        }
                        if (role is "assistant" or "toolResult" && m.TryGetProperty("usage", out var mu) && mu.ValueKind == JsonValueKind.Object)
                            usage.Add(Usage($"{entryId}|{mts}|{model}", mts, model, mu));
                        break;
                }
            }
            catch (JsonException) { }
        }
        if (updated == 0) updated = header > 0 ? header : new DateTimeOffset(modifiedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var title = nameSet && name.Length > 0 ? name : firstUser.Length > 0 ? Format.OneLine(firstUser, 120) : "(no messages)";
        var group = lastStop is "error" ? SessionGroup.Failed
            : lastStop is "pending" or "toolUse" && DateTimeOffset.Now.ToUnixTimeMilliseconds() - updated < 10 * 60_000L ? SessionGroup.Working
            : SessionGroup.Done;
        return new PiFile(id, title, cwd, updated, model, group, usage);
    }

    /// <summary>Pi's usage: input excludes cache; reasoning is already part of output.</summary>
    static PiEntry Usage(string key, long ms, string model, JsonElement u) =>
        new(key, new TokenEntry(DateTimeOffset.FromUnixTimeMilliseconds(ms), model,
            N(u, "input"), N(u, "output"), N(u, "cacheRead") + N(u, "cacheWrite")));

    static string Text(JsonElement m)
    {
        if (!m.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        return string.Join(' ', c.EnumerateArray()
            .Where(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") == "text")
            .Select(b => Str(b, "text")));
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Iso(string s) => DateTimeOffset.TryParse(s, out var t) ? t.ToUnixTimeMilliseconds() : 0;

    static long N(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>The session's file: Pi resumes it in the session's own folder.</summary>
    public override List<string> ResumeArgs(AgentSession s) => ["--session", s.Id];
}

/// <summary>
/// Just enough YAML for agent configs: nested `key: value` maps by indentation, flattened to
/// "parent.child" keys. List items, anchors and multi-line values are skipped.
/// </summary>
internal static class MiniYaml
{
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new List<(int Indent, string Key)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-") continue;
            int indent = line.Length - trimmed.Length;
            int colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            var key = trimmed[..colon].Trim().Trim('"', '\'');
            var value = trimmed[(colon + 1)..].Trim();
            int hash = value.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0 && !value.StartsWith('"') && !value.StartsWith('\'')) value = value[..hash].Trim();
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var full = string.Join('.', stack.Select(s => s.Key).Append(key));
            if (value.Length == 0) stack.Add((indent, key));
            else if (value[0] is not ('|' or '>' or '[' or '{' or '&' or '*')) d[full] = value.Trim('"', '\'');
        }
        return d;
    }
}
