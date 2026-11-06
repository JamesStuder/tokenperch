using System.Windows.Input;
using Avalonia.Threading;
using TokenPerch.Core;

namespace TokenPerch;

/// <summary>State behind the tray icon and the panel, and the actions they trigger.
/// Data is gathered on background threads and applied on the UI thread.</summary>
public sealed class MainViewModel : ObservableObject
{
    readonly AppConfig _cfg;
    readonly UsageService _usage;
    readonly SessionWindows _windows = new();
    string _sessionsKey = "";
    int _sessionsBusy, _usageBusy;
    string _status = "";
    CancellationTokenSource? _statusClear;

    public MainViewModel(AppConfig cfg)
    {
        _cfg = cfg;
        _usage = new UsageService(cfg);
        Groups =
        [
            new(SessionGroup.Needs, "NEEDS INPUT", "?", Palette.Orange),
            new(SessionGroup.Working, "WORKING", "▶", Palette.Blue),
            new(SessionGroup.Done, "COMPLETED", "✓", Palette.Green),
            new(SessionGroup.Failed, "FAILED", "✕", Palette.Red),
        ];
        RefreshCommand = new RelayCommand(_ => { _ = RefreshUsageAsync(force: true); _ = RefreshAgentsAsync(); });
        RestartCommand = new RelayCommand(_ => Restart());
        OpenClaudeCommand = new RelayCommand(_ => OpenClaude());
        UsagePageCommand = new RelayCommand(_ => { RequestHide?.Invoke(); Terminal.OpenUrl("https://claude.ai/settings/usage"); });
        SettingsCommand = new RelayCommand(_ => OpenSettings?.Invoke());
        UpdateCommand = new RelayCommand(_ => _ = UpdateAsync());
        ReleaseNotesCommand = new RelayCommand(_ => { if (_update is { } r) { RequestHide?.Invoke(); Terminal.OpenUrl(r.PageUrl); } });
        ClearModelCommand = new RelayCommand(_ => Background(() => _usage.ClearDefaultModel(), refreshUsage: true));
        _selectTab = new RelayCommand(p => { if (p is AgentTabVM t) SelectTab(t); });
        _openAgentSession = new RelayCommand(p => { if (p is AgentSessionVM s) OpenAgentSession(s); });
        Tabs = [new AgentTabVM("Claude Code", null, _selectTab) { Selected = true }];
    }

    /// <summary>Running / percentages / needs-input count changed: redraw the tray icon.</summary>
    public event Action? TrayStateChanged;
    public event Action? RequestHide;
    public event Action? OpenSettings;

    // ---- tray state
    public bool Running { get; private set; }
    public double? SessionPercent { get; private set; }
    public double MaxPercent { get; private set; }
    /// <summary>Sessions waiting for the user, all agents together (the tray's badge).</summary>
    public int NeedsCount => _claudeNeeds + _agentNeeds.Values.Sum();
    int _claudeNeeds;
    Dictionary<string, int> _agentNeeds = new();

    public string ToolTip
    {
        get
        {
            var waiting = new List<string>();
            if (_claudeNeeds > 0) waiting.Add($"Claude Code {_claudeNeeds}");
            waiting.AddRange(_agentNeeds.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}"));
            return "Token Perch"
                + (waiting.Count > 0 ? "\nWaiting for you: " + string.Join(", ", waiting) : "")
                + "\nClaude Code " + (Running ? "running" : "not running")
                + (SessionPercent is { } s ? $" · session {s:0}%, weekly {MaxPercent:0}% max" : "")
                + (_update is { } u ? $"\nUpdate available: {u.Version.ToString(3)}" : "");
        }
    }

    // ---- header / footer
    string _plan = "";
    public string Title => SelectedAgent?.Name ?? "Claude Code";
    public string Subtitle => SelectedAgent is { } a
        ? AgentSubtitle(a)
        : (_plan.Length > 0 ? _plan + "  ·  " : "") + (Running ? "running" : "not running");
    string? _error;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool CanRestart => IsClaude && Running && _usage.Tmux.Configured;
    public string OpenText => SelectedAgent is { } a ? $"Open {a.Name}" : Running ? "Open Claude Code" : "Start Claude Code";
    public ICommand RefreshCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand OpenClaudeCommand { get; }
    public ICommand UsagePageCommand { get; }
    public ICommand ClearModelCommand { get; }
    public ICommand SettingsCommand { get; }

