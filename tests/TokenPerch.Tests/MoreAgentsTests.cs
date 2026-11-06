using System.Text.Json;
using TokenPerch.Core;
using Microsoft.Data.Sqlite;

namespace TokenPerch.Tests;

public class MoreAgentsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("tokenperch-agents2-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static void Exec(string db, params string[] sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        c.Open();
        foreach (var s in sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = s;
            cmd.ExecuteNonQuery();
        }
    }

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    // ---- Codex: numbers from a real Codex CLI 0.166.0 session (two turns, threads.tokens_used = 17508)

    [Fact]
    public void Codex_TokensFromRollout_MatchThreadTotal()
    {
        var home = Path.Combine(_dir, "codex");
        var day = Path.Combine(home, "sessions", "2026", "11", "05");
        Directory.CreateDirectory(day);
        var now = DateTimeOffset.Now;
        var t = now.AddMinutes(-5);
        var rollout = Path.Combine(day, "rollout-x-abc.jsonl");
        File.WriteAllLines(rollout,
        [
            $$$$$"""{"timestamp":"{{{{{Iso(t)}}}}}","type":"session_meta","payload":{"id":"abc","cwd":"/tmp"}}""",
            $$$$$"""{"timestamp":"{{{{{Iso(t)}}}}}","type":"turn_context","payload":{"turn_id":"1","model":"muse-glimmer-30b"}}""",
            $$$$$"""{"timestamp":"{{{{{Iso(t.AddSeconds(4))}}}}}","type":"token_usage_record","payload":{"usage":{"input_tokens":8356,"cached_input_tokens":8337,"cache_write_input_tokens":0,"output_tokens":289,"reasoning_output_tokens":0,"total_tokens":8645}}}""",
            $$$$$"""{"timestamp":"{{{{{Iso(t.AddSeconds(4))}}}}}","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":8645},"last_token_usage":{"total_tokens":8645}}}}""",
            $$$$$"""{"timestamp":"{{{{{Iso(t.AddSeconds(8))}}}}}","type":"turn_context","payload":{"turn_id":"2","model":"muse-glimmer-30b"}}""",
            $$$$$"""{"timestamp":"{{{{{Iso(t.AddSeconds(11))}}}}}","type":"token_usage_record","payload":{"usage":{"input_tokens":8661,"cached_input_tokens":8644,"cache_write_input_tokens":0,"output_tokens":202,"reasoning_output_tokens":0,"total_tokens":8863}}}""",
        ]);
        File.WriteAllText(Path.Combine(home, "config.toml"), """
            model = "muse-glimmer-30b"
            model_provider = "tunnel"

            [model_providers.tunnel]
            name = "Local llama-server via tunnel"
            base_url = "http://127.0.0.1:11234/v1"
            wire_api = "responses"
            """);
        long ms = t.AddSeconds(11).ToUnixTimeMilliseconds();
        Exec(Path.Combine(home, "state_5.sqlite"),
            "CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT, cwd TEXT, model TEXT, tokens_used INTEGER, title TEXT, " +
            "first_user_message TEXT, name TEXT, updated_at_ms INTEGER, archived INTEGER)",
            $"INSERT INTO threads VALUES ('abc', '{rollout}', '/tmp', 'muse-glimmer-30b', 17508, 'Say hello', 'Say hello', NULL, {ms}, 0)",
            $"INSERT INTO threads VALUES ('arch', '{rollout}x', '/tmp', 'muse-glimmer-30b', 5, 'archived one', '', NULL, {ms}, 1)");
        Exec(Path.Combine(home, "thread_history_1.sqlite"),
            "CREATE TABLE thread_turns (thread_id TEXT, turn_id TEXT, status TEXT, started_at INTEGER)",
            $"INSERT INTO thread_turns VALUES ('abc','1','completed',{ms - 9000})",
            $"INSERT INTO thread_turns VALUES ('abc','2','inProgress',{ms - 1000})");

        var a = new CodexAgent(home);
        Assert.True(a.Installed);
        var r = a.Read(now);
        Assert.Null(r.Error);
        Assert.Equal(17508, r.Tokens.TodayTotal);      // = threads.tokens_used: nothing counted twice
        Assert.Equal(491, r.Tokens.TodayOutput);
        var m = Assert.Single(r.Tokens.Models);
        Assert.Equal(("muse-glimmer-30b", 8356L - 8337 + 8661 - 8644, 8337L + 8644), (m.Name, m.Input, m.Cache));
        var s = Assert.Single(r.Sessions);              // the archived one is left out
        Assert.Equal(("abc", "Say hello", 17508L, SessionGroup.Working), (s.Id, s.Title, s.Tokens, s.Group));
        Assert.Equal([new AgentModel("Default", "muse-glimmer-30b", "Local llama-server via tunnel · 127.0.0.1:11234")], r.Models);
        Assert.Equal(["resume", "abc"], a.ResumeArgs(s));
    }

    [Fact]
    public void Codex_NotInstalled_WithOnlyAnEmptySessionsFolder()
    {
        var home = Path.Combine(_dir, "codex-empty");
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        Assert.False(new CodexAgent(home).Installed);
    }

    // ---- Goose: schema from the v1.57.0 source

    [Fact]
    public void Goose_LedgerTokens_Sessions_AndConfig()
    {
        var root = Path.Combine(_dir, "goose");
        Directory.CreateDirectory(Path.Combine(root, "data", "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "config.yaml"), """
            active_provider: openai
            providers:
              openai:
                model: muse-glimmer-30b   # server
            OPENAI_HOST: http://127.0.0.1:11234
            extensions:
              developer:
                enabled: true
            """);
        var now = DateTimeOffset.Now;
        long t = now.ToUnixTimeSeconds() - 120;
        Exec(Path.Combine(root, "data", "sessions", "sessions.db"),
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, name TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT '', user_set_name BOOLEAN DEFAULT FALSE, session_type TEXT NOT NULL DEFAULT 'user', working_dir TEXT NOT NULL, created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP, updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP, extension_data TEXT DEFAULT '{}', total_tokens INTEGER, input_tokens INTEGER, output_tokens INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER, accumulated_total_tokens INTEGER, accumulated_input_tokens INTEGER, accumulated_output_tokens INTEGER, accumulated_cache_read_tokens INTEGER, accumulated_cache_write_tokens INTEGER, accumulated_cost REAL, schedule_id TEXT, recipe_json TEXT, user_recipe_values_json TEXT, provider_name TEXT, model_config_json TEXT, goose_mode TEXT NOT NULL DEFAULT 'auto', archived_at TIMESTAMP, project_id TEXT, parent_session_id TEXT, system_prompt_override TEXT, system_prompt_extras_json TEXT, container_id TEXT)",
            "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT, session_id TEXT NOT NULL REFERENCES sessions(id), role TEXT NOT NULL, content_json TEXT NOT NULL, created_timestamp INTEGER NOT NULL, timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP, tokens INTEGER, metadata_json TEXT)",
            "CREATE TABLE usage_ledger (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE, created_timestamp INTEGER NOT NULL, model TEXT, input_tokens INTEGER, output_tokens INTEGER, total_tokens INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER, cost REAL, cost_source TEXT, is_compaction INTEGER DEFAULT 0)",
            "INSERT INTO sessions (id, name, working_dir, accumulated_total_tokens, provider_name, model_config_json) VALUES ('20261105_1', 'Fix tests', '/tmp', 1400, 'openai', '{\"model_name\":\"muse-glimmer-30b\"}')",
            "INSERT INTO sessions (id, name, working_dir, session_type, parent_session_id) VALUES ('20261105_2', 'helper', '/tmp', 'sub_agent', '20261105_1')",
            $"INSERT INTO messages (session_id, role, content_json, created_timestamp) VALUES ('20261105_1', 'user', '[]', {t})",
            // ms timestamp (Goose's rule: > 1e10 means milliseconds); cache inside input (total = in + out)
            $"INSERT INTO usage_ledger (session_id, created_timestamp, model, input_tokens, output_tokens, total_tokens, cache_read_tokens, cache_write_tokens) VALUES ('20261105_1', {t * 1000}, 'muse-glimmer-30b', 1000, 100, 1100, 600, 0)",
            $"INSERT INTO usage_ledger (session_id, created_timestamp, model, input_tokens, output_tokens, total_tokens, cache_read_tokens, cache_write_tokens) VALUES ('20261105_2', {t}, 'gpt-oss-agent', 250, 50, 300, 0, 0)",
            $"INSERT INTO usage_ledger (session_id, created_timestamp, model, input_tokens, output_tokens, total_tokens, cost_source) VALUES ('20261105_1', {t}, NULL, 9999, 9999, 19998, 'carried_forward')");

        var a = new GooseAgent(root);
        Assert.True(a.Installed);
        var r = a.Read(now);
        Assert.Null(r.Error);
        Assert.Equal(1400, r.Tokens.TodayTotal);          // 400 + 100 + 600 cache, + 300 subagent; back-fill left out
        Assert.Equal(["muse-glimmer-30b", "gpt-oss-agent"], r.Tokens.Models.Select(m => m.Name));
        var s = Assert.Single(r.Sessions);
        Assert.Equal(("20261105_1", "Fix tests", "muse-glimmer-30b", 1400L), (s.Id, s.Title, s.Model, s.Tokens));
        Assert.Equal(t * 1000, s.Updated);
        Assert.Equal([new AgentModel("Default", "muse-glimmer-30b", "openai · 127.0.0.1:11234")], r.Models);
        Assert.Equal(["session", "--resume", "--session-id", "20261105_1"], a.ResumeArgs(s));
    }

    [Fact]
    public void Goose_ModelFromEnvironmentWins() =>
        Assert.Equal("env-model", GooseAgent.ParseConfig(
            new() { ["active_provider"] = "ollama", ["providers.ollama.model"] = "file-model" },
            k => k == "GOOSE_MODEL" ? "env-model" : null).Single().Model);

    // ---- Pi: format from the v1.5.0 source (docs/session-format.md)

    [Fact]
    public void Pi_UsagePerReply_ForkCountedOnce_TitleAndModels()
    {
        var agentDir = Path.Combine(_dir, "pi");
        var proj = Path.Combine(agentDir, "sessions", "--tmp-proj--");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(agentDir, "settings.json"), """{ "defaultProvider": "tunnel", "defaultModel": "muse-glimmer-30b" }""");
        File.WriteAllText(Path.Combine(agentDir, "models.json"), """
            {"providers":{"tunnel":{"baseUrl":"http://127.0.0.1:11234/v1","api":"openai-completions","models":[{"id":"muse-glimmer-30b"},{"id":"other"}]}}}
            """);
        var now = DateTimeOffset.Now;
        long t = now.ToUnixTimeMilliseconds() - 60_000;
        string Line(object o) => JsonSerializer.Serialize(o);
        var reply = Line(new { type = "message", id = "a1b2c3d4", parentId = "u1", timestamp = Iso(now.AddMinutes(-1)),
            message = new { role = "assistant", provider = "tunnel", model = "muse-glimmer-30b", stopReason = "stop", timestamp = t,
                usage = new { input = 300, output = 40, cacheRead = 1000, cacheWrite = 0, reasoning = 10, totalTokens = 1340 } } });
        var user = Line(new { type = "message", id = "u1", timestamp = Iso(now.AddMinutes(-2)),
            message = new { role = "user", content = new object[] { new { type = "text", text = "Fix the" }, new { type = "text", text = "build" } }, timestamp = t - 5000 } });
        File.WriteAllLines(Path.Combine(proj, "2026-11-05T10-00-00-000Z_s1.jsonl"),
        [
            Line(new { type = "session", version = 3, id = "s1", timestamp = Iso(now.AddMinutes(-3)), cwd = "/tmp/proj" }),
            user, reply,
        ]);
        // A fork copies the entries verbatim (same ids, timestamps and usage), then goes on.
        File.WriteAllLines(Path.Combine(proj, "2026-11-05T10-05-00-000Z_s2.jsonl"),
        [
            Line(new { type = "session", version = 3, id = "s2", timestamp = Iso(now.AddMinutes(-1)), cwd = "/tmp/proj", parentSession = "s1" }),
            user, reply,
            Line(new { type = "session_info", id = "n1", timestamp = Iso(now), name = "Build fix (fork)" }),
        ]);

        var a = new PiAgent(agentDir);
        Assert.True(a.Installed);
        var r = a.Read(now);
        Assert.Null(r.Error);
        Assert.Equal(1340, r.Tokens.TodayTotal);          // the fork's copy is not counted again
        Assert.Equal(2, r.Sessions.Count);
        Assert.Contains(r.Sessions, s => s.Title == "Fix the build" && s.Tokens == 1340 && s.Model == "muse-glimmer-30b");
        Assert.Contains(r.Sessions, s => s.Title == "Build fix (fork)");
        Assert.Equal(
        [
            new AgentModel("Default", "muse-glimmer-30b", "tunnel · 127.0.0.1:11234"),
            new AgentModel("Also set up", "other", "tunnel · 127.0.0.1:11234"),
        ], r.Models);
        var s1 = r.Sessions.Single(s => s.Title == "Fix the build");
        Assert.Equal(["--session", s1.Id], a.ResumeArgs(s1));
        Assert.EndsWith("_s1.jsonl", s1.Id);
    }

    // ---- Qwen Code: record shapes from a real 0.26.8 session

    [Fact]
    public void Qwen_TelemetryTokens_NotUsageMetadataAgain_TitleAndModel()
    {
        var home = Path.Combine(_dir, "qwen");
        var chats = Path.Combine(home, "projects", "-tmp-demo", "chats");
        Directory.CreateDirectory(chats);
        File.WriteAllText(Path.Combine(home, "settings.json"), """
            { "security": { "auth": { "selectedType": "openai" } }, "model": { "name": "muse-glimmer-30b" },
              "modelProviders": { "openai": [ { "id": "muse-glimmer-30b", "baseUrl": "http://127.0.0.1:11234/v1" } ] }, "$version": 4 }
            """);
        var now = DateTimeOffset.Now;
        string Line(object o) => JsonSerializer.Serialize(o);
        object Api(string rid, int input, int cached, int output, string? sub = null) => new
        {
            sessionId = "s-1", type = "system", subtype = "ui_telemetry", cwd = "/tmp/demo", timestamp = Iso(now),
            systemPayload = new { uiEvent = new Dictionary<string, object?>
            {
                ["event.name"] = "qwen-code.api_response", ["event.timestamp"] = Iso(now.AddMinutes(-1)), ["model"] = "muse-glimmer-30b",
                ["input_token_count"] = input, ["cached_content_token_count"] = cached, ["output_token_count"] = output,
                ["total_token_count"] = input + output, ["response_id"] = rid, ["subagent_id"] = sub,
            } },
        };
        File.WriteAllLines(Path.Combine(chats, "s-1.jsonl"),
        [
            Line(new { sessionId = "s-1", type = "user", cwd = "/tmp/demo", timestamp = Iso(now), message = new { role = "user", parts = new[] { new { text = "say hi" } } } }),
            Line(new { sessionId = "s-1", type = "assistant", cwd = "/tmp/demo", model = "muse-glimmer-30b",
                usageMetadata = new { promptTokenCount = 9275, candidatesTokenCount = 282, totalTokenCount = 9557 } }),
            Line(Api("r1", 9275, 0, 282)),
            Line(Api("r2", 5970, 4000, 630, "memory-extractor")),
            Line(Api("r1", 9275, 0, 282)),     // the same call again (copied by a fork): counted once
        ]);

        var a = new QwenAgent(home);
        Assert.True(a.Installed);
        var r = a.Read(now);
        Assert.Null(r.Error);
        Assert.Equal(9557 + 6600, r.Tokens.TodayTotal);
        var m = Assert.Single(r.Tokens.Models);
        Assert.Equal((9275L + 1970, 912L, 4000L), (m.Input, m.Output, m.Cache));
        var s = Assert.Single(r.Sessions);
        Assert.Equal(("s-1", "say hi", "/tmp/demo", "muse-glimmer-30b"), (s.Id, s.Title, s.Directory, s.Model));
        Assert.Equal([new AgentModel("Default", "muse-glimmer-30b", "openai · 127.0.0.1:11234")], r.Models);
        Assert.Equal(["--resume", "s-1"], a.ResumeArgs(s));
    }

    // ---- waiting for the user

    [Fact]
    public void Codex_UnansweredQuestion_MarksWaiting_AnsweredDoesNot()
    {
        var dir = Path.Combine(_dir, "codex-q");
        Directory.CreateDirectory(dir);
        var t = Iso(DateTimeOffset.Now);
        string Call(string id) => $$$"""{"timestamp":"{{{t}}}","type":"response_item","payload":{"type":"function_call","name":"request_user_input","call_id":"{{{id}}}","arguments":"{}"}}""";
        string Out(string id) => $$$"""{"timestamp":"{{{t}}}","type":"response_item","payload":{"type":"function_call_output","call_id":"{{{id}}}","output":"yes"}}""";
        var waiting = Path.Combine(dir, "a.jsonl");
        File.WriteAllLines(waiting, [Call("c1"), Out("c1"), Call("c2")]);
        var answered = Path.Combine(dir, "b.jsonl");
        File.WriteAllLines(answered, [Call("c1"), $$$"""{"timestamp":"{{{t}}}","type":"event_msg","payload":{"type":"turn_aborted"}}"""]);
        Assert.True(CodexAgent.ParseRollout(waiting).AsksUser);
        Assert.False(CodexAgent.ParseRollout(answered).AsksUser);
    }

    [Fact]
    public void Qwen_UnansweredAskUserQuestion_MarksWaiting()
    {
        var path = Path.Combine(_dir, "q.jsonl");
        string Line(object o) => JsonSerializer.Serialize(o);
        object Ask(string id) => new { sessionId = "s", type = "assistant", model = "m",
            message = new { role = "model", parts = new object[] { new { functionCall = new { id, name = "ask_user_question", args = new { } } } } } };
        object Result(string id) => new { sessionId = "s", type = "tool_result", toolCallResult = new { callId = id, status = "success" } };
        File.WriteAllLines(path, [Line(Ask("a")), Line(Result("a")), Line(Ask("b"))]);
        Assert.True(QwenAgent.ParseFile(path).AsksUser);
        File.WriteAllLines(path, [Line(Ask("a")), Line(Result("a"))]);
        Assert.False(QwenAgent.ParseFile(path).AsksUser);
    }

    [Fact]
    public void Goose_UnansweredToolConfirmation_MarksWaiting()
    {
        var root = Path.Combine(_dir, "goose-wait");
        Directory.CreateDirectory(Path.Combine(root, "data", "sessions"));
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        string Confirm(string id) => $$$"""[{"type":"actionRequired","data":{"actionType":"toolConfirmation","id":"{{{id}}}","toolName":"shell","arguments":{},"prompt":null}}]""";
        string Answer(string id) => $$$"""[{"type":"actionRequired","data":{"actionType":"toolConfirmationResponse","id":"{{{id}}}","permission":"allow_once"}}]""";
        Exec(Path.Combine(root, "data", "sessions", "sessions.db"),
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, name TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT '', session_type TEXT NOT NULL DEFAULT 'user', working_dir TEXT NOT NULL, updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP, total_tokens INTEGER, parent_session_id TEXT, archived_at TIMESTAMP)",
            "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, content_json TEXT NOT NULL, created_timestamp INTEGER NOT NULL)",
            "INSERT INTO sessions (id, name, working_dir) VALUES ('w', 'waits', '/tmp'), ('d', 'done', '/tmp')",
            $"INSERT INTO messages (session_id, role, content_json, created_timestamp) VALUES ('w','assistant','{Confirm("1")}',{now}), ('w','user','{Answer("1")}',{now}), ('w','assistant','{Confirm("2")}',{now})",
            $"INSERT INTO messages (session_id, role, content_json, created_timestamp) VALUES ('d','assistant','{Confirm("1")}',{now}), ('d','user','[{{\"type\":\"toolResponse\",\"id\":\"1\"}}]',{now})");
        var r = new GooseAgent(root).Read(DateTimeOffset.Now);
        Assert.Null(r.Error);
        Assert.Equal(SessionGroup.Needs, r.Sessions.Single(s => s.Id == "w").Group);
        Assert.Equal(SessionGroup.Done, r.Sessions.Single(s => s.Id == "d").Group);
    }

    [Fact]
    public void Toml_TablesStringsAndComments()
    {
        var t = TokenPerch.Core.CodexAgent.ParseConfig("""
            # top
            model = 'gpt-5.5'   # inline comment
            approval_policy = "never"
            [model_providers."my-srv"]
            base_url = "http://h:1/v1"
            [[mcp_servers.x.args]]
            model = "ignored"
            """);
        Assert.Equal([new AgentModel("Default", "gpt-5.5", "openai")], t);
    }
}
