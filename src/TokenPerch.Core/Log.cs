namespace TokenPerch.Core;

/// <summary>Plain text log in the cache dir (tokenperch.log), rotated at 1 MB.</summary>
public static class Log
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(Paths.CacheDir, "tokenperch.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.CacheDir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
