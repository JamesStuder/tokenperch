namespace TokenPerch.Core;

/// <summary>Where Claude Code keeps its files, and where this app keeps its own.</summary>
public static class Paths
{
    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Claude Code's config dir: CLAUDE_CONFIG_DIR, else ~/.claude (%USERPROFILE%\.claude on Windows).</summary>
    public static string ClaudeDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Home, ".claude");

    public static string Credentials => Path.Combine(ClaudeDir, ".credentials.json");
    public static string Projects => Path.Combine(ClaudeDir, "projects");
    public static string Jobs => Path.Combine(ClaudeDir, "jobs");
    public static string Sessions => Path.Combine(ClaudeDir, "sessions");
    public static string ClaudeSettings => Path.Combine(ClaudeDir, "settings.json");

    public static string ConfigDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenPerch")
        : Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "tokenperch");

    /// <summary>Where the settings were before 3.0, when Token Perch was called Claude Tray.</summary>
    public static string LegacyConfigDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeTray")
        : Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "claude-tray");

    public static string CacheDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenPerch", "cache")
        : Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), "tokenperch");

    /// <summary>Short-lived files (window pid files). XDG_RUNTIME_DIR on Linux, %TEMP% on Windows.</summary>
    public static string RuntimeDir => OperatingSystem.IsWindows()
        ? Path.Combine(Path.GetTempPath(), "TokenPerch")
        : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r ? r : Path.GetTempPath();

    static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v : Path.Combine(Home, fallback);
}
