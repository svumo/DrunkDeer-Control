using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WpfApp.Profile;

// Crash-safe file operations for profile and settings JSON. Pure System.IO
// with no app state, so Driver.Tests compiles this file in by link.
//
// Guarantees:
//   * A write either fully lands or leaves the previous file untouched
//     (temp file + flush-to-disk + File.Replace / File.Move).
//   * The previous version survives as "<file>.bak" after each overwrite.
//   * Renames write the new file before the old one is moved to ".bak".
//   * Unreadable files are moved aside to ".corrupt", never deleted, and
//     never silently overwritten by a new profile with the same name.
//
// Side files never end in ".json", so profile discovery ignores them.
public static class ProfileFileStore
{
    public const string BakSuffix = ".bak";
    public const string CorruptSuffix = ".corrupt";
    private const string TmpSuffix = ".tmp";
    public const int MaxNameLength = 64;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> so a crash
    /// or power cut at any point leaves either the old or the new file, never
    /// a truncated one. The replaced version is kept as "<path>.bak".
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}{TmpSuffix}";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            WithRetry(() =>
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, path + BakSuffix, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, path);
            });
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>
    /// Saves content under a new file name. The new file is written first;
    /// only then is the old file moved to "<oldPath>.bak". A case-only rename
    /// ("game" → "Game") is handled as a move on the same file.
    /// Throws <see cref="IOException"/> if a different file already exists at
    /// <paramref name="newPath"/>, so one profile can never overwrite another.
    /// </summary>
    public static void RenameAndWrite(string oldPath, string newPath, string content)
    {
        bool sameFile = string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase);
        if (sameFile)
        {
            if (File.Exists(oldPath) && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
                WithRetry(() => File.Move(oldPath, newPath));
            WriteAtomic(newPath, content);
            return;
        }

        if (File.Exists(newPath))
            throw new IOException($"A profile file named '{Path.GetFileName(newPath)}' already exists");

        WriteAtomic(newPath, content);
        if (File.Exists(oldPath))
            WithRetry(() => File.Move(oldPath, oldPath + BakSuffix, overwrite: true));
    }

    /// <summary>
    /// Moves an unreadable file to "<path>.corrupt" (or a timestamped variant
    /// if that already exists) and returns the new path. The content is kept
    /// so the user or a support request can recover it.
    /// </summary>
    public static string Quarantine(string path)
    {
        var target = path + CorruptSuffix;
        if (File.Exists(target))
            target = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}{CorruptSuffix}";
        WithRetry(() => File.Move(path, target));
        return target;
    }

    /// <summary>
    /// Copies <paramref name="path"/> to "<path>.<tag>.bak" unless that backup
    /// already exists. Used before a schema migration rewrites a file.
    /// </summary>
    public static string? BackupOnce(string path, string tag)
    {
        var target = $"{path}.{tag}{BakSuffix}";
        if (File.Exists(target) || !File.Exists(path)) return null;
        File.Copy(path, target);
        return target;
    }

    /// <summary>
    /// Deletes temp files left by a write that was interrupted mid-way.
    /// Only call while no write can be in progress (single instance, startup).
    /// </summary>
    public static int CleanupStaleTemps(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(directory, "*" + TmpSuffix))
            if (TryDelete(f)) n++;
        return n;
    }

    /// <summary>
    /// Returns a user-facing reason why <paramref name="name"/> can't be used
    /// as a profile name, or null if it's fine. <paramref name="otherNames"/>
    /// are the names of every OTHER profile (comparison ignores case, because
    /// Windows file names do).
    /// </summary>
    public static string? ValidateName(string? name, IEnumerable<string> otherNames)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name != name.Trim()) return "Names can't start or end with a space.";
        if (name.Length > MaxNameLength) return $"Names can be at most {MaxNameLength} characters.";
        if (name.EndsWith('.')) return "Names can't end with a full stop.";
        var bad = name.Where(c => Path.GetInvalidFileNameChars().Contains(c)).Distinct().ToArray();
        if (bad.Length > 0)
        {
            var shown = string.Join(" ", bad.Where(c => !char.IsControl(c)));
            return shown.Length > 0
                ? $"Names can't contain {shown}"
                : "Names can't contain control characters.";
        }
        var stem = name.Split('.')[0].TrimEnd();
        if (ReservedDeviceNames.Contains(stem)) return $"\"{name}\" is reserved by Windows. Pick another name.";
        if (otherNames.Any(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase)))
            return $"A profile called \"{name}\" already exists.";
        return null;
    }

    // Antivirus and indexers briefly lock freshly written files; a short
    // retry turns those transient sharing violations into a non-event.
    private static void WithRetry(Action action, int attempts = 4)
    {
        for (int i = 1; ; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < attempts) { Thread.Sleep(40 * i); }
            catch (UnauthorizedAccessException) when (i < attempts) { Thread.Sleep(40 * i); }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
