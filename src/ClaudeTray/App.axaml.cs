using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClaudeTray.Core;

namespace ClaudeTray;

/// <summary>
/// The tray icon. Left click toggles the panel; the menu (right click) has Open Claude Code,
/// the usage page, Refresh and Quit. Polls: running every 2 s, sessions every 15 s (3 s while the
/// panel is open), usage every 60 s.
/// </summary>
public partial class App : Application
{
    MainViewModel _vm = null!;
    TrayIcon _tray = null!;
    PanelWindow? _panel;
    SettingsWindow? _settings;
    AppConfig _cfg = null!;
    DispatcherTimer _sessionsTimer = null!;
    (bool, double?, double, int) _drawn = (false, -1, -1, -1);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var cfg = _cfg = AppConfig.Load();
        Log.Info($"start {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}; config {AppConfig.FilePath}");
        Appearance.ApplyTheme(cfg);
        _vm = new MainViewModel(cfg);
        _vm.OpenSettings += ShowSettings;
        _vm.TrayStateChanged += UpdateTray;
        _vm.RequestHide += () => _panel?.HidePanel();
        Notifier.PanelOpen = () => _panel?.IsVisible == true;

        var menu = new NativeMenu();
        menu.Items.Add(Item("Open Claude Code", () => _vm.OpenClaude()));
        menu.Items.Add(Item("Show usage panel", TogglePanel));
        menu.Items.Add(Item("Usage on claude.ai", () => Terminal.OpenUrl("https://claude.ai/settings/usage")));
        menu.Items.Add(Item("Refresh", () => { _ = _vm.RefreshUsageAsync(force: true); _ = _vm.RefreshSessionsAsync(); }));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Settings…", ShowSettings));
        menu.Items.Add(Item("Quit Claude Tray", () => desktop.Shutdown()));

        _tray = new TrayIcon
        {
            Icon = TrayIconRenderer.Render(false, null, 0, 0),
            ToolTipText = "Claude Code",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => TogglePanel();
        TrayIcon.SetIcons(this, [_tray]);

        Every(TimeSpan.FromSeconds(2), () => _ = _vm.PollRunningAsync());
        Every(TimeSpan.FromSeconds(60), () => { _ = _vm.RefreshUsageAsync(); _ = _vm.RefreshAgentsAsync(); });
        _sessionsTimer = Every(TimeSpan.FromSeconds(15), () => _ = _vm.RefreshSessionsAsync());
        Every(TimeSpan.FromSeconds(30), () => { if (_panel?.IsVisible == true) _vm.Tick(); });
        // Updates: first check a minute after start, then every 6 hours.
        DispatcherTimer.RunOnce(() => { if (_cfg.CheckForUpdates) _ = _vm.CheckForUpdateAsync(); }, TimeSpan.FromMinutes(1));
        var updates = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updates.Tick += (_, _) => { if (_cfg.CheckForUpdates) _ = _vm.CheckForUpdateAsync(); };
        updates.Start();
    }

    static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    static DispatcherTimer Every(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer { Interval = interval };
        t.Tick += (_, _) => tick();
        t.Start();
        tick();
        return t;
    }

    void TogglePanel()
    {
        _panel ??= CreatePanel();
        if (_panel.IsVisible) { _panel.HidePanel(); return; }
        if (DateTime.UtcNow - _panel.HiddenAt < TimeSpan.FromMilliseconds(400)) return;
        _ = _vm.RefreshUsageAsync();
        _ = _vm.RefreshSessionsAsync();
        _ = _vm.RefreshAgentsAsync();
        _vm.Tick();
        _sessionsTimer.Interval = TimeSpan.FromSeconds(3);
        _panel.ShowPanel();
    }

    void ShowSettings()
    {
        _panel?.HidePanel();
        if (_settings is null)
        {
            var vm = new SettingsViewModel(_cfg, AppearanceChanged, _vm.CheckForUpdateAsync);
            _settings = new SettingsWindow { DataContext = vm };
            _settings.Closed += (_, _) => _settings = null;
            _ = vm.RefreshAuthAsync();
        }
        _settings.Show();
        _settings.Activate();
    }

    void AppearanceChanged()
    {
        Appearance.ApplyTheme(_cfg);
        if (_panel is not null) Appearance.ApplyColors(_cfg, _panel);
    }

    PanelWindow CreatePanel()
    {
        var p = new PanelWindow { DataContext = _vm };
        Appearance.ApplyColors(_cfg, p);
        p.Hidden += () =>
        {
            _sessionsTimer.Interval = TimeSpan.FromSeconds(15);
            _vm.PanelClosed();
        };
        return p;
    }

    void UpdateTray()
    {
        var state = (_vm.Running, _vm.SessionPercent, _vm.MaxPercent, _vm.NeedsCount);
        _tray.ToolTipText = _vm.ToolTip;
        if (state == _drawn) return;
        _drawn = state;
        _tray.Icon = TrayIconRenderer.Render(_vm.Running, _vm.SessionPercent, _vm.MaxPercent, _vm.NeedsCount);
    }
}
