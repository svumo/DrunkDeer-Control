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
    /// Pre-WPF redirect check, called from <see cref="Program.Main"/> before
    /// <see cref="PreviousInstallCleaner.RunOnce"/> and before
    /// <c>App()</c> construction. Handles ONLY the silent redirect cases
    /// (canonical exists, version is equal or we're newer) so a numbered
    /// duplicate exe — e.g. <c>DrunkDeer-Control (12).exe</c> — exits
    /// immediately without leaving behind a stray
    /// <c>%LocalAppData%\DrunkDeer-Control (12)\</c> data directory that
    /// PreviousInstallCleaner would otherwise create.
    ///
    /// Dialog cases (we're older, or no canonical exists) require WPF +
    /// MaterialDesign resources, so they're deferred to <see cref="HandleLaunch"/>
    /// which runs in <c>App.OnStartup</c>. Those cases still create the
    /// stray dir, but only when the user is interacting with a dialog —
    /// rare and intentional.
    /// </summary>
    public static LaunchDecision HandleEarlyLaunch()
    {
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--no-install-redirect"))
            {
                DebugLogger.Log("InstallationManager.HandleEarlyLaunch: --no-install-redirect set, skipping");
                return LaunchDecision.Continue;
            }

            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return LaunchDecision.Continue;

            // I AM the canonical → no redirect needed.
            if (PathsEqual(current, CanonicalExePath)) return LaunchDecision.Continue;

            var myVersion = Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0, 0);
            var (canonicalPath, canonicalVersion) = ReadRegistry();

            // Need a canonical install on disk AND a recorded version for any
            // silent decision. Missing either → defer to HandleLaunch (first-launch
            // dialog or full re-check).
            if (string.IsNullOrEmpty(canonicalPath) || !File.Exists(canonicalPath))
                return LaunchDecision.Continue;
            if (canonicalVersion is null) return LaunchDecision.Continue;

            // Equal version → silent redirect to canonical. Same binary modulo
            // filename; no point running both copies.
            if (myVersion == canonicalVersion)
            {
                DebugLogger.Log($"InstallationManager.HandleEarlyLaunch: I'm v{myVersion}, canonical is the same version at '{canonicalPath}' — silently redirecting");
                LaunchAndExit(canonicalPath);
                return LaunchDecision.ExitAfterRedirect;
            }

            // Newer than canonical → silent auto-upgrade. User downloaded a
            // newer build; promote it to canonical and launch from there.
            if (myVersion > canonicalVersion)
            {
                try
                {
                    DebugLogger.Log($"InstallationManager.HandleEarlyLaunch: I'm v{myVersion} > canonical v{canonicalVersion}, auto-upgrading canonical at '{canonicalPath}'");
                    UpgradeCanonical(current);
                    LaunchAndExit(CanonicalExePath);
                    return LaunchDecision.ExitAfterRedirect;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"InstallationManager.HandleEarlyLaunch: auto-upgrade failed — {ex.GetType().Name}: {ex.Message}; deferring to HandleLaunch");
                    return LaunchDecision.Continue;
                }
            }

            // We're older than canonical → defer; HandleLaunch will show the
            // "open installed version?" dialog.
            return LaunchDecision.Continue;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"InstallationManager.HandleEarlyLaunch: top-level exception — {ex.GetType().Name}: {ex.Message}");
            return LaunchDecision.Continue;
        }
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
            // Dev convenience: `dotnet run -- --no-install-redirect` (or launching
            // any exe with this flag) bypasses the canonical-install redirect so a
            // bin/Debug build doesn't bounce to the installed v1.5.0.
            if (Environment.GetCommandLineArgs().Contains("--no-install-redirect"))
            {
                DebugLogger.Log("InstallationManager: --no-install-redirect set, skipping");
                return LaunchDecision.Continue;
            }

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
                // 2a. Same version as canonical → no functional difference between
                // running this copy or the installed one. Silently redirect rather
                // than spamming the user with a pointless "you already have this
                // installed" dialog when they just re-downloaded the current build.
                if (canonicalVersion is not null && myVersion == canonicalVersion)
                {
                    DebugLogger.Log($"InstallationManager: I'm v{myVersion}, canonical is the same version at '{canonicalPath}' — silently redirecting (no prompt)");
                    LaunchAndExit(canonicalPath);
                    return LaunchDecision.ExitAfterRedirect;
                }

                // 2b. We're older → offer to open the installed copy.
                if (canonicalVersion is not null && myVersion < canonicalVersion)
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
        // Drop the single-instance lock BEFORE starting the target. We're
        // about to exit anyway; if we hold the mutex while the target boots,
        // it sees createdNew=false, fails the by-name takeover (browser
        // duplicates like "DrunkDeer-Control (5).exe" don't match), and
        // exits doing nothing — the dialog vanishes and no window appears.
        Program.ReleaseSingleInstanceLock();

        // Best-effort: if PreviousInstallCleaner already created our
        // per-filename APP_DIR (e.g. "%LocalAppData%\DrunkDeer-Control (12)\")
        // and it's still empty because we never reached Settings/Profile load,
        // delete it. Otherwise every numbered redirect leaves a junk dir
        // behind. Silent on any failure — this is purely cosmetic.
        TryRemoveOwnEmptyAppDir();

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
        });
        DebugLogger.Log($"InstallationManager: released single-instance lock and launched '{exePath}', exiting current process");
    }

    /// <summary>
    /// Removes <c>%LocalAppData%\&lt;current exe filename&gt;\</c> if and only
    /// if it's completely empty. Called from <see cref="LaunchAndExit"/> so
    /// numbered duplicate copies (DrunkDeer-Control (10).exe etc.) that get
    /// silently redirected don't leave behind a per-filename junk directory.
    ///
    /// Safety: we never touch the canonical's data dir (would nuke profiles
    /// on a normal launch). Verified two ways:
    ///   - the canonical APP_DIR matches Path.GetFileNameWithoutExtension of
    ///     the canonical exe ("DrunkDeer-Control"), so a hyphenated numbered
    ///     duplicate has a DIFFERENT current dir;
    ///   - we only enter LaunchAndExit when redirecting AWAY from the current
    ///     exe, so the current exe is by definition not the canonical.
    /// Still, only delete when fully empty — guards against weird states where
    /// a future feature decides to write into APP_DIR before redirect.
    /// </summary>
    private static void TryRemoveOwnEmptyAppDir()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return;
            var name = Path.GetFileNameWithoutExtension(current);
            if (string.IsNullOrEmpty(name)) return;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                name);
            if (!Directory.Exists(dir)) return;
            // Defence in depth: if the dir matches the canonical's name, bail.
            // (Canonical exe is "DrunkDeer-Control.exe" by build convention, so
            // any numbered duplicate would have a different name — but a user
            // could manually rename their canonical, so check anyway.)
            var canonicalName = Path.GetFileNameWithoutExtension(CanonicalExePath);
            if (string.Equals(name, canonicalName, StringComparison.OrdinalIgnoreCase)) return;
            // Only delete if empty (no files, no subdirs).
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
            Directory.Delete(dir);
            DebugLogger.Log($"InstallationManager: removed empty per-filename APP_DIR '{dir}'");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"InstallationManager.TryRemoveOwnEmptyAppDir: {ex.GetType().Name}: {ex.Message}");
        }
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
