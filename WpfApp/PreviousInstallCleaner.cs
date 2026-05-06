using Driver;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace WpfApp;

/// <summary>
/// On startup, detects artifacts left behind by previous installs that used a
/// different executable name (e.g. "DrunkDeer-Control.exe" vs "DrunkDeer Control.exe")
/// and migrates user data + startup intent into the current install's locations.
///
/// Runs once per launch, after the single-instance mutex has been acquired and
/// before Settings/ProfileManager load — so migrated profiles are picked up by
/// the rest of the app without any further work.
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

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
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
            CleanLegacyStartupEntries(currentExe, currentAppName);
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

    private static void CleanLegacyStartupEntries(string currentExe, string currentAppName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        var existingNames = key.GetValueNames();
        bool currentIsRegistered = existingNames.Any(n =>
            string.Equals(n, currentAppName, StringComparison.OrdinalIgnoreCase));
        bool migratedStartupIntent = false;

        foreach (var valueName in existingNames)
        {
            bool isLegacy = LegacyAppNames.Any(n =>
                string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase));
            if (!isLegacy) continue;

            var raw = key.GetValue(valueName) as string;
            DebugLogger.Log($"PreviousInstallCleaner: found legacy startup entry '{valueName}' = {raw}");

            // The user previously asked for autostart. If the current install does not
            // already have an entry, register it now so that intent survives the rename.
            if (!currentIsRegistered && !migratedStartupIntent)
            {
                var newCommand = $"\"{currentExe}\" --start-minimized";
                try
                {
                    key.SetValue(currentAppName, newCommand);
                    DebugLogger.Log($"PreviousInstallCleaner: migrated startup intent to '{currentAppName}' = {newCommand}");
                    currentIsRegistered = true;
                    migratedStartupIntent = true;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"PreviousInstallCleaner: failed to set '{currentAppName}': {ex.Message}");
                }
            }

            try
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
                DebugLogger.Log($"PreviousInstallCleaner: removed legacy startup entry '{valueName}'");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"PreviousInstallCleaner: failed removing '{valueName}': {ex.Message}");
            }
        }
    }
}
