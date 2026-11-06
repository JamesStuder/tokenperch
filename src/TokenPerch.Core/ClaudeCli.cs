using System.Diagnostics;

namespace TokenPerch.Core;

/// <summary>The Claude Code executable, and whether it is running.</summary>
public static class ClaudeCli
{
    /// <summary>ClaudeCommand from the config, else the native installer's ~/.local/bin/claude, else PATH.
    /// (An app started at login may not have ~/.local/bin on its PATH.)</summary>
    public static string Exe(AppConfig cfg)
    {
        if (ShellWords.Split(cfg.ClaudeCommand) is [var first, ..]) return first;
        var local = Path.Combine(Paths.Home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        if (File.Exists(local)) return local;
        return Proc.Which("claude") ?? "claude";
    }

    /// <summary>Extra words from ClaudeCommand after the executable (e.g. "--model x").</summary>
    public static List<string> ExtraArgs(AppConfig cfg) => ShellWords.Split(cfg.ClaudeCommand).Skip(1).ToList();

    public static ProcResult Run(AppConfig cfg, params string[] args) =>
        Proc.Run(Exe(cfg), args, TimeSpan.FromSeconds(30));

    /// <summary>Any Claude Code process running (interactive or a background session's).</summary>
    public static bool AnyRunning()
    {
        var procs = Process.GetProcessesByName("claude");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}
