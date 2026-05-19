using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Driver;

namespace WpfApp;

/// <summary>
/// Owns the single Windows "run at login" registry entry.
///
/// Why this is careful: the value name and target path used to be derived
/// from whichever exe filename the user happened to launch. Browser
/// duplicates ("DrunkDeer-Control (5).exe") and the canonical install
/// ("DrunkDeer-Control.exe") therefore wrote *different* Run entries — so the
/// toggle looked like it "didn't save" depending on which copy you opened,
/// and startup could be left pointing at a Downloads file that later got
/// deleted. Now there is exactly ONE logical entry: a fixed value name,
/// always pointing at the canonical install.
/// </summary>
public static class StartupShortcutHelper
{
    /// <summary>
    /// Fixed Run-key value name. Must NOT depend on the running exe's
    /// filename. Equals the canonical exe's base name, so users who already
    /// have startup enabled from an installed copy need no migration.
    /// </summary>
    private const string APP_NAME = "DrunkDeer-Control";

    private const string REGISTRY_KEY_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Matches our entry under any name a past build may have written:
    /// "DrunkDeer-Control", "DrunkDeer Control", "DrunkDeerControl",
    /// "DrunkdeerControl", optionally with a " (N)" browser-duplicate suffix.
    /// </summary>
    private static readonly Regex OurNamePattern =
        new(@"^drunkdeer[ -]?control( \(\d+\))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The exe Windows should launch at login: the canonical install if it
    /// exists (stable across in-place auto-updates), otherwise the running
    /// exe (best effort for a never-installed portable copy).
    /// </summary>
    private static string? ResolveTargetExe()
    {
        var canonical = InstallationManager.CanonicalExePath;
        if (!string.IsNullOrEmpty(canonical) && File.Exists(canonical))
            return canonical;
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
    }

    private static string BuildCommand(string exePath) => $"\"{exePath}\" --start-minimized";

    /// <summary>
    /// True if startup is enabled under the canonical name OR any
    /// legacy/duplicate variant — so the toggle reflects reality no matter
    /// which copy of the exe is currently running.
    /// </summary>
    public static bool StartupFileExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, false);
            if (key is null) return false;
            return key.GetValueNames().Any(n => OurNamePattern.IsMatch(n));
        }
        catch
        {
            return false;
        }
    }

    private static void AddToStartup()
    {
        try
        {
            var target = ResolveTargetExe();
            if (target is null) return;
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, true);
            if (key is null) return;
            PurgeAllVariants(key); // collapse any stragglers first
            var command = BuildCommand(target);
            key.SetValue(APP_NAME, command);
            DebugLogger.Log($"StartupShortcutHelper: enabled startup → {APP_NAME} = {command}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"StartupShortcutHelper.AddToStartup failed: {ex.Message}");
        }
    }

    private static void RemoveFromStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, true);
            if (key is null) return;
            var removed = PurgeAllVariants(key);
            if (removed > 0)
                DebugLogger.Log($"StartupShortcutHelper: disabled startup (removed {removed} entr{(removed == 1 ? "y" : "ies")})");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"StartupShortcutHelper.RemoveFromStartup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Collapses the historical mess (per-filename names, " (N)" duplicates,
    /// stale Downloads paths) into a single canonical entry. If the user has
    /// startup enabled under ANY variant, after this call there is exactly
    /// one entry: APP_NAME → canonical exe. If it was disabled, no-op.
    /// Idempotent; safe to call every launch.
    /// </summary>
    public static void SelfHealStartupRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, true);
            if (key is null) return;

            var variants = key.GetValueNames().Where(n => OurNamePattern.IsMatch(n)).ToList();
            if (variants.Count == 0) return; // startup off — nothing to do

            var target = ResolveTargetExe();
            if (target is null) return;
            var desired = BuildCommand(target);

            // Already exactly one correct entry pointing at the right exe?
            // Leave it alone (avoids a pointless registry write every launch).
            if (variants.Count == 1 &&
                string.Equals(variants[0], APP_NAME, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(key.GetValue(APP_NAME) as string, desired, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var v in variants)
            {
                try { key.DeleteValue(v, throwOnMissingValue: false); } catch { }
            }
            key.SetValue(APP_NAME, desired);
            DebugLogger.Log($"StartupShortcutHelper: consolidated {variants.Count} startup entr{(variants.Count == 1 ? "y" : "ies")} → {APP_NAME} = {desired}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"StartupShortcutHelper.SelfHealStartupRegistration failed: {ex.Message}");
        }
    }

    /// <summary>Deletes every Run value matching our name pattern. Returns the count removed.</summary>
    private static int PurgeAllVariants(RegistryKey key)
    {
        int removed = 0;
        foreach (var name in key.GetValueNames().Where(n => OurNamePattern.IsMatch(n)).ToList())
        {
            try { key.DeleteValue(name, throwOnMissingValue: false); removed++; } catch { }
        }
        return removed;
    }

    /// <summary>Handles the checkbox change event for the startup toggle.</summary>
    public static void OnCheckChanged(bool isChecked)
    {
        if (isChecked) AddToStartup();
        else RemoveFromStartup();
    }
}
