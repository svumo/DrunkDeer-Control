using System.Reflection;

namespace Driver;

public static class DebugLogger
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DrunkDeer Control",
        "debug.log"
    );

    private const long MaxBytes = 2 * 1024 * 1024;
    private const long CheckEveryBytes = 64 * 1024;

    private static readonly object _lock = new();
    private static long _bytesSinceCheck;

    static DebugLogger()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); } catch { }
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxBytes) Rotate();
        }
        catch { }
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        Log($"=== Session start (Driver v{version}, OS {Environment.OSVersion}, .NET {Environment.Version}) ===");
    }

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
                _bytesSinceCheck += line.Length;
                if (_bytesSinceCheck >= CheckEveryBytes)
                {
                    _bytesSinceCheck = 0;
                    try
                    {
                        var fi = new FileInfo(LogPath);
                        if (fi.Exists && fi.Length > MaxBytes) Rotate();
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void Rotate()
    {
        var oldPath = LogPath + ".old";
        try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
        try { File.Move(LogPath, oldPath); } catch { }
    }
}
