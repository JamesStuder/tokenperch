using System.Runtime.InteropServices;
using System.Text;

namespace TokenPerch.Core;

/// <summary>
/// Which background sessions have a terminal window open, and raising it.
/// Linux: the terminal's pid (pid file), raised with a one-shot KWin script on Plasma.
/// Windows: the window title ("Claude · name"), raised with SetForegroundWindow.
/// </summary>
public sealed class SessionWindows
{
    static string PidFile(string id) => Path.Combine(Paths.RuntimeDir, $"tokenperch-session-{id}.pid");

    public void Remember(string id, int pid)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.WriteAllText(PidFile(id), pid.ToString()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("pid file", e); }
    }

    public void Forget(string id)
    {
        try { File.Delete(PidFile(id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public bool IsOpen(string id, string name) =>
        OperatingSystem.IsWindows() ? FindWindow(Terminal.WindowTitle(name)) != IntPtr.Zero : Pid(id) is not null;

    public bool Raise(string id, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var h = FindWindow(Terminal.WindowTitle(name));
            if (h == IntPtr.Zero) return false;
            if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
            return SetForegroundWindow(h);
        }
        return Pid(id) is { } pid && KWin.RaisePid(pid);
    }

    static int? Pid(string id)
    {
        try { return int.TryParse(File.ReadAllText(PidFile(id)), out var pid) && Proc.IsAlive(pid) ? pid : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static IntPtr FindWindow(string title)
    {
        var found = IntPtr.Zero;
        var buf = new StringBuilder(512);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            buf.Clear();
            GetWindowText(h, buf, buf.Capacity);
            if (buf.ToString() != title) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
}
