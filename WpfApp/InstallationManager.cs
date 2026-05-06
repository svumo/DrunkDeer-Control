using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Driver;
using WpfApp.Components;

namespace WpfApp;

/// <summary>
/// Owns the question "what is the canonical install of DrunkDeer Control,
/// and is the currently-running exe it?". Solves the long-standing problem
/// where users would auto-update the copy in their Downloads folder, then
/// later double-click an older copy from Desktop / a different download
/// and end up running a stale version unknowingly.
///
/// Algorithm on launch (called once from App.OnStartup before MainWindow):
///
///   * Read the registry pointer at HKCU\Software\DrunkDeer Control →
///     {InstalledExePath, InstalledVersion}.
///   * If we ARE the canonical, stamp our version into the registry (in
///     case we just got auto-updated) and continue normally.
///   * If we're NOT the canonical and the canonical exists on disk:
///       - we're older or equal → ask the user: "Open the installed
///         version?" Default yes (3s autoselect). If yes, launch
///         canonical and exit.
///       - we're newer → silently move ourselves over the canonical
///         (the user explicitly downloaded a newer build, that's what
///         they want), launch canonical, exit.
///   * If no canonical exists → show the first-launch install dialog.
///     User can install (copy to %LocalAppData%\DrunkDeer Control\bin)
///     or "Just run once" (continue without installing).
/// </summary>
public static class InstallationManager
{
    private const string RegistryKey = @"Software\DrunkDeer Control";
    private const string ValueExePath = "InstalledExePath";
    private const string ValueVersion = "InstalledVersion";

    /// <summary>
    /// Canonical install path inside the user's profile — no admin needed,
    /// survives Windows feature updates, separate from the data directory
    /// (Program.APP_DIR) so a corrupted exe never takes profiles down with
    /// it.
    /// </summary>
    public static readonly string CanonicalExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DrunkDeer Control", "bin", "DrunkDeer-Control.exe");

    public enum LaunchDecision
    {
        Continue,           // Run normally (we're canonical, or user dismissed prompt).
        ExitAfterRedirect,  // We launched another exe; this process should shut down.
    }

