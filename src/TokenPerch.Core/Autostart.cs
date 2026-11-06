namespace TokenPerch.Core;

/// <summary>
/// Start at login, the same way the installers set it up: on Linux an autostart entry
/// ($XDG_CONFIG_HOME/autostart/tokenperch.desktop), on Windows the per-user Run key ("TokenPerch").
/// </summary>
public static class Autostart
{
    static string DesktopFile(string? configHome = null) =>
        Path.Combine(configHome ?? (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Paths.Home, ".config")),
            "autostart", "tokenperch.desktop");

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue("TokenPerch") is not null;
            }
            return File.Exists(DesktopFile());
        }
    }

    /// <summary>Turns start at login on or off for this executable. Returns an error message or null.</summary>
    public static string? Set(bool on, string? exe = null, string? configHome = null)
    {
        exe ??= Environment.ProcessPath ?? "";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) key.SetValue("TokenPerch", $"\"{exe}\"");
                else key.DeleteValue("TokenPerch", throwOnMissingValue: false);
                return null;
            }
            var file = DesktopFile(configHome);
            if (!on) { File.Delete(file); return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, DesktopEntry(exe));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("autostart", e);
            return "Couldn't change start at login: " + e.Message;
        }
    }

    /// <summary>The same entry the Linux installer writes.</summary>
    internal static string DesktopEntry(string exe) => $"""
        [Desktop Entry]
        Type=Application
        Name=Token Perch
        Comment=Coding agent sessions, models and usage in the system tray
        Exec={exe}
        Icon=tokenperch
        Terminal=false
        Categories=Development;Utility;
        StartupNotify=false
        X-GNOME-Autostart-enabled=true

        """;
}
