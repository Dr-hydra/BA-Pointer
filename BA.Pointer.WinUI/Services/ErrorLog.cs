namespace BA.Pointer.Services;

public static class ErrorLog
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BA.Pointer");

    public static string RuntimeLogPath => Path.Combine(DirectoryPath, "runtime.log");

    public static void WriteInfo(string source, string message) => Append("INFO", source, message);

    public static void WriteWarning(string source, string message) => Append("WARN", source, message);

    public static void Write(Exception exception, string source = "Application") =>
        Append("ERROR", source, exception.ToString());

    private static void Append(string level, string source, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                var line = $"[{DateTimeOffset.Now:O}] [{level}] [{source}] " +
                           $"[pid={Environment.ProcessId}, thread={Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
                File.AppendAllText(RuntimeLogPath, line);
            }
        }
        catch
        {
            // Diagnostics must never mask the original renderer error.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(RuntimeLogPath) || new FileInfo(RuntimeLogPath).Length < MaximumLogBytes) return;
        var previousPath = Path.Combine(DirectoryPath, "runtime.previous.log");
        File.Copy(RuntimeLogPath, previousPath, overwrite: true);
        File.WriteAllText(RuntimeLogPath, string.Empty);
    }
}
