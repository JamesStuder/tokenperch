using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClaudeTray.Core;

namespace ClaudeTray;

/// <summary>The panel: a borderless window placed next to the taskbar / top bar, hidden when it loses focus.</summary>
public partial class PanelWindow : Window
{
    const int Gap = 8;

    public PanelWindow()
    {
        InitializeComponent();
        Activated += (_, _) => Log.Info("panel activated");
        Deactivated += (_, _) => OnDeactivated();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HidePanel(); };
    }

    /// <summary>When the panel last hid itself. A click on the tray icon first takes focus away from the
    /// panel (hiding it), then arrives as a click: that click must not reopen it.</summary>
    public DateTime HiddenAt { get; private set; }

    public event Action? Hidden;

    DateTime _shownAt;

    /// <summary>Clicking the tray icon gives focus back to the tray when the button is released, right
    /// after the panel opened. Losing focus in the first second therefore doesn't close the panel: it
    /// takes the focus back. After that, clicking anywhere else closes it.</summary>
    void OnDeactivated()
    {
        if (!IsVisible) return;
        if (DateTime.UtcNow - _shownAt < TimeSpan.FromSeconds(1))
        {
            Log.Info("panel lost focus while opening; taking it back");
            var title = Title ?? "";
            Task.Run(async () =>
            {
                await Task.Delay(150);
                if (!KWin.Available || !KWin.ActivateWindow(Environment.ProcessId, title))
                    Dispatcher.UIThread.Post(Activate);
            });
            return;
        }
        Log.Info("panel lost focus");
        HidePanel();
    }

    public void ShowPanel()
    {
        _shownAt = DateTime.UtcNow;
        if (WinPlacement.PanelNearPointer(Width, MaxPanelHeight, Gap) is { } wp)
        {
            Height = wp.Height;
            Position = new PixelPoint(wp.X, wp.Y);
            Show();
            Activate();
            Log.Info($"panel shown at {Position} (pointer placement, scale {wp.Scale}) size {Width}x{Height}");
            return;
        }
        Place();
        if (!KWin.Available)
        {
            Show();
            Activate();
            Log.Info($"panel shown at {Position} size {Width}x{Height}");
            return;
        }
        // Plasma: KWin knows where the pointer and the panels are (an XWayland client doesn't), so it
        // moves the panel next to the pointer. Invisible until then, so it doesn't jump.
        Opacity = 0;
        Show();
        Activate();
        int w = (int)Width, h = (int)MaxPanelHeight;
        var title = Title ?? "";
        Task.Run(async () =>
        {
            await Task.Delay(60);
            bool ok = false;
            try { ok = KWin.PlacePanel(Environment.ProcessId, title, w, h, Gap); }
            catch (Exception e) { Log.Error("KWin placement", e); }
            Dispatcher.UIThread.Post(() =>
            {
                Opacity = 1;
                Log.Info($"panel shown via KWin ({(ok ? "placed" : "not placed")}) at {Position} size {Bounds.Width}x{Bounds.Height}");
            });
        });
    }

    const double MaxPanelHeight = 720;

    public void HidePanel()
    {
        if (!IsVisible) return;
        HiddenAt = DateTime.UtcNow;
        Hide();
        Log.Info("panel hidden");
        Hidden?.Invoke();
    }

    /// <summary>Top-right under a top panel, bottom-right above a bottom taskbar, left for a left taskbar.</summary>
    void Place()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        double scale = screen.Scaling;
        Height = Math.Min(MaxPanelHeight, work.Height / scale - 2 * Gap);
        int w = (int)(Width * scale), h = (int)(Height * scale), m = (int)(Gap * scale);
        int x = work.X > bounds.X ? work.X + m : work.Right - w - m;
        int y = work.Y > bounds.Y ? work.Y + m : work.Bottom - h - m;
        Position = new PixelPoint(x, y);
    }

    void SessionTapped(object? sender, TappedEventArgs e)
    {
        // The remove button sits inside the row; its clicks are not row clicks.
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if ((sender as Control)?.DataContext is SessionVM s) s.Open.Execute(null);
    }

    void AgentSessionTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if ((sender as Control)?.DataContext is AgentSessionVM s) s.Open.Execute(s);
    }
}
