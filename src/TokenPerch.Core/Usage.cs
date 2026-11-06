namespace TokenPerch.Core;

public sealed record UsageSnapshot(
    string Plan,
    LimitsSnapshot Limits,
    TokenReport Tokens,
    string CurrentModel,
    bool LiveModelSwitch,
    DateTimeOffset Taken);

/// <summary>Everything the usage column shows, gathered in one go (limits are cached for 5 minutes).</summary>
public sealed class UsageService(AppConfig cfg)
{
    readonly TokenStats _tokens = new();
    readonly object _gate = new();

    public Tmux Tmux { get; } = new(cfg);

    public async Task<UsageSnapshot> GetAsync(bool force = false)
    {
        var limits = await LimitsService.GetAsync(force).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        TokenReport tokens;
        string current;
        bool live = Tmux.Configured;
        lock (_gate)   // TokenStats keeps a per-file cache; one scan at a time
        {
            tokens = _tokens.Compute(now);
            current = live ? _tokens.CurrentModel() : ClaudeSettings.DefaultModel() ?? "";
        }
        return new UsageSnapshot(LimitsService.Plan(), limits, tokens, current, live, now);
    }

    /// <summary>Model button: switches the tmux session live, else sets the default for new sessions.
    /// Returns a status line for the panel.</summary>
    public string SetModel(ModelChoice m)
    {
        if (Tmux.Configured)
            return Tmux.SendModel(m.Id) ?? $"Model: {m.Name} — sent /model to the running session";
        return ClaudeSettings.SetDefaultModel(m.Id) ?? $"New sessions start with {m.Name}";
    }

    public string ClearDefaultModel() =>
        ClaudeSettings.SetDefaultModel(null) ?? "New sessions use your account's default model";
}
