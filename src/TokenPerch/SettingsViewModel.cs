using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using TokenPerch.Core;

namespace TokenPerch;

/// <summary>One custom colour: off = the theme's own, on = the picked colour.</summary>
public sealed class ColorSetting : ObservableObject
{
    readonly Func<string?> _get;
    readonly Action<string?> _set;
    readonly Action _changed;
    Color _color;

    public ColorSetting(string label, Color fallback, Func<string?> get, Action<string?> set, Action changed)
    {
        Label = label;
        _get = get;
        _set = set;
        _changed = changed;
        _color = Appearance.Parse(get()) ?? fallback;
    }

    public string Label { get; }

    public bool Custom
    {
        get => _get() is { Length: > 0 };
        set
        {
            _set(value ? Appearance.Hex(_color) : null);
            Raise();
            _changed();
        }
    }

    public Color Color
    {
        get => _color;
        set
        {
            if (!Set(ref _color, value)) return;
            if (!Custom) { _set(Appearance.Hex(value)); Raise(nameof(Custom)); }
            else _set(Appearance.Hex(value));
            _changed();
        }
    }

    public void Reload()
    {
        _color = Appearance.Parse(_get()) ?? _color;
        Raise(nameof(Color));
        Raise(nameof(Custom));
    }
}

/// <summary>The Settings window: Claude Code login, appearance, support, about.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    readonly AppConfig _cfg;
    readonly Action _appearanceChanged;
    readonly Func<Task<string>> _checkUpdates;

    public SettingsViewModel(AppConfig cfg, Action appearanceChanged, Func<Task<string>> checkUpdates)
    {
        _cfg = cfg;
        _checkUpdates = checkUpdates;
        _appearanceChanged = appearanceChanged;
        Colors =
        [
            new("Background", Color.Parse("#202020"), () => cfg.Colors.Background, v => cfg.Colors.Background = v, ColorsChanged),
            new("Text", Color.Parse("#FFFFFF"), () => cfg.Colors.Text, v => cfg.Colors.Text = v, ColorsChanged),
            new("Buttons", Color.Parse("#3A3A3A"), () => cfg.Colors.Button, v => cfg.Colors.Button = v, ColorsChanged),
            new("Button text", Color.Parse("#FFFFFF"), () => cfg.Colors.ButtonText, v => cfg.Colors.ButtonText = v, ColorsChanged),
        ];
        RefreshAuthCommand = new RelayCommand(_ => _ = RefreshAuthAsync());
        LoginCommand = new RelayCommand(_ => Login());
        LogoutCommand = new RelayCommand(_ => Logout());
        ResetColorsCommand = new RelayCommand(_ => ResetColors());
        IssueCommand = new RelayCommand(_ => Terminal.OpenUrl(Support.IssuesUrl));
        ProjectCommand = new RelayCommand(_ => Terminal.OpenUrl(Support.ProjectUrl));
        OpenConfigCommand = new RelayCommand(_ => OpenFile(AppConfig.FilePath));
        CheckUpdatesCommand = new RelayCommand(async _ => { UpdateCheckStatus = "Checking…"; UpdateCheckStatus = await _checkUpdates(); });
        OpenLogCommand = new RelayCommand(_ => OpenFile(Log.FilePath));
    }

    // ---- account

    string _authTitle = "Checking…";
    public string AuthTitle { get => _authTitle; private set => Set(ref _authTitle, value); }
    string _authDetail = "";
    public string AuthDetail { get => _authDetail; private set => Set(ref _authDetail, value); }
    string _accountStatus = "";
    public string AccountStatus { get => _accountStatus; private set => Set(ref _accountStatus, value); }
    bool _loggedIn;
    public bool LoggedIn { get => _loggedIn; private set { Set(ref _loggedIn, value); Raise(nameof(LoginText)); } }
    public string LoginText => LoggedIn ? "Log in again" : "Log in";
    bool _confirmLogout;
    public string LogoutText => _confirmLogout ? "Click again to log out" : "Log out";
    public ICommand RefreshAuthCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }

    public async Task RefreshAuthAsync()
    {
        AuthTitle = "Checking…";
        var s = await Task.Run(() => ClaudeAuth.Status(_cfg));
        LoggedIn = s.LoggedIn;
        AuthTitle = s.LoggedIn ? $"Logged in as {s.Email}" : "Not logged in";
        AuthDetail = s.LoggedIn
            ? string.Join(" · ", new[] { s.Organization, Cap(s.Subscription), s.Method }.Where(x => x.Length > 0))
            : s.Error ?? "Claude Code needs a login before it (and the usage limits here) work.";
    }

    void Login()
    {
        var err = ClaudeAuth.Login(_cfg);
        AccountStatus = err ?? "A terminal opened with claude auth login: finish in the browser, then click Refresh.";
    }

    void Logout()
    {
        if (!_confirmLogout)
        {
            _confirmLogout = true;
            Raise(nameof(LogoutText));
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() => { _confirmLogout = false; Raise(nameof(LogoutText)); }));
            return;
        }
        _confirmLogout = false;
        Raise(nameof(LogoutText));
        Task.Run(() => ClaudeAuth.Logout(_cfg)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            AccountStatus = t.Result ?? "Logged out of Claude Code.";
            _ = RefreshAuthAsync();
        }));
    }

    static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    // ---- appearance

    public string[] Themes { get; } = ["Follow system", "Light", "Dark"];
    public int ThemeIndex
    {
        get => _cfg.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        set
        {
            _cfg.Theme = value switch { 1 => "light", 2 => "dark", _ => "system" };
            Raise();
            SaveAppearance();
        }
    }
    public List<ColorSetting> Colors { get; }
    public ICommand ResetColorsCommand { get; }

    void ColorsChanged() => SaveAppearance();

    void ResetColors()
    {
        _cfg.Colors = new PanelColors();
        foreach (var c in Colors) c.Reload();
        SaveAppearance();
    }

    void SaveAppearance()
    {
        try { _cfg.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("save config", e); }
        _appearanceChanged();
    }

    // ---- support

    public ICommand IssueCommand { get; }
    public ICommand ProjectCommand { get; }

    // ---- about

    public string Version => "Token Perch " + typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3);
    public bool CheckForUpdates
    {
        get => _cfg.CheckForUpdates;
        set { _cfg.CheckForUpdates = value; Raise(); SaveAppearance(); }
    }
    string _updateCheckStatus = "";
    public string UpdateCheckStatus { get => _updateCheckStatus; private set => Set(ref _updateCheckStatus, value); }
    public ICommand CheckUpdatesCommand { get; }
    public string ConfigPath => AppConfig.FilePath;
    public string LogPath => Log.FilePath;
    public ICommand OpenConfigCommand { get; }
    public ICommand OpenLogCommand { get; }

    static void OpenFile(string path)
    {
        if (OperatingSystem.IsWindows()) Proc.Start(["notepad.exe", path]);
        else Proc.Start(["xdg-open", path]);
    }
}