    // ---- update banner
    ReleaseInfo? _update;
    bool _updating;
    string _lastNotified = "";
    public bool UpdateAvailable => _update is not null;
    public string UpdateText => _update is null ? "" : $"Token Perch {_update.Version.ToString(3)} is available (you have {Updater.Current.ToString(3)})";
    public bool CanUpdate => _update is not null && !_updating;
    public ICommand UpdateCommand { get; }
    public ICommand ReleaseNotesCommand { get; }
    public string UpdateStatus { get; private set; } = "";

    /// <summary>Checks GitHub for a newer release. Returns a status line (for Settings → Check now).</summary>
    public async Task<string> CheckForUpdateAsync()
    {
        try
        {
            var r = await Task.Run(Updater.CheckAsync);
            _update = r;
            Raise(nameof(UpdateAvailable)); Raise(nameof(UpdateText)); Raise(nameof(CanUpdate));
            TrayStateChanged?.Invoke();
            if (r is null) return $"You have the latest version ({Updater.Current.ToString(3)}).";
            if (_lastNotified != r.Tag)
            {
                _lastNotified = r.Tag;
                Notifier.Show($"Token Perch {r.Version.ToString(3)} is available — open the panel to update.");
            }
            return $"Version {r.Version.ToString(3)} is available.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Error("update check", e);
            return "Couldn't reach GitHub to check for updates.";
        }
    }

    async Task UpdateAsync()
    {
        if (_update is not { } r) return;
        _updating = true;
        Raise(nameof(CanUpdate));
        void Progress(string m) => Dispatcher.UIThread.Post(() => { UpdateStatus = m; Raise(nameof(UpdateStatus)); });
        var err = await Task.Run(() => Updater.InstallAsync(r, Progress));
        UpdateStatus = err ?? "Installing — Token Perch will restart in a moment.";
        Raise(nameof(UpdateStatus));
        if (err is not null) { _updating = false; Raise(nameof(CanUpdate)); }
    }

    // ---- sessions column
    public List<SessionGroupVM> Groups { get; }
    bool _noSessions = true;
    public bool NoSessions { get => _noSessions; private set => Set(ref _noSessions, value); }
    string? _sessionsError;
    public string? SessionsError { get => _sessionsError; private set { if (Set(ref _sessionsError, value)) Raise(nameof(HasSessionsError)); } }
    public bool HasSessionsError => !string.IsNullOrEmpty(SessionsError);

    // ---- usage column
    List<LimitRow> _limits = [];
    public List<LimitRow> Limits { get => _limits; private set { Set(ref _limits, value); Raise(nameof(HasLimits)); } }
    public bool HasLimits => Limits.Count > 0;
    List<DayRow> _days = [];
    public List<DayRow> Days { get => _days; private set => Set(ref _days, value); }
    List<ModelRow> _models = [];
    public List<ModelRow> Models { get => _models; private set { Set(ref _models, value); Raise(nameof(HasModels)); } }
    public bool HasModels => Models.Count > 0;
    List<ModelChoiceVM> _choices = [];
    public List<ModelChoiceVM> ModelChoices { get => _choices; private set => Set(ref _choices, value); }
    bool _live;
    public string ModelHeader => _live ? "MODEL · RUNNING SESSION" : "MODEL · NEW SESSIONS";
    public bool ShowClearModel => !_live;

    // ---- agent tabs (OpenCode, Hermes): shown only when one of them is installed
    readonly ICommand _selectTab, _openAgentSession;
    readonly Dictionary<string, AgentReport> _reports = new();
    readonly HiddenSessions _hidden = new();
    int _agentsBusy;
    List<AgentTabVM> _tabs = [];
    public List<AgentTabVM> Tabs { get => _tabs; private set { Set(ref _tabs, value); Raise(nameof(ShowTabs)); } }
    public bool ShowTabs => Tabs.Count > 1;
    public Agent? SelectedAgent => Tabs.FirstOrDefault(t => t.Selected)?.Agent;
    public bool IsClaude => SelectedAgent is null;
    public bool IsAgent => SelectedAgent is not null;

    List<AgentModelRow> _agentModels = [];
    public List<AgentModelRow> AgentModels { get => _agentModels; private set => Set(ref _agentModels, value); }
    List<DayRow> _agentDays = [];
    public List<DayRow> AgentDays { get => _agentDays; private set => Set(ref _agentDays, value); }
    List<ModelRow> _agentTokenModels = [];
    public List<ModelRow> AgentTokenModels { get => _agentTokenModels; private set { Set(ref _agentTokenModels, value); Raise(nameof(HasAgentTokenModels)); } }
    public bool HasAgentTokenModels => AgentTokenModels.Count > 0;
    List<AgentSessionVM> _agentSessions = [];
    public List<AgentSessionVM> AgentSessions { get => _agentSessions; private set { Set(ref _agentSessions, value); Raise(nameof(NoAgentSessions)); } }
    public bool NoAgentSessions => AgentSessions.Count == 0;
    string? _agentError;
    public string? AgentError { get => _agentError; private set { if (Set(ref _agentError, value)) Raise(nameof(HasAgentError)); } }
    public bool HasAgentError => !string.IsNullOrEmpty(AgentError);
    public string AgentDaysHeader => "TOKENS BY DAY · THIS COMPUTER";

