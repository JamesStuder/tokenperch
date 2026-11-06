using System.Text;
using System.Text.Json;

namespace ClaudeTray.Core;

/// <summary>
/// Qwen Code (QwenLM/qwen-code): $QWEN_HOME (default ~/.qwen)/projects/&lt;project&gt;/chats/&lt;session&gt;.jsonl.
/// Tokens from the per-call "qwen-code.api_response" telemetry records, which also cover background
/// calls (the assistant records' usageMetadata repeats the main calls only and is not added again).
/// Models from settings.json. Checked against Qwen Code 0.26.8.
/// </summary>
public sealed class QwenAgent : Agent
{
    readonly string? _home;
    readonly Dictionary<string, (DateTime Modified, long Length, QwenFile Data)> _files = new();

    public QwenAgent(string? home = null) => _home = home;

    public override string Id => "qwen";
    public override string Name => "Qwen Code";
    public override string Command => "qwen";

    string Home => _home ?? (Environment.GetEnvironmentVariable("QWEN_HOME") is { Length: > 0 } h ? h : Path.Combine(Paths.Home, ".qwen"));
    /// <summary>Session files move with QWEN_RUNTIME_DIR (settings' advanced.runtimeOutputDir is not followed).</summary>
    string Runtime => _home is null && Environment.GetEnvironmentVariable("QWEN_RUNTIME_DIR") is { Length: > 0 } r ? r : Home;

    public override string DataPath => Path.Combine(Runtime, "projects");

    protected override List<AgentModel> ReadModels()
    {
        var path = Path.Combine(Home, "settings.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return ParseConfig(doc.RootElement, Environment.GetEnvironmentVariable);
    }

    /// <summary>model.name (OPENAI_MODEL wins) with the auth type from security.auth.selectedType; the base
    /// URL from the modelProviders.&lt;type&gt; entry with that id (OPENAI_BASE_URL wins).</summary>
    internal static List<AgentModel> ParseConfig(JsonElement root, Func<string, string?> env)
    {
        string Path2(params string[] keys)
        {
            var e = root;
            foreach (var k in keys)
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out e)) return "";
            return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        }
        var type = Path2("security", "auth", "selectedType");
        var model = env("OPENAI_MODEL") is { Length: > 0 } em && type is "openai" or "" ? em : Path2("model", "name");
        if (model.Length == 0) return [];
        var url = "";
        if (root.TryGetProperty("modelProviders", out var mp) && mp.ValueKind == JsonValueKind.Object
            && mp.TryGetProperty(type, out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var p in list.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var id) && id.GetString() == model
                    && p.TryGetProperty("baseUrl", out var b) && b.GetString() is { Length: > 0 } u) { url = u; break; }
        if (type == "openai" && env("OPENAI_BASE_URL") is { Length: > 0 } eu) url = eu;
        var where = type.Length > 0 ? type : "qwen";
        return [new AgentModel("Default", model, url.Length > 0 ? $"{where} · {Host(url)}" : where)];
    }

    internal sealed record QwenFile(string Id, string Title, string Cwd, string Model, List<(string Key, TokenEntry Tokens)> Usage);

    protected override (IEnumerable<TokenEntry> Tokens, List<AgentSession> Sessions) ReadHistory(DateTimeOffset since)
    {
        var projects = DataPath;
        if (!Directory.Exists(projects)) return ([], []);
        var paths = new List<string>();
        try
        {
            foreach (var p in Directory.EnumerateDirectories(projects))
            {
                var chats = Path.Combine(p, "chats");
                if (Directory.Exists(chats)) paths.AddRange(Directory.EnumerateFiles(chats, "*.jsonl"));   // not chats/archive
            }
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
                _files[path] = c = (info.LastWriteTimeUtc, info.Length, ParseFile(path));
            var f = c.Data;
            long tokens = 0;
            foreach (var (key, t) in f.Usage)
            {
                tokens += t.Input + t.Output + t.Cache;
                if (seen.Add(key)) entries.Add(t);
            }
            var title = TitleFile(path) ?? f.Title;
            sessions.Add(new AgentSession(f.Id, title, f.Cwd, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                SessionGroup.Done, f.Model, tokens));
        }
        foreach (var gone in _files.Keys.Where(k => !current.Contains(k)).ToList()) _files.Remove(gone);
        return (entries, sessions.OrderByDescending(s => s.Updated).Take(40).ToList());
    }

    /// <summary>chats/&lt;id&gt;.title.json {displayName} — a title set in Qwen's own session picker.</summary>
    static string? TitleFile(string jsonl)
    {
        var p = Path.ChangeExtension(jsonl, null) + ".title.json";
        try
        {
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("displayName", out var d) && d.GetString() is { Length: > 0 } s ? Format.OneLine(s, 120) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static QwenFile ParseFile(string path)
    {
        string id = Path.GetFileNameWithoutExtension(path), cwd = "", firstUser = "", customTitle = "", model = "";
        var usage = new List<(string, TokenEntry)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (Str(e, "sessionId") is { Length: > 0 } sid) id = sid;
                if (cwd.Length == 0 && Str(e, "cwd") is { Length: > 0 } c) cwd = c;
                var type = Str(e, "type");
                var subtype = Str(e, "subtype");
                if (type == "user" && firstUser.Length == 0 && subtype.Length == 0 && e.TryGetProperty("message", out var m)
                    && m.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    firstUser = string.Join(' ', parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => Str(p, "text")).Where(t => t.Length > 0));
                if (type == "assistant" && Str(e, "model") is { Length: > 0 } am) model = am;
                if (!e.TryGetProperty("systemPayload", out var sp) || sp.ValueKind != JsonValueKind.Object) continue;
                if (subtype == "custom_title" && Str(sp, "customTitle") is { } ct) customTitle = ct;
                if (subtype == "ui_telemetry" && sp.TryGetProperty("uiEvent", out var ev) && ev.ValueKind == JsonValueKind.Object
                    && Str(ev, "event.name") == "qwen-code.api_response")
                {
                    if (!DateTimeOffset.TryParse(Str(ev, "event.timestamp"), out var ts)) continue;
                    // input includes the cached tokens and output the thoughts: total = input + output.
                    long input = N(ev, "input_token_count"), cached = N(ev, "cached_content_token_count"), output = N(ev, "output_token_count");
                    var key = Str(ev, "response_id") is { Length: > 0 } rid ? rid
                        : $"{Str(ev, "execution_id")}|{Str(ev, "prompt_id")}|{ts.ToUnixTimeMilliseconds()}";
                    var evModel = Str(ev, "model") is { Length: > 0 } em ? em : model;
                    usage.Add((key, new TokenEntry(ts, evModel, Math.Max(0, input - cached), output, cached)));
                }
            }
            catch (JsonException) { }
        }
        var title = customTitle.Length > 0 ? customTitle : firstUser.Length > 0 ? firstUser : "(untitled)";
        return new QwenFile(id, Format.OneLine(title, 120), cwd, model, usage);
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long N(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>Qwen finds sessions per project: this runs in the session's folder (see Terminal.OpenAgent).</summary>
    public override List<string> ResumeArgs(AgentSession s) => ["--resume", s.Id];
}
