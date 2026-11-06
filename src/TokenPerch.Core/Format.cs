using System.Globalization;
using System.Text.RegularExpressions;

namespace TokenPerch.Core;

/// <summary>Text shown in the panel: token counts, reset times, pace, model names.</summary>
public static partial class Format
{
    public static string Human(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.0}B",
        >= 1_000_000 => $"{n / 1e6:0.0}M",
        >= 1_000 => $"{n / 1e3:0.0}k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"3h 05m" / "42m" until <paramref name="at"/>.</summary>
    public static string Until(DateTimeOffset at, DateTimeOffset now)
    {
        int mins = Math.Max(0, (int)((Round(at) - now).TotalMinutes));
        return mins >= 60 ? $"{mins / 60}h {mins % 60:00}m" : $"{mins}m";
    }

    /// <summary>"resets 6:00 PM (in 3h 55m)", with the weekday when it isn't today.</summary>
    public static string ResetText(DateTimeOffset at, DateTimeOffset now) =>
        $"resets {Clock(Round(at), now)} (in {Until(at, now)})";

    /// <summary>Local time, prefixed with the weekday when it isn't today.</summary>
    public static string Clock(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var day = local.Date == now.ToLocalTime().Date ? "" : local.ToString("ddd ", CultureInfo.CurrentCulture);
        return day + local.ToString("t", CultureInfo.CurrentCulture);
    }

    static DateTimeOffset Round(DateTimeOffset t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute + (t.Second >= 30 ? TimeSpan.TicksPerMinute : 0), t.Offset);

    /// <summary>"now" / "5m" / "3h" / "2d" since a Unix-ms time.</summary>
    public static string Ago(long unixMs, DateTimeOffset now)
    {
        double s = Math.Max(0, (now.ToUnixTimeMilliseconds() - unixMs) / 1000.0);
        return s < 60 ? "now" : s < 3600 ? $"{(int)(s / 60)}m" : s < 86400 ? $"{(int)(s / 3600)}h" : $"{(int)(s / 86400)}d";
    }

    /// <summary>One line, at most <paramref name="max"/> characters.</summary>
    public static string OneLine(string? s, int max = 160)
    {
        s = Whitespace().Replace(s ?? "", " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5", "claude-haiku-4-5-20251001" → "Haiku 4.5".</summary>
    public static string ModelName(string? id)
    {
        var m = string.IsNullOrEmpty(id) ? "?" : id;
        if (m.StartsWith("claude-", StringComparison.Ordinal)) m = m["claude-".Length..];
        var parts = m.Split('-');
        if (parts.Length >= 2 && parts[0].All(char.IsLetter))
        {
            var ver = string.Join('.', parts.Skip(1).Take(2).Where(p => p.All(char.IsDigit) && p.Length > 0));
            var name = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
            return (name + " " + ver).Trim();
        }
        return m;
    }

    /// <summary>Straight-line projection from how far into its window a limit is.</summary>
    public static string Pace(Limit limit, DateTimeOffset now)
    {
        if (limit.ResetsAt is not { } reset || limit.Window is not { } window) return "";
        double left = Math.Max(0, (reset - now).TotalSeconds);
        double elapsed = window.TotalSeconds - left;
        double pct = limit.Percent;
        if (elapsed < window.TotalSeconds * 0.05 || pct <= 0)
            return pct > 0 ? "too early to tell" : "nothing used yet";
        double rate = pct / elapsed;   // percent per second
        double atReset = pct + rate * left;
        if (atReset < 100) return $"on pace for {atReset:0}% by reset";
        var hit = now.AddSeconds((100 - pct) / rate);
        return $"at this pace: 100% {Clock(hit, now)}, before reset";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
