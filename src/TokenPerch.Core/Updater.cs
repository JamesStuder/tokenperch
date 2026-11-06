using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenPerch.Core;

public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string AssetName, string AssetUrl, string? SumsUrl);

/// <summary>
/// Updates from GitHub releases: the latest release's tag (vX.Y.Z) against our version; Update downloads the
/// archive for this platform (tokenperch-X.Y.Z-&lt;rid&gt;.tar.gz / .zip), checks it against SHA256SUMS, unpacks it
/// and runs its installer, which stops this app, replaces it and starts the new one.
/// </summary>
public static class Updater
{
    const string LatestApi = "https://api.github.com/repos/JamesStuder/tokenperch/releases/latest";

    // Before Http: static fields initialise in order, and the client's User-Agent uses it.
    public static Version Current { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("tokenperch", Current.ToString(3)));
        return h;
    }

    /// <summary>linux-x64, linux-arm64, win-x64 or win-arm64.</summary>
    public static string Rid =>
        (OperatingSystem.IsWindows() ? "win-" : "linux-") +
        (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    /// <summary>The latest release if it is newer than this version and has a download for this platform, else null.
    /// Throws on network / API errors.</summary>
    public static async Task<ReleaseInfo?> CheckAsync()
    {
        var json = await Http.GetStringAsync(LatestApi).ConfigureAwait(false);
        var r = Parse(json, Rid);
        Log.Info($"update check: latest {r?.Tag ?? "(none for " + Rid + ")"}, running {Current.ToString(3)}");
        return r is not null && r.Version > new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build)) ? r : null;
    }

    internal static ReleaseInfo? Parse(string json, string rid)
    {
        var rel = JsonNode.Parse(json);
        var tag = (string?)rel?["tag_name"] ?? "";
        if (rel?["draft"]?.GetValue<bool>() == true || rel?["prerelease"]?.GetValue<bool>() == true) return null;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        string? assetName = null, assetUrl = null, sumsUrl = null;
        foreach (var a in rel?["assets"]?.AsArray() ?? [])
        {
            var name = (string?)a?["name"] ?? "";
            var url = (string?)a?["browser_download_url"] ?? "";
            if (name == "SHA256SUMS") sumsUrl = url;
            else if (name.Contains("-" + rid + ".", StringComparison.Ordinal) && (name.EndsWith(".tar.gz") || name.EndsWith(".zip")))
                (assetName, assetUrl) = (name, url);
        }
        if (assetName is null || assetUrl is null) return null;
        return new ReleaseInfo(version, tag, (string?)rel?["html_url"] ?? "", assetName, assetUrl, sumsUrl);
    }

    /// <summary>Downloads, verifies and unpacks the release, then starts its installer. On success the installer
    /// stops this app shortly after; returns an error message otherwise.</summary>
    public static async Task<string?> InstallAsync(ReleaseInfo r, Action<string> progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tokenperch-update-{r.Version}");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            var archive = Path.Combine(dir, r.AssetName);
            progress($"Downloading {r.Tag}…");
            await using (var src = await Http.GetStreamAsync(r.AssetUrl).ConfigureAwait(false))
            await using (var dst = File.Create(archive))
                await src.CopyToAsync(dst).ConfigureAwait(false);

            if (r.SumsUrl is null) return "The release has no SHA256SUMS, so the download can't be checked.";
            var sums = await Http.GetStringAsync(r.SumsUrl).ConfigureAwait(false);
            var expected = ExpectedHash(sums, r.AssetName);
            if (expected is null) return $"SHA256SUMS has no entry for {r.AssetName}.";
            string actual;
            await using (var fs = File.OpenRead(archive))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs).ConfigureAwait(false));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return "The download didn't match its checksum; nothing was installed.";

            progress("Installing…");
            var unpacked = Path.Combine(dir, "unpacked");
            if (archive.EndsWith(".zip")) ZipFile.ExtractToDirectory(archive, unpacked);
            else
            {
                Directory.CreateDirectory(unpacked);
                await using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, unpacked, overwriteFiles: true).ConfigureAwait(false);
            }
            var script = Directory.EnumerateFiles(unpacked, OperatingSystem.IsWindows() ? "install.ps1" : "install.sh",
                SearchOption.AllDirectories).FirstOrDefault();
            if (script is null) return "The release has no installer for this platform.";

            Log.Info($"update: running {script}");
            int? pid = OperatingSystem.IsWindows()
                ? Proc.Start(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                              .. (AutostartEnabled() ? Array.Empty<string>() : ["-NoAutostart"])],
                             cwd: Path.GetDirectoryName(script), hideWindow: true)
                : Proc.Start([.. LinuxDetach(), "sh", script, .. (AutostartEnabled() ? Array.Empty<string>() : ["--no-autostart"])],
                             cwd: Path.GetDirectoryName(script));
            return pid is null ? "Couldn't start the installer." : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                    or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("update", e);
            return "Update failed: " + e.Message;
        }
    }

    internal static string? ExpectedHash(string sums, string file)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == file) return parts[0];
        }
        return null;
    }

    /// <summary>The installer stops this app; run it in its own systemd scope so it isn't taken down with us.</summary>
    static string[] LinuxDetach() =>
        Proc.Which("systemd-run") is { } run
            ? [run, "--user", "--scope", "--quiet", "--collect", $"--unit=tokenperch-update-{Environment.ProcessId}"]
            : Proc.Which("setsid") is { } setsid ? [setsid] : [];

    /// <summary>Keeps the user's start-at-login choice: the installers turn it on unless told not to.</summary>
    static bool AutostartEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("TokenPerch") is not null;
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Paths.Home, ".config");
        return File.Exists(Path.Combine(xdg, "autostart", "tokenperch.desktop"));
    }
}
