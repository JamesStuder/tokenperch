using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenPerch.Core;

public sealed record AuthStatus(bool LoggedIn, string Email, string Organization, string Subscription, string Method, string? Error = null);

/// <summary>Claude Code's login, through `claude auth status | login | logout`.</summary>
public static class ClaudeAuth
{
    public static AuthStatus Status(AppConfig cfg)
    {
        var r = ClaudeCli.Run(cfg, "auth", "status", "--json");
        try
        {
            var j = JsonNode.Parse(r.StdOut);
            if (j is null) return new AuthStatus(false, "", "", "", "", Format.OneLine(r.StdErr, 160));
            return new AuthStatus(j["loggedIn"]?.GetValue<bool>() == true, (string?)j["email"] ?? "", (string?)j["orgName"] ?? "",
                (string?)j["subscriptionType"] ?? "", (string?)j["authMethod"] ?? "");
        }
        catch (JsonException)
        {
            var why = Format.OneLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut, 160);
            return new AuthStatus(false, "", "", "", "", why.Length > 0 ? why : "claude auth status failed");
        }
    }

    /// <summary>Opens a terminal running `claude auth login` (it opens the browser; the terminal is for the
    /// code to paste when the browser can't hand it back). Returns an error message or null.</summary>
    public static string? Login(AppConfig cfg)
    {
        var cmd = Terminal.Wrap([ClaudeCli.Exe(cfg), "auth", "login"], "Claude Code login");
        if (cmd is null) return "No terminal found to run claude auth login";
        return Proc.Start(cmd) is null ? "Couldn't start a terminal" : null;
    }

    public static string? Logout(AppConfig cfg)
    {
        var r = ClaudeCli.Run(cfg, "auth", "logout");
        return r.Ok ? null : Format.OneLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut, 160);
    }
}
