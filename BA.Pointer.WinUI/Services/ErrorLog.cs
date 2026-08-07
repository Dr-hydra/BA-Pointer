namespace BA.Pointer.Services;

public static class ErrorLog
{
    public static void Write(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BA.Pointer");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "error.log"), $"[{DateTimeOffset.Now:O}]\n{exception}\n\n");
        }
        catch
        {
            // Diagnostics must never mask the original renderer error.
        }
    }
}