    /// <summary>
    /// Inspects the running exe vs the canonical install and either lets
    /// the app continue, redirects to a different exe, or shows the
    /// first-launch install prompt. Returns whether to keep running.
    /// </summary>
    public static LaunchDecision HandleLaunch()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current))
            {
                DebugLogger.Log("InstallationManager: cannot resolve current exe path, skipping");
                return LaunchDecision.Continue;
            }

            var myVersion = Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0, 0);
            var (canonicalPath, canonicalVersion) = ReadRegistry();

            // Case 1: I AM the canonical (or registry is stale and points
            // exactly here). Refresh the registry stamp in case auto-update
            // bumped my version since last run.
            if (PathsEqual(current, CanonicalExePath))
            {
                WriteRegistry(CanonicalExePath, myVersion);
                DebugLogger.Log($"InstallationManager: running as canonical at '{current}' v{myVersion}");
                return LaunchDecision.Continue;
            }

            // Case 2: There's a canonical install recorded and it exists.
            if (!string.IsNullOrEmpty(canonicalPath) && File.Exists(canonicalPath))
            {
                // 2a. We're older or equal → offer to open the installed copy.
                if (canonicalVersion is not null && myVersion <= canonicalVersion)
                {
                    DebugLogger.Log($"InstallationManager: I'm v{myVersion}, canonical is v{canonicalVersion} at '{canonicalPath}' — prompting user");
                    var result = InstallDialog.ShowOpenInstalled(myVersion, canonicalVersion, canonicalPath);
                    if (result == InstallDialog.Result.OpenInstalled)
                    {
                        LaunchAndExit(canonicalPath);
                        return LaunchDecision.ExitAfterRedirect;
                    }
                    DebugLogger.Log("InstallationManager: user chose to run this older copy anyway");
                    return LaunchDecision.Continue;
                }

                // 2b. We're newer → silently auto-upgrade the canonical.
                // The user just downloaded a newer build; that's clearly
                // what they want as their installed version.
                try
                {
                    DebugLogger.Log($"InstallationManager: I'm v{myVersion} > canonical v{canonicalVersion}, auto-upgrading canonical at '{canonicalPath}'");
                    UpgradeCanonical(current);
                    LaunchAndExit(CanonicalExePath);
                    return LaunchDecision.ExitAfterRedirect;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"InstallationManager: canonical auto-upgrade failed — {ex.GetType().Name}: {ex.Message}; falling back to continue");
                    return LaunchDecision.Continue;
                }
            }

            // Case 3: No canonical (registry empty or canonical was deleted).
            // Ask the user whether to install. Either choice keeps the user
            // running an app right now — they're never blocked.
            DebugLogger.Log("InstallationManager: no canonical install found, prompting user");
            var firstLaunch = InstallDialog.ShowFirstLaunch(myVersion, current, CanonicalExePath);
            if (firstLaunch == InstallDialog.Result.Install)
            {
                try
                {
                    InstallToCanonical(current);
                    LaunchAndExit(CanonicalExePath);
                    return LaunchDecision.ExitAfterRedirect;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"InstallationManager: install failed — {ex.GetType().Name}: {ex.Message}; continuing as portable copy");
                    return LaunchDecision.Continue;
                }
            }
            DebugLogger.Log("InstallationManager: user chose to run portably (no install)");
            return LaunchDecision.Continue;
        }
        catch (Exception ex)
        {
            // Never block the app from starting because of an installer
            // bug — fall through to normal launch.
            DebugLogger.Log($"InstallationManager.HandleLaunch: top-level exception — {ex}");
            return LaunchDecision.Continue;
        }
    }

    /// <summary>
    /// Copies the running exe to the canonical path (creates the parent
    /// directory if missing) and stamps the registry. Throws on any IO
    /// failure — caller treats as "install failed, continue portably".
    /// </summary>
    private static void InstallToCanonical(string sourceExe)
    {
        var dir = Path.GetDirectoryName(CanonicalExePath)!;
        Directory.CreateDirectory(dir);
        File.Copy(sourceExe, CanonicalExePath, overwrite: true);
        var version = Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0, 0);
        WriteRegistry(CanonicalExePath, version);
        DebugLogger.Log($"InstallationManager: installed to '{CanonicalExePath}' v{version}");
    }

    /// <summary>
    /// Replaces the canonical exe with the running exe (newer version
    /// scenario). Same swap dance as AutoUpdater: rename old canonical
    /// to .bak, copy self over, leave .bak for the new process to clean
    /// up on next launch.
    /// </summary>
    private static void UpgradeCanonical(string sourceExe)
    {
        var bak = CanonicalExePath + ".bak";
        if (File.Exists(bak)) try { File.Delete(bak); } catch { }
        File.Move(CanonicalExePath, bak);
        try
        {
            File.Copy(sourceExe, CanonicalExePath, overwrite: false);
        }
        catch
        {
            // Roll back so the user isn't stranded with no canonical.
            try { File.Move(bak, CanonicalExePath); } catch { }
            throw;
        }
        var version = Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0, 0);
        WriteRegistry(CanonicalExePath, version);
        // .bak is left behind; the canonical's next launch deletes it via
        // AutoUpdater.CleanupBakIfPresent (which already runs in OnSourceInitialized).
    }

    private static void LaunchAndExit(string exePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
        });
        DebugLogger.Log($"InstallationManager: launched '{exePath}', exiting current process");
    }

    public static (string? exePath, Version? version) ReadRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            if (key is null) return (null, null);
            var path = key.GetValue(ValueExePath) as string;
            var versionString = key.GetValue(ValueVersion) as string;
            Version? version = null;
            if (!string.IsNullOrEmpty(versionString) && Version.TryParse(versionString, out var v))
                version = v;
            return (path, version);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"InstallationManager.ReadRegistry: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
    }

    private static void WriteRegistry(string exePath, Version version)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
            key.SetValue(ValueExePath, exePath, RegistryValueKind.String);
            key.SetValue(ValueVersion, version.ToString(), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"InstallationManager.WriteRegistry: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
