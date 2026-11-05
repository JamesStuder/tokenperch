namespace ClaudeTray.Core;

/// <summary>Opening Claude Code (or a background session) in a terminal window, on Linux and Windows.</summary>
public static class Terminal
{
    // Linux terminals tried in order, with the words that come before the command to run.
    // Only konsole gets a title: --separate keeps it its own process, so its window can be raised later.
    static readonly (string Exe, string[] Prefix)[] LinuxTerminals =
    [
        ("konsole", ["--separate", "-p", "tabtitle={title}", "-e"]),
        ("gnome-terminal", ["--"]),
        ("kgx", ["--"]),
        ("xfce4-terminal", ["-x"]),
        ("alacritty", ["-e"]),
        ("kitty", []),
        ("wezterm", ["start", "--"]),
        ("foot", []),
        ("xterm", ["-e"]),
        ("x-terminal-emulator", ["-e"]),
    ];

    public static string WindowTitle(string sessionName) => $"Claude · {sessionName}";

    /// <summary>Command that runs <paramref name="program"/> in a new terminal window titled <paramref name="title"/>.</summary>
    public static List<string>? Wrap(IReadOnlyList<string> program, string title)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Proc.Which("wt.exe") is { } wt)
                return [wt, "-w", "new", "--title", title, "--suppressApplicationTitle", .. program];
            // conhost: start's first quoted argument is the window title (ours always has a space).
            return ["cmd.exe", "/c", "start", title, .. program];
        }
        foreach (var (exe, prefix) in LinuxTerminals)
            if (Proc.Which(exe) is { } path)
                return [path, .. prefix.Select(p => p.Replace("{title}", title)), .. program];
        return null;
    }

    /// <summary>Fills {claude}, {id}, {name}, {title} in a configured command line.</summary>
    public static List<string> Expand(string template, string claude, string id = "", string name = "") =>
        ShellWords.Split(template)
            .Select(w => w.Replace("{claude}", claude).Replace("{id}", id).Replace("{name}", name)
                          .Replace("{title}", WindowTitle(name)))
            .ToList();

    /// <summary>"Open Claude Code": OpenCommand if set (through sh -c on Linux), else a terminal running claude.</summary>
    public static string? OpenClaude(AppConfig cfg)
    {
        if (cfg.OpenCommand.Length > 0)
        {
            var cmd = OperatingSystem.IsWindows()
                ? Expand(cfg.OpenCommand, ClaudeCli.Exe(cfg))
                : ["sh", "-c", cfg.OpenCommand];
            return Proc.Start(cmd) is null ? "Couldn't run openCommand — check config.json" : null;
        }
        var wrapped = Wrap([ClaudeCli.Exe(cfg), .. ClaudeCli.ExtraArgs(cfg)], "Claude Code");
        if (wrapped is null) return "No terminal found — set openCommand in config.json";
        return Proc.Start(wrapped) is null ? "Couldn't start a terminal" : null;
    }

    /// <summary>Raises the session's window if it is open, else opens a terminal running `claude attach id`.</summary>
    public static string? OpenSession(AppConfig cfg, SessionWindows windows, string id, string name)
    {
        if (windows.IsOpen(id, name) && windows.Raise(id, name)) return null;
        var claude = ClaudeCli.Exe(cfg);
        var cmd = cfg.SessionCommand.Length > 0
            ? Expand(cfg.SessionCommand, claude, id, name)
            : Wrap([claude, "attach", id], WindowTitle(name));
        if (cmd is null) return "No terminal found — set sessionCommand in config.json";
        // On Linux, a systemd scope of its own: the window outlives this app. systemd-run --scope execs
        // the command in place, so the pid is the terminal's (needed to raise the window later).
        if (OperatingSystem.IsLinux() && Proc.Which("systemd-run") is { } run)
            cmd = [run, "--user", "--scope", "--quiet", "--collect", $"--unit=claude-tray-session-{id}-{Environment.ProcessId}", .. cmd];
        var pid = Proc.Start(cmd);
        if (pid is null) return "Couldn't start a terminal";
        windows.Remember(id, pid.Value);
        return null;
    }

    /// <summary>Opens another agent (OpenCode, Hermes) in a terminal: new, or resuming one of its sessions.</summary>
    public static string? OpenAgent(Agent agent, AgentSession? session = null)
    {
        if (Proc.Which(agent.Command) is not { } exe) return $"{agent.Name} isn't on PATH";
        var title = session is null ? agent.Name : $"{agent.Name} · {Format.OneLine(session.Title, 40)}";
        var cwd = session is { Directory.Length: > 0 } && Directory.Exists(session.Directory) ? session.Directory : null;
        var cmd = Wrap([exe, .. session is null ? [] : agent.ResumeArgs(session)], title);
        if (cmd is null) return "No terminal found";
        if (OperatingSystem.IsLinux() && Proc.Which("systemd-run") is { } run)
            cmd = [run, "--user", "--scope", "--quiet", "--collect", .. cmd];
        return Proc.Start(cmd, cwd) is null ? "Couldn't start a terminal" : null;
    }

    public static void OpenUrl(string url)
    {
        if (OperatingSystem.IsWindows())
            Proc.Start(["cmd.exe", "/c", "start", "", url], hideWindow: true);
        else
            Proc.Start(["xdg-open", url]);
    }
}
