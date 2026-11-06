using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenPerch.Core;

/// <summary>Custom panel colours ("#RRGGBB"); null/empty = the theme's own.</summary>
public sealed class PanelColors
{
    public string? Background { get; set; }
    public string? Text { get; set; }
    public string? Button { get; set; }
    public string? ButtonText { get; set; }
}

public sealed class ModelChoice
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
}

/// <summary>
/// config.json in the config dir (~/.config/tokenperch on Linux, %APPDATA%\TokenPerch on Windows).
/// Written with the defaults on first start; every key is optional.
/// </summary>
public sealed class AppConfig
{
    /// <summary>What "Open Claude Code" runs. Empty = a terminal running Claude Code in your home folder.</summary>
    public string OpenCommand { get; set; } = "";

    /// <summary>Terminal a background session opens in. {claude} = the Claude Code executable,
    /// {id} / {name} = the session's. Empty = picked for your terminal.</summary>
    public string SessionCommand { get; set; } = "";

    /// <summary>Linux: tmux socket and pane of an interactive Claude Code, e.g. "-L claude -t claude".
    /// Makes the model buttons switch that running session and enables Restart.</summary>
    public string TmuxTarget { get; set; } = "";

    /// <summary>The Claude Code executable. Empty = ~/.local/bin/claude (the native installer's), else PATH.</summary>
    public string ClaudeCommand { get; set; } = "";

    /// <summary>Look for new releases on GitHub (at start, then every 6 hours).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    public PanelColors Colors { get; set; } = new();

    /// <summary>The model buttons.</summary>
    public List<ModelChoice> Models { get; set; } = DefaultModels();

    public static List<ModelChoice> DefaultModels() =>
    [
        new() { Name = "Fable 5.1", Id = "claude-fable-5-1" },
        new() { Name = "Opus 5.5", Id = "claude-opus-5-5" },
        new() { Name = "Sonnet 5.5", Id = "claude-sonnet-5-5" },
        new() { Name = "Haiku 5.5", Id = "claude-haiku-5-5" },
    ];

    public static string FilePath => Path.Combine(Paths.ConfigDir, "config.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public void Save()
    {
        Directory.CreateDirectory(Paths.ConfigDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static AppConfig Load()
    {
        MigrateLegacy(Paths.LegacyConfigDir, Paths.ConfigDir);
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new AppConfig();
            var fresh = new AppConfig();
            Directory.CreateDirectory(Paths.ConfigDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(fresh, Json));
            return fresh;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error($"config {FilePath}", e);
            return new AppConfig();
        }
    }

    /// <summary>First start after the rename from Claude Tray: copy its settings files (config.json,
    /// hidden-sessions.json, …) when this app has no settings folder yet. The old folder is left as it is.</summary>
    internal static void MigrateLegacy(string legacyDir, string configDir)
    {
        try
        {
            if (Directory.Exists(configDir) || !Directory.Exists(legacyDir)) return;
            Directory.CreateDirectory(configDir);
            foreach (var f in Directory.EnumerateFiles(legacyDir, "*.json"))
                File.Copy(f, Path.Combine(configDir, Path.GetFileName(f)));
            Log.Info($"settings copied from {legacyDir}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings migration", e);
        }
    }
}
