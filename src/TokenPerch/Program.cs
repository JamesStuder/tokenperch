using System.Runtime.InteropServices;
using Avalonia;
using TokenPerch.Core;

namespace TokenPerch;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "--text" or "--sessions" or "--agents" or "--update" or "--version" or "--help" or "-h"))
            return Cli.Run(args);

        // One tray per user.
        using var single = new Mutex(true, "TokenPerch-" + Environment.UserName, out bool first);
        if (!first)
        {
            Log.Info("already running; exiting");
            return 0;
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception e)
        {
            Log.Error("fatal", e);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

/// <summary>Terminal mode: `tokenperch --text` prints the limits and today's tokens.</summary>
static class Cli
{
    public static int Run(string[] args)
    {
        // A WinExe has no console of its own: borrow the calling terminal's, unless output is already
        // redirected (a pipe, ssh), which must keep working.
        if (OperatingSystem.IsWindows() && GetStdHandle(-11) == IntPtr.Zero) AttachConsole(-1);
        if (OperatingSystem.IsWindows()) Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("--version"))
        {
            Console.WriteLine("tokenperch " + typeof(Cli).Assembly.GetName().Version?.ToString(3));
            return 0;
        }
        if (args.Contains("--update"))
        {
            var r = Updater.CheckAsync().GetAwaiter().GetResult();
            if (r is null) { Console.WriteLine($"Up to date ({Updater.Current.ToString(3)})."); return 0; }
            var failed = Updater.InstallAsync(r, Console.WriteLine).GetAwaiter().GetResult();
            Console.WriteLine(failed ?? $"Installing {r.Tag}; the installer replaces and restarts Token Perch.");
            return failed is null ? 0 : 1;
        }
        if (args.Contains("--sessions"))
        {
            var r = SessionsService.Get(AppConfig.Load(), new SessionWindows());
            foreach (var s in r.Sessions)
                Console.WriteLine($"{s.Group,-8} {s.Id}  {Format.Ago(s.Updated, DateTimeOffset.Now),4}{(s.Open ? " open" : "")}  {s.Name}  — {Format.OneLine(s.Text, 80)}");
            if (r.Error is { } e) Console.WriteLine("note: " + e);
            return 0;
        }
        if (args.Contains("--agents"))
        {
            var now = DateTimeOffset.Now;
            var found = Agent.All.Where(a => a.Installed).ToList();
            if (found.Count == 0) Console.WriteLine("No other agents found (OpenCode, Hermes).");
            foreach (var a in found)
            {
                var r = a.Read(now);
                Console.WriteLine($"== {a.Name}");
                foreach (var m in r.Models) Console.WriteLine($"   {m.Role,-12} {m.Model}  ({m.Where})");
                Console.WriteLine($"   Today: {Format.Human(r.Tokens.TodayTotal)} tokens ({Format.Human(r.Tokens.TodayOutput)} output)");
                foreach (var m in r.Tokens.Models) Console.WriteLine($"      {m.Name}: {Format.Human(m.Total)}");
                Console.WriteLine("   Week:  " + string.Join("  ", r.Tokens.Days.Select(d => $"{d.Name} {Format.Human(d.Total)}")));
                foreach (var s in r.Sessions.Take(5))
                    Console.WriteLine($"   {s.Group,-8} {Format.Ago(s.Updated, now),4}  {Format.OneLine(s.Title, 50)}  [{s.Model}, {Format.Human(s.Tokens)}]");
                if (r.Error is { } e) Console.WriteLine("   note: " + e);
            }
            return 0;
        }
        if (!args.Contains("--text"))
        {
            Console.WriteLine("tokenperch            start the tray icon\n" +
                              "tokenperch --text     print plan limits and today's tokens\n" +
                              "tokenperch --sessions list background sessions\n" +
                              "tokenperch --agents   other agents (OpenCode, Hermes, Codex, Goose, Pi, Qwen): models, tokens, sessions\n" +
                              "tokenperch --update   install the latest release from GitHub, if newer\n" +
                              "tokenperch --version\n\n" +
                              $"Settings: {AppConfig.FilePath}\nLog: {Log.FilePath}");
            return 0;
        }
        var cfg = AppConfig.Load();
        var u = new UsageService(cfg).GetAsync(force: args.Contains("--refresh")).GetAwaiter().GetResult();
        if (u.Plan.Length > 0) Console.WriteLine(u.Plan);
        foreach (var l in u.Limits.Limits)
        {
            var reset = l.ResetsAt is { } r ? Format.ResetText(r, u.Taken) : "";
            Console.WriteLine($"{l.Label,-22} {l.Percent,3:0}%  {reset}  — {Format.Pace(l, u.Taken)}");
        }
        Console.WriteLine($"Today: {Format.Human(u.Tokens.TodayTotal)} tokens ({Format.Human(u.Tokens.TodayOutput)} output)");
        foreach (var m in u.Tokens.Models) Console.WriteLine($"   {m.Name}: {Format.Human(m.Total)}");
        if (u.Limits.Error is { } err) Console.WriteLine("note: " + err);
        return 0;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int which);
}
