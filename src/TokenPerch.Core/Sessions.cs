using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenPerch.Core;

public enum SessionGroup { Needs, Working, Done, Failed }

public sealed record SessionInfo(string Id, string Name, SessionGroup Group, string Text, long Updated, bool Open);

public sealed record SessionsResult(List<SessionInfo> Sessions, string? Error = null);

/// <summary>
/// Background sessions from `claude agents --json --all`, with the status line and result from
/// &lt;config dir&gt;/jobs/&lt;id&gt;/state.json.
/// </summary>
public static class SessionsService
{
    public static SessionsResult Get(AppConfig cfg, SessionWindows windows)
    {
        var r = ClaudeCli.Run(cfg, "agents", "--json", "--all");
        if (!r.Ok)
        {
            var why = Format.OneLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut, 120);
            return new SessionsResult([], $"claude agents failed{(why.Length > 0 ? ": " + why : "")}");
        }
        try { return new SessionsResult(Parse(r.StdOut, ReadState, windows.IsOpen)); }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return new SessionsResult([], "claude agents: unexpected output");
        }
    }

    static JsonNode? ReadState(string id)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Jobs, id, "state.json"))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static List<SessionInfo> Parse(string agentsJson, Func<string, JsonNode?> readState, Func<string, string, bool> isOpen)
    {
        var list = new List<SessionInfo>();
        foreach (var a in JsonNode.Parse(agentsJson)?.AsArray() ?? [])
        {
            if (a is null || (string?)a["kind"] != "background" || (string?)a["id"] is not { } id) continue;
            var st = readState(id);
            var group = (string?)a["state"] switch
            {
                "blocked" => SessionGroup.Needs,
                "done" or "stopped" => SessionGroup.Done,
                "failed" => SessionGroup.Failed,
                _ => SessionGroup.Working,
            };
            var text = group switch
            {
                SessionGroup.Needs => Text(st?["needs"]) ?? Text(st?["detail"]),
                SessionGroup.Working => Text(st?["detail"]),
                _ => Text(st?["output"]) ?? Text(st?["detail"]),
            };
            long updated = DateTimeOffset.TryParse((string?)st?["updatedAt"], out var u)
                ? u.ToUnixTimeMilliseconds()
                : a["startedAt"]?.GetValue<long>() ?? 0;
            var name = (string?)a["name"] is { Length: > 0 } n && n != id ? n : Format.OneLine(Text(st?["intent"]), 60);
            if (string.IsNullOrEmpty(name)) name = "Untitled session";
            list.Add(new SessionInfo(id, name, group, Format.OneLine(text), updated, isOpen(id, name)));
        }
        list.Sort((x, y) => y.Updated.CompareTo(x.Updated));
        return list;
    }

    /// <summary>A state field as text: a string, or an object's "result".</summary>
    static string? Text(JsonNode? v) => v switch
    {
        null => null,
        JsonObject o => Text(o["result"]),
        JsonValue s when s.TryGetValue<string>(out var str) => str.Length > 0 ? str : null,
        _ => v.ToJsonString(),
    };

    /// <summary>`claude rm id`: drops it from the list. The conversation log is kept.</summary>
    public static string? Remove(AppConfig cfg, SessionWindows windows, string id)
    {
        var r = ClaudeCli.Run(cfg, "rm", id);
        windows.Forget(id);
        return r.Ok ? null : Format.OneLine(r.StdOut + " " + r.StdErr, 200);
    }
}
