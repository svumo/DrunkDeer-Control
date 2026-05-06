using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Driver;

namespace WpfApp;

public sealed record DownloadProgress(long Downloaded, long Total);
public sealed record DownloadResult(string StagedPath, long Bytes);

public static class AutoUpdater
{
    // Staged exe lives under %LocalAppData%\DrunkDeer Control\update — same
    // root as profiles/settings, so cleanup is trivial and we never write
    // anywhere outside the user's app data on any code path.
    private static readonly string UpdateDir = Path.Combine(Program.APP_DIR, "update");
    public static readonly string StagedPath = Path.Combine(UpdateDir, "staged.exe");

    public static string BakPath(string currentExe) => currentExe + ".bak";

    /// <summary>
    /// Returns true if we can write a sibling file to the running exe's
    /// directory. False means the install is in a write-protected location
    /// (Program Files, etc.) and we should fall back to the browser flow.
    /// </summary>
    public static bool CanWriteToInstallDir(out string reason)
    {
        reason = "";
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current))
        {
            reason = "Cannot resolve current exe path";
            return false;
        }

        var dir = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            reason = "Install directory not found";
            return false;
        }

        var probe = Path.Combine(dir, $".ddc-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            DebugLogger.Log($"AutoUpdater.CanWriteToInstallDir: probe failed in '{dir}' — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Downloads the update to <see cref="StagedPath"/> with progress and
    /// cancellation. Throws on any failure (caller surfaces in UI). Cleans
    /// up the partial file if cancelled or thrown.
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        string url,
        long? expectedSize,
        IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(UpdateDir);
        // Wipe any leftover staged file from a previous attempt.
        TryDelete(StagedPath);

        DebugLogger.Log($"AutoUpdater.Download: starting from '{url}' to '{StagedPath}'");

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            // GitHub asset redirects use a User-Agent check; supply one to be safe.
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DrunkDeer-Control/{UpdateChecker.CurrentVersion}");

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? expectedSize ?? -1L;
            using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var dst = new FileStream(StagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    progress?.Report(new DownloadProgress(downloaded, total));
                }
            }

            var info = new FileInfo(StagedPath);
            // Sanity floor: the published single-file exe is ~169 MB. Anything
            // smaller than 50 MB means we got a redirect HTML page or a
            // truncated stream — bail rather than try to install garbage.
            const long MinPlausibleBytes = 50L * 1024 * 1024;
            if (info.Length < MinPlausibleBytes)
                throw new InvalidDataException($"Downloaded file is suspiciously small ({info.Length} bytes)");

            // If the server gave us a Content-Length, require an exact match.
            if (total > 0 && info.Length != total)
                throw new InvalidDataException($"Downloaded {info.Length} bytes, expected {total}");

            DebugLogger.Log($"AutoUpdater.Download: ok ({info.Length} bytes)");
            return new DownloadResult(StagedPath, info.Length);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"AutoUpdater.Download: failed — {ex.GetType().Name}: {ex.Message}");
            TryDelete(StagedPath);
            throw;
        }
    }

    /// <summary>
    /// Renames the running exe to .bak, moves the staged exe into its place,
    /// launches the new exe, and shuts down the application. Throws on swap
    /// failure (caller transitions to Failed state).
    /// </summary>
    public static void ApplyAndRestart(string stagedPath)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve current exe path");
        var bak = BakPath(currentExe);

        DebugLogger.Log($"AutoUpdater.ApplyAndRestart: currentExe='{currentExe}' staged='{stagedPath}'");

        // Wipe any leftover .bak from a previous failed run (or a prior update
        // that didn't get cleaned up — though OnSourceInitialized normally does
        // this).
        TryDelete(bak);

        // Step 1: rename the running exe out of the way. Windows allows this
        // because the OS pins the file handle to the inode, not the path.
        File.Move(currentExe, bak);
        DebugLogger.Log($"AutoUpdater.ApplyAndRestart: renamed running exe → '{bak}'");

        try
        {
            // Step 2: drop the new build at the original path.
            File.Move(stagedPath, currentExe);
            DebugLogger.Log($"AutoUpdater.ApplyAndRestart: moved staged → '{currentExe}'");
        }
        catch (Exception ex)
        {
            // Roll back: put the old exe back so the user isn't stranded.
            DebugLogger.Log($"AutoUpdater.ApplyAndRestart: swap failed — {ex.GetType().Name}: {ex.Message}; rolling back");
            try
            {
                File.Move(bak, currentExe);
                DebugLogger.Log("AutoUpdater.ApplyAndRestart: rollback restored running exe");
            }
            catch (Exception rollbackEx)
            {
                DebugLogger.Log($"AutoUpdater.ApplyAndRestart: ROLLBACK FAILED — {rollbackEx}");
            }
            throw;
        }

        // Step 3: launch the new exe.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExe,
                UseShellExecute = true
            });
            DebugLogger.Log("AutoUpdater.ApplyAndRestart: new process started");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"AutoUpdater.ApplyAndRestart: failed to start new exe — {ex.GetType().Name}: {ex.Message}");
            // Don't roll back — the new exe is in place and the user can
            // launch it manually. Surface the error.
            throw;
        }

        // Step 4 (the shutdown of the old process) is the caller's job —
        // Application.Current.Shutdown() must run on the dispatcher thread
        // and ApplyAndRestart is invoked from a background Task.Run. The
        // new process will see the leftover .bak alongside it and delete
        // it on first launch via CleanupBakIfPresent.
    }

    /// <summary>
    /// Deletes the .bak left behind by a previous update. Safe to call any
    /// time — silent no-op if there's nothing to clean.
    /// </summary>
    public static void CleanupBakIfPresent()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current)) return;
        var bak = BakPath(current);
        if (!File.Exists(bak)) return;
        try
        {
            File.Delete(bak);
            DebugLogger.Log($"AutoUpdater.CleanupBak: removed '{bak}'");
        }
        catch (Exception ex)
        {
            // Not fatal — a Windows AV might still be hashing it. Try again
            // next launch.
            DebugLogger.Log($"AutoUpdater.CleanupBak: could not remove '{bak}' yet — {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void CleanupStagedIfPresent()
    {
        TryDelete(StagedPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { DebugLogger.Log($"AutoUpdater.TryDelete: '{path}' — {ex.GetType().Name}: {ex.Message}"); }
    }
}
