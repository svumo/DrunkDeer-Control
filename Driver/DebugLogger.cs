using System.Reflection;

namespace Driver;

public static class DebugLogger
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DrunkDeer Control",
        "debug.log"
    );

    private static readonly object _lock = new();

    static DebugLogger()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); } catch { }
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        Log($"=== Session start (Driver v{version}, OS {Environment.OSVersion}, .NET {Environment.Version}) ===");
    }

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
