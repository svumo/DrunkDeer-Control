using Driver;
using System.Diagnostics;
using System.IO;

namespace WpfApp;

/// <summary>
/// On startup, detects user-data directories left behind by previous installs
/// that used a different executable name (e.g. "DrunkDeer-Control.exe" vs
/// "DrunkDeer Control.exe") and merges them into the current install's data
/// directory.
///
/// Runs once per launch, after the single-instance mutex has been acquired and
/// before Settings/ProfileManager load — so migrated profiles are picked up by
/// the rest of the app without any further work.
///
/// Note: the Windows "run at login" entry is intentionally NOT touched here.
/// Startup registration is owned solely by <see cref="StartupShortcutHelper"/>,
/// which keeps a single fixed entry pointed at the canonical install. The old
/// per-filename migration that lived here used to hijack the startup entry
/// onto whatever transient copy was launched (e.g. a Downloads duplicate).
/// </summary>
internal static class PreviousInstallCleaner
{
    /// <summary>Names earlier versions of this app may have used.</summary>
    private static readonly string[] LegacyAppNames =
    [
        "DrunkDeer-Control",
        "DrunkdeerControl",
        "DrunkDeerControl",
    ];

    private const string MigrationMarker = ".migrated-to-current";

    public static void RunOnce()
    {
        try
        {
            var currentExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe))
            {
                DebugLogger.Log("PreviousInstallCleaner: cannot resolve current exe path, skipping");
                return;
            }
            var currentAppName = Path.GetFileNameWithoutExtension(currentExe);
            DebugLogger.Log($"PreviousInstallCleaner: scanning (current='{currentAppName}', exe='{currentExe}')");

            MigrateLegacyDataDirs(currentAppName);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"PreviousInstallCleaner: top-level exception {ex}");
        }
    }

    private static void MigrateLegacyDataDirs(string currentAppName)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var currentDir = Path.Combine(local, currentAppName);
        Directory.CreateDirectory(currentDir);

        foreach (var legacy in LegacyAppNames)
        {
            if (string.Equals(legacy, currentAppName, StringComparison.OrdinalIgnoreCase)) continue;
            var legacyDir = Path.Combine(local, legacy);
            if (!Directory.Exists(legacyDir)) continue;

            var marker = Path.Combine(legacyDir, MigrationMarker);
            if (File.Exists(marker))
            {
                DebugLogger.Log($"PreviousInstallCleaner: '{legacy}' already migrated, skipping");
                continue;
            }

            try
            {
                var copied = MergeDirectory(legacyDir, currentDir);
                File.WriteAllText(marker, $"Migrated to '{currentAppName}' on {DateTime.Now:O} ({copied} files copied)");
                DebugLogger.Log($"PreviousInstallCleaner: migrated {copied} files from '{legacyDir}' to '{currentDir}'");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"PreviousInstallCleaner: migration of '{legacyDir}' failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Copies files from <paramref name="from"/> into <paramref name="to"/>,
    /// preserving relative paths. Existing files in <paramref name="to"/> are
    /// preserved (current install's data wins on conflict).
    /// </summary>
    private static int MergeDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        int copied = 0;
        foreach (var srcFile in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(srcFile);
            if (fileName.Equals(MigrationMarker, StringComparison.OrdinalIgnoreCase)) continue;

            var rel = Path.GetRelativePath(from, srcFile);
            var dst = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst))
            {
                DebugLogger.Log($"  skip (current already has): {rel}");
                continue;
            }
            File.Copy(srcFile, dst);
            copied++;
            DebugLogger.Log($"  copied: {rel}");
        }
        return copied;
    }
}
