using ClaudeTray.Core;
using Microsoft.Data.Sqlite;

namespace ClaudeTray.Tests;

public class AgentsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("claude-tray-agents-").FullName;

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

    [Fact]
    public void OpenCodeConfig_DefaultFirst_ThenOtherModels()
    {
        var json = """
            {
              // comments and trailing commas are allowed in opencode.jsonc
              "provider": {
                "cachy-ai": { "name": "cachy-ai (server)", "options": { "baseURL": "http://127.0.0.1:11234/v1" },
                              "models": { "muse-glimmer-30b": { "name": "Muse" } } },
                "laptop":   { "options": { "baseURL": "http://127.0.0.1:11434/v1" },
                              "models": { "gpt-oss-agent": {} } },
              },
              "model": "cachy-ai/muse-glimmer-30b",
            }
            """;
        var m = OpenCodeAgent.ParseConfig(json);
        Assert.Equal(2, m.Count);
        Assert.Equal(new AgentModel("Default", "muse-glimmer-30b", "cachy-ai (server) · 127.0.0.1:11234"), m[0]);
        Assert.Equal(new AgentModel("Also set up", "gpt-oss-agent", "laptop · 127.0.0.1:11434"), m[1]);
    }

    [Fact]
    public void HermesConfig_DefaultAndBackup()
    {
        var yaml = """
            model:
              default: muse-glimmer-30b   # the server
              provider: custom
              base_url: "http://127.0.0.1:11234/v1"
              context_length: 65536

            # Used only when the server can't be reached
            fallback_providers:
              - provider: custom
                model: gpt-oss-agent
                base_url: http://127.0.0.1:11434/v1
              - provider: openrouter
                model: some/model
            telemetry:
              shared_metrics:
                enabled: false
            """;
        var m = HermesAgent.ParseConfig(yaml);
        Assert.Equal(
        [
            new AgentModel("Default", "muse-glimmer-30b", "127.0.0.1:11234"),
            new AgentModel("Backup", "gpt-oss-agent", "127.0.0.1:11434"),
            new AgentModel("Backup", "some/model", "openrouter"),
        ], m);
    }

    [Fact]
    public void OpenCode_TokensPerMessage_PlusSessionRemainder_AndSessions()
    {
        var data = Path.Combine(_dir, "oc");
        Directory.CreateDirectory(data);
        var db = Path.Combine(data, "opencode.db");
        var now = DateTimeOffset.Now;
        long t = now.ToUnixTimeMilliseconds() - 60_000, old = now.AddDays(-30).ToUnixTimeMilliseconds();
        Exec(db,
            "CREATE TABLE session_v2 (id text PRIMARY KEY, parent_id text, title text, directory text NOT NULL, " +
            "tokens_input integer, tokens_output integer, tokens_reasoning integer, tokens_cache_read integer, tokens_cache_write integer, " +
            "time_updated integer, time_busy integer, time_idle integer, idle_outcome text, time_archived integer)",
            "CREATE TABLE session_message (id text PRIMARY KEY, session_id text, type text, seq integer, time_created integer, data text)",
            // s1: 2 replies (100+10+5 and 200+20) and 30 more input in the session's own total (title call)
            $"INSERT INTO session_v2 VALUES ('s1', NULL, 'Fix build', '/tmp', 330, 30, 0, 5, 0, {t}, {t - 5000}, {t}, 'succeeded', NULL)",
            $"INSERT INTO session_message VALUES ('m1','s1','assistant',1,{t - 2000}," +
            "'{\"model\":{\"id\":\"muse\",\"providerID\":\"srv\"},\"tokens\":{\"input\":100,\"output\":10,\"reasoning\":0,\"cache\":{\"read\":5,\"write\":0}}}')",
            $"INSERT INTO session_message VALUES ('m2','s1','assistant',2,{t - 1000}," +
            "'{\"model\":{\"id\":\"muse\",\"providerID\":\"srv\"},\"tokens\":{\"input\":200,\"output\":20,\"reasoning\":0,\"cache\":{\"read\":0,\"write\":0}}}')",
            $"INSERT INTO session_message VALUES ('u1','s1','user',0,{t - 3000},'{{}}')",
            // s2: still busy; s3: subagent (hidden); s4: a month old (hidden)
            $"INSERT INTO session_v2 VALUES ('s2', NULL, '', '/tmp', 0, 0, 0, 0, 0, {t}, {t}, {t - 1}, NULL, NULL)",
            $"INSERT INTO session_v2 VALUES ('s3', 's1', 'child', '/tmp', 0, 0, 0, 0, 0, {t}, NULL, NULL, NULL, NULL)",
            $"INSERT INTO session_v2 VALUES ('s4', NULL, 'old', '/tmp', 9, 9, 0, 0, 0, {old}, NULL, NULL, 'failed', NULL)");

        var r = new OpenCodeAgent(data, Path.Combine(_dir, "none")).Read(now);
        Assert.Null(r.Error);
        Assert.Equal(365, r.Tokens.TodayTotal);         // 115 + 220 + 30 remainder
        var muse = Assert.Single(r.Tokens.Models);
        Assert.Equal(("muse", 330L, 30L, 5L), (muse.Name, muse.Input, muse.Output, muse.Cache));
        Assert.Equal(["s1", "s2"], r.Sessions.Select(s => s.Id).OrderBy(x => x));
        var s1 = r.Sessions.Single(s => s.Id == "s1");
        Assert.Equal((SessionGroup.Done, "muse", 365L, "Fix build"), (s1.Group, s1.Model, s1.Tokens, s1.Title));
        var s2 = r.Sessions.Single(s => s.Id == "s2");
        Assert.Equal((SessionGroup.Working, "(untitled)"), (s2.Group, s2.Title));
    }

    [Fact]
    public void Hermes_TokensPerSessionAndModel_AndSessions()
    {
        var home = Path.Combine(_dir, "hermes");
        Directory.CreateDirectory(home);
        var db = Path.Combine(home, "state.db");
        var now = DateTimeOffset.Now;
        double t = now.ToUnixTimeSeconds() - 60;
        Exec(db,
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, title TEXT, cwd TEXT, model TEXT, parent_session_id TEXT, started_at REAL, " +
            "ended_at REAL, end_reason TEXT, last_activity_at REAL, input_tokens INTEGER, output_tokens INTEGER, " +
            "reasoning_tokens INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER)",
            "CREATE TABLE session_model_usage (session_id TEXT, model TEXT, input_tokens INTEGER, output_tokens INTEGER, " +
            "reasoning_tokens INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER, first_seen REAL, last_seen REAL)",
            $"INSERT INTO sessions VALUES ('h1','Describe machine','/tmp','muse',NULL,{t - 100},{t},'cli_close',{t},10,2,0,100,0)",
            $"INSERT INTO sessions VALUES ('h2',NULL,'/tmp','gpt',NULL,{t - 10},NULL,NULL,{t},5,1,0,0,0)",
            $"INSERT INTO session_model_usage VALUES ('h1','muse',10,2,0,100,0,{t - 100},{t})",
            $"INSERT INTO session_model_usage VALUES ('h2','gpt',5,1,0,0,0,{t - 10},{t})");

        var r = new HermesAgent(home).Read(now);
        Assert.Null(r.Error);
        Assert.Equal(118, r.Tokens.TodayTotal);
        Assert.Equal(["muse", "gpt"], r.Tokens.Models.Select(m => m.Name));
        Assert.Equal(SessionGroup.Done, r.Sessions.Single(s => s.Id == "h1").Group);
        Assert.Equal(SessionGroup.Working, r.Sessions.Single(s => s.Id == "h2").Group);
        Assert.Equal(["--resume", "h1", "--in", "/tmp"], new HermesAgent(home).ResumeArgs(r.Sessions.Single(s => s.Id == "h1")));
    }

    [Fact]
    public void UnknownSchema_ReportsError_InsteadOfThrowing()
    {
        var home = Path.Combine(_dir, "hermes-new");
        Directory.CreateDirectory(home);
        Exec(Path.Combine(home, "state.db"), "CREATE TABLE something_else (x INTEGER)");
        var r = new HermesAgent(home).Read(DateTimeOffset.Now);
        Assert.NotNull(r.Error);
        Assert.Empty(r.Sessions);
    }

    [Fact]
    public void HiddenSessions_PersistAndDropIdsNoLongerListed()
    {
        var path = Path.Combine(_dir, "cfg", "hidden-sessions.json");
        var h = new HiddenSessions(path);
        h.Hide("opencode", "a", ["a", "b"]);
        h.Hide("opencode", "gone", ["b", "gone"]);   // "a" is no longer listed: dropped
        h.Hide("hermes", "x", ["x"]);

        var reloaded = new HiddenSessions(path);
        Assert.False(reloaded.IsHidden("opencode", "a"));
        Assert.True(reloaded.IsHidden("opencode", "gone"));
        Assert.True(reloaded.IsHidden("hermes", "x"));
        Assert.False(reloaded.IsHidden("hermes", "gone"));
    }

    [Fact]
    public void Installed_OnlyWhenDatabaseExists() =>
        Assert.False(new HermesAgent(Path.Combine(_dir, "missing")).Installed);
}