    string AgentSubtitle(Agent a)
    {
        if (!_reports.TryGetValue(a.Id, out var r)) return "";
        var model = r.Models.FirstOrDefault()?.Model ?? "";
        int today = r.Sessions.Count(s => DateTimeOffset.FromUnixTimeMilliseconds(s.Updated).ToLocalTime().Date == DateTime.Today);
        return string.Join("  ·  ", new[] { model, $"{Format.Human(r.Tokens.TodayTotal)} tokens today", $"{today} session{(today == 1 ? "" : "s")} today" }
            .Where(x => x.Length > 0));
    }

    void SelectTab(AgentTabVM tab)
    {
        foreach (var t in Tabs) t.Selected = t == tab;
        Raise(nameof(SelectedAgent)); Raise(nameof(IsClaude)); Raise(nameof(IsAgent));
        Raise(nameof(Title)); Raise(nameof(Subtitle)); Raise(nameof(OpenText)); Raise(nameof(CanRestart));
        ShowAgent();
        if (tab.Agent is not null) _ = RefreshAgentsAsync();
    }

    /// <summary>Reads every installed agent off the UI thread (a few small SQLite queries each).</summary>
    public async Task RefreshAgentsAsync()
    {
        if (Interlocked.Exchange(ref _agentsBusy, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.Now;
            var result = await Task.Run(() => Agent.All.Where(a => a.Installed).Select(a => (a, r: a.Read(now))).ToList());
            var ids = string.Join(',', result.Select(x => x.a.Id));
            if (ids != string.Join(',', Tabs.Skip(1).Select(t => t.Agent!.Id)))
            {
                var keep = SelectedAgent?.Id;
                var tabs = new List<AgentTabVM> { new("Claude Code", null, _selectTab) };
                tabs.AddRange(result.Select(x => new AgentTabVM(x.a.Name, x.a, _selectTab)));
                (tabs.FirstOrDefault(t => t.Agent?.Id == keep) ?? tabs[0]).Selected = true;
                Tabs = tabs;
                Raise(nameof(SelectedAgent)); Raise(nameof(IsClaude)); Raise(nameof(IsAgent));
                Raise(nameof(Title)); Raise(nameof(OpenText)); Raise(nameof(CanRestart));
            }
            _reports.Clear();
            foreach (var (a, r) in result) _reports[a.Id] = r;
            // Waiting sessions of every agent count on the tray icon (not the ones removed from the list).
            var needs = result.ToDictionary(x => x.a.Name,
                x => x.r.Sessions.Count(s => s.Group == SessionGroup.Needs && !_hidden.IsHidden(x.a.Id, s.Id)));
            if (!needs.OrderBy(kv => kv.Key).SequenceEqual(_agentNeeds.OrderBy(kv => kv.Key)))
            {
                _agentNeeds = needs;
                TrayStateChanged?.Invoke();
            }
            ShowAgent();
        }
        catch (Exception e)
        {
            Log.Error("agents refresh", e);
        }
        finally { Interlocked.Exchange(ref _agentsBusy, 0); }
    }

    void ShowAgent()
    {
        Raise(nameof(Subtitle));
        if (SelectedAgent is not { } a || !_reports.TryGetValue(a.Id, out var r))
        {
            AgentModels = []; AgentDays = []; AgentTokenModels = []; AgentSessions = []; AgentError = null;
            return;
        }
        AgentError = r.Error;
        AgentModels = r.Models.Select(m => new AgentModelRow(m.Role, m.Model, m.Where)).ToList();
        double dayPeak = Math.Max(1, r.Tokens.Days.Select(d => d.Total).DefaultIfEmpty(0).Max());
        AgentDays = r.Tokens.Days.Select(d => new DayRow(d.Today ? "Today" : d.Name, Format.Human(d.Total), d.Total / dayPeak * 100, d.Today)).ToList();
        double modelPeak = Math.Max(1, r.Tokens.Models.Select(m => m.Total).DefaultIfEmpty(0).Max());
        AgentTokenModels = r.Tokens.Models.Select(m => new ModelRow(m.Name, Format.Human(m.Total), m.Total / modelPeak * 100,
            $"in {Format.Human(m.Input)} · out {Format.Human(m.Output)} · cache {Format.Human(m.Cache)}")).ToList();
        var visible = r.Sessions.Where(s => !_hidden.IsHidden(a.Id, s.Id)).OrderBy(s => s.Group == SessionGroup.Needs ? 0 : 1).ToList();
        var key = a.Id + "\n" + string.Join('\n', visible.Select(s => $"{s.Id}|{s.Group}|{s.Title}|{s.Updated}|{s.Tokens}"));
        if (key != _agentSessionsKey)
        {
            _agentSessionsKey = key;
            AgentSessions = visible.Select(s => new AgentSessionVM(s, s.Group switch
            {
                SessionGroup.Needs => "?",
                SessionGroup.Working => "▶",
                SessionGroup.Failed => "✕",
                _ => "✓",
            }, s.Group switch
            {
                SessionGroup.Needs => Palette.Orange,
                SessionGroup.Working => Palette.Blue,
                SessionGroup.Failed => Palette.Red,
                _ => Palette.Green,
            }, _openAgentSession, RemoveAgentSession)).ToList();
        }
    }

    /// <summary>✕ → Remove: hides the session from the panel; nothing in the agent changes.</summary>
    void RemoveAgentSession(AgentSessionVM s)
    {
        if (SelectedAgent is not { } a) return;
        try
        {
            var listed = _reports.TryGetValue(a.Id, out var r) ? r.Sessions.Select(x => x.Id) : [];
            _hidden.Hide(a.Id, s.Info.Id, listed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("hide agent session", e);
            ShowStatus("Couldn't remove: " + e.Message);
            return;
        }
        ShowAgent();
        _ = RefreshAgentsAsync();   // a hidden waiting session no longer counts on the tray icon
    }
    string _agentSessionsKey = "";

    void OpenAgentSession(AgentSessionVM s)
    {
        if (SelectedAgent is not { } a) return;
        RequestHide?.Invoke();
        Background(() => Terminal.OpenAgent(a, s.Info) ?? "");
    }

    // ---- polling (called by the tray controller's timers)

    public async Task PollRunningAsync()
    {
        bool running = await Task.Run(() => _usage.Tmux.Configured ? _usage.Tmux.HasSession() : ClaudeCli.AnyRunning());
        if (running == Running) return;
        Running = running;
        Raise(nameof(Subtitle)); Raise(nameof(CanRestart)); Raise(nameof(OpenText));
        TrayStateChanged?.Invoke();
    }

    public async Task RefreshUsageAsync(bool force = false)
    {
        if (Interlocked.Exchange(ref _usageBusy, 1) == 1) return;
        try
        {
            var snap = await Task.Run(() => _usage.GetAsync(force));
            ApplyUsage(snap);
            if (force) ShowStatus("Refreshed");
        }
        catch (Exception e)
        {
            Log.Error("usage refresh", e);
        }
        finally { Interlocked.Exchange(ref _usageBusy, 0); }
    }

    public async Task RefreshSessionsAsync()
    {
        if (Interlocked.Exchange(ref _sessionsBusy, 1) == 1) return;
        try
        {
            var result = await Task.Run(() => SessionsService.Get(_cfg, _windows));
            ApplySessions(result);
        }
        catch (Exception e)
        {
            Log.Error("sessions refresh", e);
        }
        finally { Interlocked.Exchange(ref _sessionsBusy, 0); }
    }

    /// <summary>Every 30 s while the panel is open: the "ago" labels.</summary>
    public void Tick()
    {
        foreach (var g in Groups) foreach (var s in g.Items) s.Tick();
        foreach (var s in AgentSessions) s.Tick();
    }

    public void PanelClosed()
    {
        foreach (var g in Groups) g.ResetShowAll();
    }

    void ApplyUsage(UsageSnapshot u)
    {
        var now = u.Taken;
        _plan = u.Plan;
        Raise(nameof(Subtitle));
        Error = u.Limits.Error;
        Limits = u.Limits.Limits.Select(l =>
        {
            var pace = Format.Pace(l, now);
            return new LimitRow(l.Label, $"{l.Percent:0}%", l.Percent, Palette.Level(l.Percent), l.Percent >= 90,
                l.ResetsAt is { } r ? "resets in " + Format.Until(r, now) : "",
                pace, pace.EndsWith("before reset", StringComparison.Ordinal));
        }).ToList();
        double dayPeak = Math.Max(1, u.Tokens.Days.Select(d => d.Total).DefaultIfEmpty(0).Max());
        Days = u.Tokens.Days.Select(d => new DayRow(d.Today ? "Today" : d.Name, Format.Human(d.Total), d.Total / dayPeak * 100, d.Today)).ToList();
        double modelPeak = Math.Max(1, u.Tokens.Models.Select(m => m.Total).DefaultIfEmpty(0).Max());
        Models = u.Tokens.Models.Select(m => new ModelRow(m.Name, Format.Human(m.Total), m.Total / modelPeak * 100,
            $"in {Format.Human(m.Input)} · out {Format.Human(m.Output)} · cache {Format.Human(m.Cache)}")).ToList();
        _live = u.LiveModelSwitch;
        var pick = new RelayCommand(p => { if (p is ModelChoice m) Background(() => _usage.SetModel(m), refreshUsage: true); });
        ModelChoices = _cfg.Models.Select(m => new ModelChoiceVM(m, m.Id == u.CurrentModel, pick)).ToList();
        Raise(nameof(ModelHeader)); Raise(nameof(ShowClearModel));

        var session = u.Limits.Limits.FirstOrDefault(l => l.Kind == "session");
        double max = u.Limits.Limits.Select(l => l.Percent).DefaultIfEmpty(0).Max();
        if (session?.Percent != SessionPercent || max != MaxPercent)
        {
            SessionPercent = session?.Percent;
            MaxPercent = max;
            TrayStateChanged?.Invoke();
        }
    }

    void ApplySessions(SessionsResult r)
    {
        SessionsError = r.Error;
        var key = string.Join('\n', r.Sessions.Select(s => $"{s.Id}|{s.Group}|{s.Name}|{s.Text}|{s.Updated}|{s.Open}"));
        if (key == _sessionsKey) return;
        _sessionsKey = key;
        foreach (var g in Groups)
            g.SetAll(r.Sessions.Where(s => s.Group == g.Group).Select(s => new SessionVM(s, this, g.Glyph, g.Brush)).ToList());
        NoSessions = r.Sessions.Count == 0;
        int needs = r.Sessions.Count(s => s.Group == SessionGroup.Needs);
        if (needs != _claudeNeeds)
        {
            _claudeNeeds = needs;
            TrayStateChanged?.Invoke();
        }
    }

    // ---- actions

    public void OpenClaude()
    {
        RequestHide?.Invoke();
        if (SelectedAgent is { } a) { Background(() => Terminal.OpenAgent(a) ?? ""); return; }
        Background(() => Terminal.OpenClaude(_cfg) ?? "");
    }

    public void OpenSession(SessionVM s)
    {
        RequestHide?.Invoke();
        Background(() => Terminal.OpenSession(_cfg, _windows, s.Info.Id, s.Info.Name) ?? "");
        _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.UIThread.Post(() => _ = RefreshSessionsAsync()));
    }

    public void RemoveSession(SessionVM s)
    {
        foreach (var g in Groups)
            if (g.Group == s.Info.Group)
                g.SetAll(g.Items.Where(x => x != s).ToList());
        _sessionsKey = "";
        Background(() => SessionsService.Remove(_cfg, _windows, s.Info.Id) is { } err ? "Couldn't remove: " + err : "", refreshSessions: true);
    }

    void Restart()
    {
        RequestHide?.Invoke();
        Background(() => _usage.Tmux.Restart());
    }

    /// <summary>Runs an action off the UI thread and shows the message it returns (and notifies on Linux).</summary>
    void Background(Func<string> action, bool refreshUsage = false, bool refreshSessions = false)
    {
        Task.Run(action).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            var msg = t.IsFaulted ? "Error: " + t.Exception?.InnerException?.Message : t.Result;
            if (msg.Length > 0) ShowStatus(msg);
            if (refreshUsage) _ = RefreshUsageAsync(force: false);
            if (refreshSessions) _ = RefreshSessionsAsync();
        }));
    }

    void ShowStatus(string msg)
    {
        Log.Info("status: " + msg);
        Status = msg;
        Notifier.Show(msg);
        _statusClear?.Cancel();
        _statusClear = new CancellationTokenSource();
        var token = _statusClear.Token;
        _ = Task.Delay(8000, token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => Status = ""); });
    }
}

/// <summary>Desktop notification for action results while the panel is closed (Linux: notify-send).</summary>
static class Notifier
{
    public static Func<bool> PanelOpen { get; set; } = () => false;

    public static void Show(string message)
    {
        if (PanelOpen() || OperatingSystem.IsWindows() || message == "Refreshed") return;
        if (Proc.Which("notify-send") is { } ns)
            Proc.Start([ns, "-a", "Token Perch", "-i", "tokenperch", "Claude Code", message]);
    }
}
