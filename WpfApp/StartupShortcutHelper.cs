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
