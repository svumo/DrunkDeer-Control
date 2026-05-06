using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace WpfApp;

public static class StartupShortcutHelper
{
    private static readonly string APP_NAME = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "DrunkDeer Control");
    private const string REGISTRY_KEY_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Checks if the application is registered to run on Windows startup using Registry
    /// </summary>
    public static bool StartupFileExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, false);
            return key?.GetValue(APP_NAME) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds the application to Windows startup using Registry (.NET 8 compatible)
    /// </summary>
    private static void AddToStartup()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is null || exePath.Equals(string.Empty))
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (exePath is null) return;

            // Add --start-minimized argument
            var command = $"\"{exePath}\" --start-minimized";

            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, true);
            key?.SetValue(APP_NAME, command);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to add to startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the application from Windows startup
    /// </summary>
    private static void RemoveFromStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, true);
            if (key?.GetValue(APP_NAME) != null)
            {
                key.DeleteValue(APP_NAME);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to remove from startup: {ex.Message}");
        }
    }

    /// <summary>
    /// If startup is enabled and the registry entry points to a different path
    /// (e.g. user moved the exe or installed a new version), updates the entry
    /// to point to the current exe. Call this once at startup.
    /// </summary>
    public static void SelfHealStartupRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY_PATH, true);
            if (key is null) return;

            var existing = key.GetValue(APP_NAME) as string;
            if (existing is null) return; // startup not enabled — nothing to do

            var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe is null) return;

            var expectedCommand = $"\"{currentExe}\" --start-minimized";
            if (!string.Equals(existing, expectedCommand, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(APP_NAME, expectedCommand);
                Console.WriteLine($"Startup registration updated: {expectedCommand}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SelfHealStartupRegistration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the checkbox change event for startup toggle
    /// </summary>
    public static void OnCheckChanged(bool isChecked)
    {
        if (isChecked)
        {
            AddToStartup();
        }
        else
        {
            RemoveFromStartup();
        }
    }
}
