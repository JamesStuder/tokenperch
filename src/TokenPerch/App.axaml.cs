using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TokenPerch.Core;

namespace TokenPerch;

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
    int _drawn = -1;

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

        _desktop = desktop;
        _tray = new TrayIcon
        {
            Icon = TrayIconRenderer.Render(0),
            ToolTipText = "Token Perch",
            Menu = _menu,
            IsVisible = true,
        };
        BuildMenu();
        _vm.MenuChanged += BuildMenu;
        _tray.Clicked += (_, _) => TogglePanel();
        TrayIcon.SetIcons(this, [_tray]);

        Every(TimeSpan.FromSeconds(2), () => _ = _vm.PollRunningAsync());
        Every(TimeSpan.FromSeconds(60), () => _ = _vm.RefreshUsageAsync());
        _sessionsTimer = Every(TimeSpan.FromSeconds(15), () => { _ = _vm.RefreshSessionsAsync(); _ = _vm.RefreshAgentsAsync(); });
        Every(TimeSpan.FromSeconds(30), () => { if (_panel?.IsVisible == true) _vm.Tick(); });
        // Updates: first check a minute after start, then every 6 hours.
        DispatcherTimer.RunOnce(() => { if (_cfg.CheckForUpdates) _ = _vm.CheckForUpdateAsync(); }, TimeSpan.FromMinutes(1));
        var updates = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updates.Tick += (_, _) => { if (_cfg.CheckForUpdates) { _ = _vm.CheckForUpdateAsync(); _ = _vm.CheckAgentUpdatesAsync(); } };
        updates.Start();
    }

    readonly NativeMenu _menu = new();
    IClassicDesktopStyleApplicationLifetime _desktop = null!;

    /// <summary>
    /// The right-click menu: Show panel · Open ▸ (Claude Code and each agent) · Waiting for you (the
    /// sessions waiting, click opens one) · Claude usage on claude.ai · Refresh · Settings… · Quit.
    /// Rebuilt when the agents or the waiting sessions change.
    /// </summary>
    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(Item("Show panel", TogglePanel));
        var open = new NativeMenu();
        open.Items.Add(Item(_vm.Running ? "Claude Code (running)" : "Claude Code", () => _vm.OpenClaude()));
        foreach (var a in _vm.MenuAgents)
            open.Items.Add(Item(_vm.IsAgentRunning(a) ? $"{a.Name} (running)" : a.Name, () => _vm.OpenAgent(a)));
        _menu.Items.Add(new NativeMenuItem("Start / Open") { Menu = open });

        // Restart: what is running (Claude Code only through its tmux pane).
        var restart = new NativeMenu();
        if (_vm.CanRestartClaude) restart.Items.Add(Item("Claude Code", () => _vm.RestartClaude()));
        foreach (var a in _vm.MenuAgents.Where(_vm.IsAgentRunning)) restart.Items.Add(Item(a.Name, () => _vm.RestartAgent(a)));
        if (restart.Items.Count > 0) _menu.Items.Add(new NativeMenuItem("Restart") { Menu = restart });

        var updates = _vm.AvailableUpdates.ToList();
        if (updates.Count > 0)
        {
            _menu.Items.Add(new NativeMenuItemSeparator());
            foreach (var (a, u) in updates) _menu.Items.Add(Item($"Update {a.Name} to {u.Latest}", () => _vm.UpdateAgent(a)));
        }

        var waiting = _vm.Waiting;
        if (waiting.Count > 0)
        {
            _menu.Items.Add(new NativeMenuItemSeparator());
            _menu.Items.Add(new NativeMenuItem($"Waiting for you ({waiting.Count})") { IsEnabled = false });
            foreach (var w in waiting.Take(10)) _menu.Items.Add(Item("? " + w.Label, w.Open));
        }

        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item("Claude usage on claude.ai", () => Terminal.OpenUrl("https://claude.ai/settings/usage")));
        _menu.Items.Add(Item("Refresh", () =>
        {
            _ = _vm.RefreshUsageAsync(force: true);
            _ = _vm.RefreshSessionsAsync();
            _ = _vm.RefreshAgentsAsync();
        }));
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item("Settings…", ShowSettings));
        _menu.Items.Add(Item("Quit Token Perch", () => _desktop.Shutdown()));
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
            var vm = new SettingsViewModel(_cfg, AppearanceChanged, _vm.CheckForUpdateAsync, _vm.AgentSettingsChanged);
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
        var state = _vm.NeedsCount;
        _tray.ToolTipText = _vm.ToolTip;
        if (state == _drawn) return;
        _drawn = state;
        _tray.Icon = TrayIconRenderer.Render(_vm.NeedsCount);
    }
}
