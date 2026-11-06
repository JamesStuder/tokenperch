namespace TokenPerch.Core;

/// <summary>One-shot KWin scripts over D-Bus (KDE Plasma only): raising a window, placing the panel.</summary>
public static class KWin
{
    public static bool Available =>
        OperatingSystem.IsLinux()
        && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase)
        && Qdbus is not null;

    static string? Qdbus => Proc.Which("qdbus6") ?? Proc.Which("qdbus");

    /// <summary>Loads, runs and unloads a script. Returns false if KWin isn't reachable.</summary>
    public static bool Run(string name, string js)
    {
        if (Qdbus is not { } qdbus) return false;
        var file = Path.Combine(Paths.RuntimeDir, name + ".js");
        try { File.WriteAllText(file, js); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        string[] scripting = ["org.kde.KWin", "/Scripting"];
        Proc.Run(qdbus, [.. scripting, "org.kde.kwin.Scripting.unloadScript", name]);
        var n = Proc.Run(qdbus, [.. scripting, "org.kde.kwin.Scripting.loadScript", file, name]).StdOut.Trim();
        if (n.Length == 0 || n == "-1") return false;
        Proc.Run(qdbus, ["org.kde.KWin", $"/Scripting/Script{n}", "org.kde.kwin.Script.run"]);
        Proc.Run(qdbus, [.. scripting, "org.kde.kwin.Scripting.unloadScript", name]);
        return true;
    }

    public static bool RaisePid(int pid) => Run("tokenperch-focus",
        "for (const w of workspace.windowList()) {\n" +
        $"    if (w.pid === {pid}) {{ w.minimized = false; workspace.activeWindow = w; break; }}\n}}\n");

    public static bool ActivateWindow(int pid, string caption) => Run("tokenperch-activate", $$"""
        for (const w of workspace.windowList()) {
            if (w.pid === {{pid}} && w.caption === {{System.Text.Json.JsonSerializer.Serialize(caption)}}) { workspace.activeWindow = w; break; }
        }
        """);

    /// <summary>Moves our panel next to the mouse pointer, inside the usable area (outside panels) of the
    /// screen the pointer is on: top half → under the top edge, bottom half → above the bottom edge.
    /// Also activates it, so clicking elsewhere closes it.</summary>
    public static bool PlacePanel(int pid, string caption, int width, int height, int gap = 8) => Run("tokenperch-place", $$"""
        const c = workspace.cursorPos;
        const area = workspace.clientArea(KWin.PlacementArea, workspace.screenAt(c), workspace.currentDesktop);
        for (const w of workspace.windowList()) {
            if (w.pid !== {{pid}} || w.caption !== {{System.Text.Json.JsonSerializer.Serialize(caption)}}) continue;
            const h = Math.min({{height}}, area.height - 2 * {{gap}});
            const wd = Math.min({{width}}, area.width - 2 * {{gap}});
            const x = Math.min(Math.max(c.x - wd / 2, area.x + {{gap}}), area.x + area.width - wd - {{gap}});
            const y = (c.y - area.y < area.height / 2) ? area.y + {{gap}} : area.y + area.height - h - {{gap}};
            w.frameGeometry = { x: x, y: y, width: wd, height: h };
            workspace.activeWindow = w;
            break;
        }
        """);
}
