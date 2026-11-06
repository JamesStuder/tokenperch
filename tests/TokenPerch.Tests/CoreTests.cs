using System.Text.Json.Nodes;
using TokenPerch.Core;

namespace TokenPerch.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(999, "999")]
    [InlineData(1_500, "1.5k")]
    [InlineData(131_600_000, "131.6M")]
    [InlineData(2_000_000_000, "2.0B")]
    public void Human(long n, string expected) => Assert.Equal(expected, Format.Human(n));

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("claude-fable-5-1", "Fable 5.1")]
    [InlineData(null, "?")]
    public void ModelName(string? id, string expected) => Assert.Equal(expected, Format.ModelName(id));

    [Fact]
    public void Ago()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(10_000_000_000);
        Assert.Equal("now", Format.Ago(now.ToUnixTimeMilliseconds() - 30_000, now));
        Assert.Equal("5m", Format.Ago(now.ToUnixTimeMilliseconds() - 5 * 60_000, now));
        Assert.Equal("3h", Format.Ago(now.ToUnixTimeMilliseconds() - 3 * 3_600_000, now));
        Assert.Equal("2d", Format.Ago(now.ToUnixTimeMilliseconds() - 2 * 86_400_000L, now));
    }

    [Fact]
    public void Until()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("3h 05m", Format.Until(now.AddMinutes(185), now));
        Assert.Equal("42m", Format.Until(now.AddMinutes(42), now));
        Assert.Equal("0m", Format.Until(now.AddMinutes(-5), now));
    }

    [Fact]
    public void OneLine()
    {
        Assert.Equal("a b c", Format.OneLine(" a\n b\t c "));
        Assert.Equal("abcd…", Format.OneLine("abcdefgh", 5));
    }

    [Fact]
    public void PaceOnTrack()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        // 5 h window, 2.5 h in, 20 % used → 40 % at reset
        var l = new Limit("Session", "session", 20, null, now.AddHours(2.5));
        Assert.Equal("on pace for 40% by reset", Format.Pace(l, now));
    }

    [Fact]
    public void PaceTooFast()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var l = new Limit("Session", "session", 80, null, now.AddHours(2.5));
        Assert.StartsWith("at this pace: 100% ", Format.Pace(l, now));
        Assert.EndsWith("before reset", Format.Pace(l, now));
    }

    [Fact]
    public void PaceEarly()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("nothing used yet", Format.Pace(new Limit("S", "session", 0, null, now.AddHours(4)), now));
        Assert.Equal("too early to tell", Format.Pace(new Limit("S", "session", 3, null, now.AddHours(4.9)), now));
        Assert.Equal("", Format.Pace(new Limit("S", "other", 3, null, now.AddHours(4)), now));
    }
}

public class ShellWordsTests
{
    [Fact]
    public void Quotes() =>
        Assert.Equal(["konsole", "-p", "tabtitle=Claude · x", "-e", "it's"],
            ShellWords.Split("konsole -p 'tabtitle=Claude · x' -e \"it's\"", backslashEscapes: true));

    [Fact]
    public void WindowsPaths() =>
        Assert.Equal([@"C:\Users\Studer\.local\bin\claude.exe", "attach", "ab12"],
            ShellWords.Split(@"C:\Users\Studer\.local\bin\claude.exe attach ab12", backslashEscapes: false));
}

public class LimitsTests
{
    [Fact]
    public void Parse()
    {
        const string json = """
        {"limits":[
          {"kind":"session","percent":15,"resets_at":"2026-10-20T18:00:00Z"},
          {"kind":"weekly_all","percent":65.5,"resets_at":"2026-10-21T16:00:00Z","severity":"warning"},
          {"kind":"weekly_scoped","percent":3,"scope":{"model":{"display_name":"Fable"}}}],
         "extra_usage":{"is_enabled":true,"used_credits":4.5}}
        """;
        var s = LimitsService.Parse(json, DateTimeOffset.UnixEpoch);
        Assert.Equal(["Session (5 h)", "Weekly · all models", "Weekly · Fable"], s.Limits.Select(l => l.Label));
        Assert.Equal(65.5, s.Limits[1].Percent);
        Assert.Equal("warning", s.Limits[1].Severity);
        Assert.Null(s.Limits[2].ResetsAt);
        Assert.Equal(4.5, s.ExtraUsed);
    }
}

