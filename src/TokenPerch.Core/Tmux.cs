using System.Text.Json.Nodes;

namespace TokenPerch.Core;

/// <summary>
/// Linux: an interactive Claude Code running in a tmux pane (TmuxTarget, e.g. "-L claude -t claude").
/// Lets the model buttons switch that running session and Restart resume it in place.
/// </summary>
public sealed class Tmux(AppConfig cfg)
{
    public bool Configured => !OperatingSystem.IsWindows() && cfg.TmuxTarget.Length > 0 && Proc.Which("tmux") is not null;

    /// <summary>TmuxTarget split into socket args (-L/-S) and target args (-t).</summary>
    (List<string> Socket, List<string> Target) Args()
    {
        var socket = new List<string>();
        var target = new List<string>();
        var words = ShellWords.Split(cfg.TmuxTarget);
        for (int i = 0; i < words.Count; i++)
        {
            var next = i + 1 < words.Count ? words[i + 1] : "";
            if (words[i] is "-L" or "-S") { socket.AddRange([words[i], next]); i++; }
            else if (words[i] == "-t") { target.AddRange([words[i], next]); i++; }
        }
        return (socket, target);
    }

    ProcResult Run(params string[] args) => Proc.Run("tmux", args, TimeSpan.FromSeconds(5));

    public bool HasSession()
    {
        var (s, t) = Args();
        return Run([.. s, "has-session", .. t]).Ok;
    }

    public string? SendModel(string modelId)
    {
        if (!HasSession()) return "Claude Code isn't running in tmux";
        var (s, t) = Args();
        Run([.. s, "send-keys", .. t, "-l", $"/model {modelId}"]);
        Run([.. s, "send-keys", .. t, "Enter"]);
        return null;
    }

    /// <summary>(session id, cwd) of the interactive Claude Code in the pane. Claude Code registers each
    /// running instance in &lt;config dir&gt;/sessions/&lt;pid&gt;.json; the pane's pid is Claude or its shell.</summary>
    (string? Id, string? Cwd) PaneSession()
    {
        var (s, t) = Args();
        var panePid = Run([.. s, "display-message", "-p", .. t, "#{pane_pid}"]).StdOut.Trim();
        if (panePid.Length == 0) return (null, null);
        var kids = Proc.Run("pgrep", ["-P", panePid], TimeSpan.FromSeconds(5)).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pid in kids.Prepend(panePid))
        {
            try
            {
                var info = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Sessions, pid + ".json")));
                if (((string?)info?["kind"] ?? "interactive") == "interactive" && (string?)info?["sessionId"] is { Length: > 0 } sid)
                    return (sid, (string?)info?["cwd"]);
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        }
        return (null, null);
    }

    /// <summary>Restarts Claude Code in its pane (respawn-pane -k keeps the attached window open) and
    /// resumes the same conversation, e.g. to load a new version. Returns a status message.</summary>
    public string Restart()
    {
        var (s, t) = Args();
        var (sid, cwd) = PaneSession();
        var cmd = string.Join(' ', new[] { Quote(ClaudeCli.Exe(cfg)) }.Concat(ClaudeCli.ExtraArgs(cfg).Select(Quote)));
        if (sid is not null) cmd += " --resume " + Quote(sid);
        var r = Run([.. s, "respawn-pane", "-k", .. t, .. (cwd is null ? Array.Empty<string>() : ["-c", cwd]), cmd]);
        if (!r.Ok) return "Restart failed: " + Format.OneLine(r.StdErr, 120);
        return sid is not null ? "Claude Code restarted — same conversation" : "Claude Code restarted — new session";
    }

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
