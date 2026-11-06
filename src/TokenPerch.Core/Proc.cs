using System.Diagnostics;

namespace TokenPerch.Core;

public readonly record struct ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Running helper programs: wait for output, or start and leave running.</summary>
public static class Proc
{
    /// <summary>Runs a program to completion (killed after <paramref name="timeout"/>). Never shows a console window.</summary>
    public static ProcResult Run(string exe, IEnumerable<string> args, TimeSpan? timeout = null, string? cwd = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Paths.Home,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new ProcResult(-1, "", "could not start " + exe);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout ?? TimeSpan.FromSeconds(20)))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new ProcResult(-1, "", $"{Path.GetFileName(exe)} timed out");
            }
            return new ProcResult(p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcResult(-1, "", e.Message);
        }
    }

    /// <summary>Starts a program and leaves it running (a terminal window, a browser). Returns its pid, or null.</summary>
    public static int? Start(IReadOnlyList<string> command, string? cwd = null, bool hideWindow = false)
    {
        if (command.Count == 0) return null;
        var psi = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            CreateNoWindow = hideWindow,
            WorkingDirectory = cwd ?? Paths.Home,
        };
        foreach (var a in command.Skip(1)) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            Log.Info("started: " + string.Join(' ', command));
            return p?.Id;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("start " + string.Join(' ', command), e);
            return null;
        }
    }

    /// <summary>Full path of a program on PATH (adds .exe on Windows), or null.</summary>
    public static string? Which(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var exts = OperatingSystem.IsWindows() && !Path.HasExtension(name) ? new[] { ".exe", ".cmd", ".bat" } : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (var ext in exts)
            {
                var full = Path.Combine(dir, name + ext);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