public class SessionsTests
{
    [Fact]
    public void GroupsAndText()
    {
        const string agents = """
        [{"id":"a1","kind":"background","state":"blocked","startedAt":1000,"name":"a1"},
         {"id":"b2","kind":"background","state":"done","startedAt":2000,"name":"fix build"},
         {"id":"c3","kind":"background","state":"working","startedAt":3000},
         {"id":"d4","kind":"background","state":"failed","startedAt":500},
         {"kind":"interactive","cwd":"/x","startedAt":9000}]
        """;
        var states = new Dictionary<string, JsonNode?>
        {
            ["a1"] = JsonNode.Parse("""{"needs":"Approve the deploy?","intent":"deploy the site","updatedAt":"2026-10-20T12:00:00Z"}"""),
            ["b2"] = JsonNode.Parse("""{"output":{"result":"Build fixed.\nAll green."}}"""),
            ["c3"] = JsonNode.Parse("""{"detail":"running tests"}"""),
        };
        var list = SessionsService.Parse(agents, id => states.GetValueOrDefault(id), (id, _) => id == "b2");

        Assert.Equal(4, list.Count);
        var a = list.Single(s => s.Id == "a1");
        Assert.Equal(SessionGroup.Needs, a.Group);
        Assert.Equal("deploy the site", a.Name);          // name == id → intent
        Assert.Equal("Approve the deploy?", a.Text);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T12:00:00Z").ToUnixTimeMilliseconds(), a.Updated);
        var b = list.Single(s => s.Id == "b2");
        Assert.Equal(SessionGroup.Done, b.Group);
        Assert.Equal("Build fixed. All green.", b.Text);
        Assert.True(b.Open);
        Assert.Equal("Untitled session", list.Single(s => s.Id == "d4").Name);
        Assert.Equal("a1", list[0].Id);                   // newest first
    }
}

public class TokenStatsTests
{
    [Fact]
    public void SumsTodayAndDedupes()
    {
        var dir = Directory.CreateTempSubdirectory("tokenperch-test");
        try
        {
            var now = DateTimeOffset.Now;
            var today = now.ToUniversalTime().ToString("O");
            var yesterday = now.AddDays(-1).ToUniversalTime().ToString("O");
            string Line(string ts, string id, string model, int input, int output) =>
                $$"""{"type":"assistant","timestamp":"{{ts}}","requestId":"r{{id}}","message":{"id":"{{id}}","model":"{{model}}","usage":{"input_tokens":{{input}},"output_tokens":{{output}},"cache_read_input_tokens":100""" + "}}}";
            Directory.CreateDirectory(Path.Combine(dir.FullName, "proj"));
            File.WriteAllLines(Path.Combine(dir.FullName, "proj", "s.jsonl"),
            [
                Line(today, "m1", "claude-opus-5-5", 10, 5),
                Line(today, "m1", "claude-opus-5-5", 10, 5),                 // duplicate
                Line(today, "m2", "claude-haiku-5-5", 1, 1),
                Line(yesterday, "m3", "claude-opus-5-5", 1000, 0),
                Line(today, "m4", "<synthetic>", 1, 1),
                """{"type":"user","message":{"content":"no usage here"}}""",
            ]);
            var stats = new TokenStats(dir.FullName);
            var r = stats.Compute(now);
            Assert.Equal(115 + 102, r.TodayTotal);
            Assert.Equal(6, r.TodayOutput);
            Assert.Equal(["Opus 5.5", "Haiku 5.5"], r.Models.Select(m => m.Name));
            Assert.Equal(1100, r.Days[^2].Total);
            Assert.True(r.Days[^1].Today);
            Assert.Equal("claude-opus-5-5", stats.CurrentModel());   // last non-synthetic assistant line
        }
        finally { dir.Delete(true); }
    }
}


public class UpdaterTests
{
    const string Release = """
    {"tag_name":"v2.1.0","html_url":"https://github.com/JamesStuder/tokenperch/releases/tag/v2.1.0","draft":false,"prerelease":false,
     "assets":[{"name":"tokenperch-2.1.0-linux-x64.tar.gz","browser_download_url":"https://x/l.tar.gz"},
               {"name":"tokenperch-2.1.0-win-x64.zip","browser_download_url":"https://x/w.zip"},
               {"name":"SHA256SUMS","browser_download_url":"https://x/SHA256SUMS"}]}
    """;

    [Fact]
    public void PicksAssetForPlatform()
    {
        var l = Updater.Parse(Release, "linux-x64")!;
        Assert.Equal(new Version(2, 1, 0), l.Version);
        Assert.Equal("tokenperch-2.1.0-linux-x64.tar.gz", l.AssetName);
        Assert.Equal("https://x/SHA256SUMS", l.SumsUrl);
        Assert.Equal("https://x/w.zip", Updater.Parse(Release, "win-x64")!.AssetUrl);
        Assert.Null(Updater.Parse(Release, "linux-arm64"));
    }

    [Fact]
    public void SkipsPrerelease() => Assert.Null(Updater.Parse(Release.Replace("\"prerelease\":false", "\"prerelease\":true"), "linux-x64"));

    [Fact]
    public void ReadsChecksums()
    {
        const string sums = "abc123  tokenperch-2.1.0-linux-x64.tar.gz\ndef456  tokenperch-2.1.0-win-x64.zip\n";
        Assert.Equal("def456", Updater.ExpectedHash(sums, "tokenperch-2.1.0-win-x64.zip"));
        Assert.Null(Updater.ExpectedHash(sums, "other.zip"));
    }
}
