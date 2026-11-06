using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenPerch.Core;

/// <summary>An agent's installed and newest version, and how to update it.</summary>
public sealed record AgentUpdate(string Installed, string Latest, string Method, IReadOnlyList<string> Command, string Note)
{
    public bool Available => Latest.Length > 0 && Latest != Installed;
}

/// <summary>
/// Update checks for the agents, by how each is installed:
/// - a pacman package (Arch/CachyOS): newest from `checkupdates` (no root; it syncs a temporary copy of
///   the package database). Updating means a full system update (`paru -Syu`, else `sudo pacman -Syu`):
///   updating one package alone is a partial upgrade, which Arch doesn't support.
/// - a global npm package: newest from `npm view &lt;package&gt; version`; update with
///   `npm install -g &lt;package&gt;@latest` (with sudo when the npm folder isn't yours).
/// Other installs (own installers, Windows) get no check. Results are kept for 6 hours.
/// </summary>
public static partial class AgentUpdates
{
    static readonly Dictionary<string, string> NpmPackages = new()
    {
        ["opencode"] = "opencode-ai",
        ["codex"] = "@openai/codex",
        ["pi"] = "@earendil-works/pi-coding-agent",
        ["qwen"] = "@qwen-code/qwen-code",
    };

    static readonly Dictionary<string, (DateTime At, AgentUpdate? Result)> Cache = new();
    static (DateTime At, Dictionary<string, string> Updates)? _pacman;
    static readonly object Gate = new();

    /// <summary>Checks one agent (cached for 6 hours unless <paramref name="force"/>). Null = no check possible.</summary>
    public static AgentUpdate? Check(Agent agent, bool force = false)
    {
        lock (Gate)
        {
            if (!force && Cache.TryGetValue(agent.Id, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromHours(6)) return c.Result;
        }
        AgentUpdate? result = null;
        try { result = CheckNow(agent, force); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error($"{agent.Id} update check", e);
        }
        lock (Gate) Cache[agent.Id] = (DateTime.UtcNow, result);
        if (result is { Available: true }) Log.Info($"{agent.Id}: {result.Latest} available (installed {result.Installed}, {result.Method})");
        return result;
    }

    /// <summary>Every file named <paramref name="command"/> on PATH, in order (a wrapper script may come
    /// first, e.g. a launcher in ~/.local/bin in front of the packaged program).</summary>
    static IEnumerable<string> AllOnPath(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, command)).Where(File.Exists).Select(Resolve).Distinct();

    static AgentUpdate? CheckNow(Agent agent, bool force)
    {
        if (OperatingSystem.IsWindows()) return null;
        foreach (var real in AllOnPath(agent.Command))
            if (CheckInstall(agent, real, force) is { } u) return u;
        return null;
    }

    static AgentUpdate? CheckInstall(Agent agent, string real, bool force)
    {
        // pacman package?
        if (Proc.Which("pacman") is { } pacman)
        {
            var owner = Proc.Run(pacman, ["-Qqo", real], TimeSpan.FromSeconds(10));
            if (owner.Ok && owner.StdOut.Trim() is { Length: > 0 } pkg)
            {
                var q = Proc.Run(pacman, ["-Q", pkg], TimeSpan.FromSeconds(10));
                var installed = q.Ok && q.StdOut.Trim().Split(' ') is { Length: 2 } parts ? parts[1] : "";
                var latest = PacmanUpdates(force).GetValueOrDefault(pkg, "");
                List<string> cmd = Proc.Which("paru") is { } paru ? [paru, "-Syu"]
                    : Proc.Which("yay") is { } yay ? [yay, "-Syu"] : ["sudo", pacman, "-Syu"];
                return new AgentUpdate(installed, latest, $"pacman package {pkg}", cmd,
                    "Updates the whole system (with your password), as Arch updates packages together.");
            }
        }

        // global npm package?
        if (NpmPackages.TryGetValue(agent.Id, out var npmPkg) && PackageRoot(real, npmPkg) is { } root && Proc.Which("npm") is { } npm)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
            var installed = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var view = Proc.Run(npm, ["view", npmPkg, "version"], TimeSpan.FromSeconds(30));
            var latest = view.Ok ? view.StdOut.Trim() : "";
            bool mine = IsWritable(Path.GetDirectoryName(root)!);
            List<string> cmd = mine ? [npm, "install", "-g", npmPkg + "@latest"] : ["sudo", npm, "install", "-g", npmPkg + "@latest"];
            return new AgentUpdate(installed, latest, $"npm package {npmPkg}", cmd, "");
        }
        return null;
    }

    /// <summary>`checkupdates` output ("name old -> new"), shared by all pacman-installed agents.</summary>
    static Dictionary<string, string> PacmanUpdates(bool force)
    {
        lock (Gate)
            if (!force && _pacman is { } p && DateTime.UtcNow - p.At < TimeSpan.FromHours(6)) return p.Updates;
        var map = new Dictionary<string, string>();
        if (Proc.Which("checkupdates") is { } cu)
        {
            // Exit 2 = no updates; output lines: "opencode 2.2.5-1 -> 2.2.6-1"
            var r = Proc.Run(cu, [], TimeSpan.FromMinutes(2));
            foreach (var line in r.StdOut.Split('\n'))
                if (UpdateLine().Match(line) is { Success: true } m) map[m.Groups[1].Value] = m.Groups[2].Value;
        }
        lock (Gate) _pacman = (DateTime.UtcNow, map);
        return map;
    }

    [GeneratedRegex(@"^(\S+)\s+\S+\s+->\s+(\S+)")]
    private static partial Regex UpdateLine();

    static string Resolve(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (IOException) { return path; }
    }

    /// <summary>The npm package folder (…/node_modules/&lt;package&gt;) the command resolves into.</summary>
    internal static string? PackageRoot(string realPath, string package)
    {
        var marker = "/node_modules/" + package + "/";
        var p = realPath.Replace('\\', '/');
        int i = p.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var root = p[..(i + marker.Length - 1)];
        return File.Exists(Path.Combine(root, "package.json")) ? root : null;
    }

    static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".tokenperch-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
