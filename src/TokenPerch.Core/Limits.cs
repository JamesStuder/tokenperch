using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TokenPerch.Core;

public sealed record Limit(string Label, string Kind, double Percent, string? Severity, DateTimeOffset? ResetsAt)
{
    /// <summary>Length of the limit's window, for the pace projection.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? Window => Kind switch
    {
        "session" => TimeSpan.FromHours(5),
        "weekly_all" or "weekly_scoped" => TimeSpan.FromDays(7),
        _ => null,
    };
}

public sealed class LimitsSnapshot
{
    public List<Limit> Limits { get; set; } = [];
    public DateTimeOffset Fetched { get; set; }
    public string? Error { get; set; }
    public double? ExtraUsed { get; set; }
}

/// <summary>
/// Plan limits from the endpoint Claude Code's /usage command uses, called with Claude Code's own login
/// at most every 5 minutes (cached in the cache dir). The login is only read, never refreshed, so this
/// can't interfere with Claude Code; if the token has expired the last numbers are shown until Claude
/// Code renews it. The endpoint is not a documented public API and may change.
/// </summary>
public static partial class LimitsService
{
    const string Url = "https://api.anthropic.com/api/oauth/usage";
    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static string CacheFile => Path.Combine(Paths.CacheDir, "limits.json");

    public static async Task<LimitsSnapshot> GetAsync(bool force = false)
    {
        var old = ReadCache();
        if (old is not null && !force && DateTimeOffset.Now - old.Fetched < Ttl) return old;
        var token = AccessToken();
        if (token is null)
            return WithError(old, "not signed in — run claude and log in");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.UserAgent.ParseAdd("claude-code");
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return WithError(old, resp.StatusCode == HttpStatusCode.Unauthorized
                    ? "login expired (open Claude Code)" : $"HTTP {(int)resp.StatusCode}");
            var fresh = Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false), DateTimeOffset.Now);
            WriteCache(fresh);
            return fresh;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return WithError(old, $"offline ({e.GetType().Name})");
        }
    }

    internal static LimitsSnapshot Parse(string json, DateTimeOffset fetched)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var snap = new LimitsSnapshot { Fetched = fetched };
        foreach (var l in root["limits"]?.AsArray() ?? [])
        {
            if (l is null) continue;
            var kind = (string?)l["kind"] ?? "?";
            var scope = (string?)l["scope"]?["model"]?["display_name"];
            var label = scope is not null ? $"Weekly · {scope}" : kind switch
            {
                "session" => "Session (5 h)",
                "weekly_all" => "Weekly · all models",
                _ => kind,
            };
            DateTimeOffset? resets = DateTimeOffset.TryParse((string?)l["resets_at"], out var r) ? r : null;
            snap.Limits.Add(new Limit(label, kind, l["percent"]?.GetValue<double>() ?? 0, (string?)l["severity"], resets));
        }
        var extra = root["extra_usage"];
        if (extra?["is_enabled"]?.GetValue<bool>() == true)
            snap.ExtraUsed = extra["used_credits"]?.GetValue<double>();
        return snap;
    }

    static LimitsSnapshot WithError(LimitsSnapshot? old, string error)
    {
        var s = old ?? new LimitsSnapshot();
        s.Error = error;
        return s;
    }

    static LimitsSnapshot? ReadCache()
    {
        try { return JsonSerializer.Deserialize<LimitsSnapshot>(File.ReadAllText(CacheFile)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    static void WriteCache(LimitsSnapshot s)
    {
        try
        {
            Directory.CreateDirectory(Paths.CacheDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(s));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("limits cache", e); }
    }

    static JsonNode? Login()
    {
        try { return JsonNode.Parse(File.ReadAllText(Paths.Credentials))?["claudeAiOauth"]; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    static string? AccessToken() => (string?)Login()?["accessToken"];

    /// <summary>"Team · Max 5x" from the login's subscription type and rate-limit tier.</summary>
    public static string Plan()
    {
        var login = Login();
        if (login is null) return "";
        var sub = (string?)login["subscriptionType"] ?? "";
        sub = sub.Length > 0 ? char.ToUpperInvariant(sub[0]) + sub[1..].ToLowerInvariant() : "";
        var m = MaxTier().Match((string?)login["rateLimitTier"] ?? "");
        var tier = m.Success ? $"Max {m.Groups[1].Value.ToLowerInvariant()}" : "";
        if (tier.Length > 0 && sub == "Max") return tier;
        return string.Join(" · ", new[] { sub, tier }.Where(p => p.Length > 0));
    }

    [GeneratedRegex(@"max_(\d+x)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxTier();
}
