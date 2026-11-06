using System.Runtime.InteropServices;

namespace TokenPerch.Core;

/// <summary>
/// Windows: where the panel goes, asked from Windows at click time: the pointer (on the tray icon) and the
/// work area (outside the taskbar) of the monitor it's on, in physical pixels. Avalonia's cached screen list
/// can be stale, e.g. after a Remote Desktop window was resized, which put the panel off screen.
/// </summary>
public static class WinPlacement
{
    /// <summary>Top-left (physical px) and the height (DIP) for a panel of <paramref name="width"/> × at most
    /// <paramref name="maxHeight"/> DIP next to the pointer: above it when the pointer is in the lower half
    /// of the monitor (taskbar at the bottom), else below it; clamped into the work area.</summary>
    public static (int X, int Y, double Height, double Scale)? PanelNearPointer(double width, double maxHeight, int gapDip)
    {
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var p)) return null;
        var mon = MonitorFromPoint(p, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info)) return null;
        double scale = GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        var work = info.rcWork;
        int gap = (int)(gapDip * scale);
        int workW = work.Right - work.Left, workH = work.Bottom - work.Top;
        double heightDip = Math.Min(maxHeight, workH / scale - 2 * gapDip);
        int w = (int)Math.Min(width * scale, workW - 2 * gap), h = (int)(heightDip * scale);
        int x = Math.Clamp(p.X - w / 2, work.Left + gap, work.Right - w - gap);
        int y = p.Y - work.Top > workH / 2 ? work.Bottom - h - gap : work.Top + gap;
        return (x, y, heightDip, scale);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
