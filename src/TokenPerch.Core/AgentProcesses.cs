using System.Diagnostics;
using System.Text;

namespace TokenPerch.Core;

/// <summary>
/// The agents' running programs: whether an agent is running, and Restart (close it, reopen its most
/// recent session). An agent's interactive program is recognised by its command name (argv[0], or the
/// script an interpreter runs) or, for npm-installed agents run by node, by its package folder.
/// Background parts (OpenCode's service, Hermes' gateway) are not "running" windows.
/// </summary>
public static class AgentProcesses
{
    sealed record Rule(string[] Names, string[] PackageMarkers, string[] ExcludeArgs);

    static readonly Dictionary<string, Rule> Rules = new()
    {
        ["opencode"] = new(["opencode"], ["/opencode-ai/"], ["serve", "web", "acp"]),
        ["hermes"] = new(["hermes"], ["/hermes-agent/hermes_cli/"], ["gateway"]),
        ["codex"] = new(["codex"], ["/@openai/codex/"], ["app-server", "mcp-server"]),
        ["goose"] = new(["goose"], [], ["goosed"]),
        ["pi"] = new(["pi"], ["/pi-coding-agent/"], []),
        ["qwen"] = new(["qwen"], ["/@qwen-code/qwen-code/"], []),
    };

    static readonly HashSet<string> Interpreters = ["node", "bun", "python", "python3", "deno", "sh", "bash"];

    /// <summary>Whether a command line is the agent's interactive program.</summary>
    internal static bool Matches(string agentId, IReadOnlyList<string> argv)
    {
        if (!Rules.TryGetValue(agentId, out var rule) || argv.Count == 0) return false;
        if (argv.Skip(1).Any(a => rule.ExcludeArgs.Contains(a))) return false;
        string Base(string p) => Path.GetFileNameWithoutExtension(p.Replace('\\', '/').Split('/')[^1]);
        if (rule.Names.Contains(Base(argv[0]))) return true;
        var first = Base(argv[0]).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.');
        if (!Interpreters.Contains(first) || argv.Count < 2) return false;
        var script = argv.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "";
        return rule.Names.Contains(Base(script)) || rule.PackageMarkers.Any(m => script.Replace('\\', '/').Contains(m, StringComparison.Ordinal));
    }

    /// <summary>Pids of the agent's interactive program (Linux: /proc; Windows: by process name).</summary>
    public static List<int> Find(Agent agent)
    {
        var pids = new List<int>();
        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
                    string raw;
                    try { raw = File.ReadAllText(Path.Combine(dir, "cmdline")); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                    if (raw.Length == 0) continue;
                    var argv = raw.TrimEnd('\0').Split('\0');
                    if (Matches(agent.Id, argv)) pids.Add(pid);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return pids;
        }
        foreach (var p in Process.GetProcessesByName(agent.Command))
            using (p) pids.Add(p.Id);
        return pids;
    }

    public static bool Running(Agent agent) => Find(agent).Count > 0;

    /// <summary>
    /// Restart: closes the agent's running programs (SIGTERM, so they save and exit; their terminal
    /// windows close with them), waits up to 5 s, restarts OpenCode's background service, then opens
    /// the agent again in a terminal on its most recent session. Returns an error message or null.
    /// </summary>
    public static string? Restart(Agent agent, AgentSession? latest)
    {
        var pids = Find(agent);
        foreach (var pid in pids) Stop(pid);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && pids.Any(Proc.IsAlive)) Thread.Sleep(200);
        foreach (var pid in pids.Where(Proc.IsAlive)) Kill(pid);
        if (agent.Id == "opencode") StopOpenCodeService();
        Log.Info($"restart {agent.Id}: stopped {pids.Count} process(es), reopening {(latest is null ? "a new session" : latest.Id)}");
        return Terminal.OpenAgent(agent, latest);
    }

    /// <summary>OpenCode starts its background service again on its next launch; its address, pid and
    /// local password are in &lt;state&gt;/opencode/service.json.</summary>
    static void StopOpenCodeService()
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } s ? s : Path.Combine(Paths.Home, ".local", "state");
        var file = Path.Combine(state, "opencode", "service.json");
        try
        {
            if (!File.Exists(file)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) && Proc.IsAlive(pid))
            {
                Stop(pid);
                for (int i = 0; i < 25 && Proc.IsAlive(pid); i++) Thread.Sleep(200);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    static void Stop(int pid)
    {
        if (OperatingSystem.IsWindows()) { Kill(pid); return; }
        Proc.Run("kill", ["-TERM", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(3));
    }

    static void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
