using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenPerch.Core;

/// <summary>
/// The "model" key in Claude Code's user settings.json: the model every new session starts with.
/// Used for the model buttons when there's no tmux session to switch live (always on Windows).
/// </summary>
public static class ClaudeSettings
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string? DefaultModel()
    {
        try { return (string?)JsonNode.Parse(File.ReadAllText(Paths.ClaudeSettings))?["model"]; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    /// <summary>Sets (or with null removes) "model", keeping every other key. The first time, the
    /// original file is copied to settings.json.tokenperch.bak. Returns an error message or null.</summary>
    public static string? SetDefaultModel(string? modelId)
    {
        var path = Paths.ClaudeSettings;
        try
        {
            JsonObject root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                  ?? throw new JsonException("settings.json is not an object")
                : new JsonObject();
            if (File.Exists(path) && !File.Exists(path + ".tokenperch.bak"))
                File.Copy(path, path + ".tokenperch.bak");
            if (modelId is null) root.Remove("model");
            else root["model"] = modelId;
            Directory.CreateDirectory(Paths.ClaudeDir);
            var tmp = path + ".tokenperch.tmp";
            File.WriteAllText(tmp, root.ToJsonString(Indented) + Environment.NewLine);
            File.Move(tmp, path, overwrite: true);
            Log.Info($"settings.json model = {modelId ?? "(removed)"}");
            return null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("settings.json", e);
            return "Couldn't update settings.json: " + e.Message;
        }
    }
}
