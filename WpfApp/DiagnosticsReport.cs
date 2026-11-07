using System;
using System.Text;
using Driver;
using WpfApp.Profile;

namespace WpfApp;

// Plain-text snapshot of what the app knows about the connected keyboard,
// for bug reports. Shared by "Copy diagnostics" and the pre-filled GitHub
// hardware report. Raw firmware hex matters: "0.033" in #9 could be read
// as several different versions.
//
// Contains no personal data: no profile names, no process trigger paths,
// and the log path is shown relative to %LOCALAPPDATA%.
public static class DiagnosticsReport
{
    public static string Build(KeyboardManager? keyboardManager, ProfileManager? profileManager, Settings? settings)
    {
        var sb = new StringBuilder();
        var appVer = UpdateChecker.CurrentVersion.ToString();
        sb.AppendLine("DrunkDeer Control diagnostics");
        sb.AppendLine($"- App version: {appVer} ({Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")})");
        sb.AppendLine($"- Windows: {Environment.OSVersion.Version}");

        if (keyboardManager?.KeyboardWithSpecs is not { } kb)
        {
            sb.AppendLine("- Keyboard: none connected");
        }
        else
        {
            var specs = kb.Specs;
            var model = KeyboardLayoutResolver.Resolve(kb);
            var caps = specs.GetCapabilities();
            int pid = kb.Keyboard.ProductID;
            sb.AppendLine($"- Model (as detected): {model?.DisplayName ?? "unrecognised"}");
            sb.AppendLine($"- USB VID:PID: 0x{kb.Keyboard.VendorID:X4}:0x{pid:X4}");
            sb.AppendLine($"- TypeCode: {specs.KeyboardType?.ToString() ?? "none"}");
            sb.AppendLine($"- Firmware: {(string.IsNullOrEmpty(specs.FirmwareVersion) ? "unknown" : specs.FirmwareVersion)} (raw 0x{specs.FirmwareVersionNumeric:X4})");
            if (specs.IsOemGen2) sb.AppendLine("- Protocol: gen-2 OEM (0x55 gateway)");
            sb.AppendLine($"- Support tier: {caps.Tier} — {caps.Label}");
            sb.AppendLine($"- Wire precision: {caps.Precision}");
            if (caps.IsTooOld) sb.AppendLine($"- Firmware below known floor: yes (0x{caps.RecommendedFloor:X4}+)");
            sb.AppendLine($"- RGB: {(caps.SupportsRgb ? "enabled" : "off")}{(caps.SupportsCustomRgb ? ", per-key enabled" : "")}");
            var remapState = caps.AllowsRemapAndPairWrites ? "sent"
                : settings?.IsRemapPairWritesAcknowledged(pid) == true ? "sent (confirmed by user)"
                : "withheld (untested on this model)";
            sb.AppendLine($"- Remap + LW/RDT pair writes: {remapState}");
        }

        sb.AppendLine($"- Last push: {profileManager?.LastPushSummary ?? "none this session"}");
        sb.AppendLine($"- Log file: {RedactUserPath(DebugLogger.LogPath)}");
        return sb.ToString();
    }

    // The report gets pasted into public GitHub issues; a raw path would
    // include the Windows user name.
    private static string RedactUserPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local) && path.StartsWith(local, StringComparison.OrdinalIgnoreCase))
            return "%LOCALAPPDATA%" + path[local.Length..];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%" + path[home.Length..];
        return System.IO.Path.GetFileName(path);
    }
}
