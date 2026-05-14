using System;
using System.Collections.Generic;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using KeyInterop = System.Windows.Input.KeyInterop;
using KeyboardKey = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;

namespace WpfApp.Components.KeyboardView;

// Right-side drawer for the Remap tab. Shows the currently-bound action for
// the selected slot(s) and offers a key-capture flow:
//   1. Click "Rebind…" → drawer flips into capture mode.
//   2. The next key event on the WPF window (other than Esc) becomes the
//      new binding. Esc aborts.
//   3. Drawer emits RebindRequested with the captured slot + key code.
//
// Parent (KeyboardDebugWindow) owns the per-key remap table and the sync.
public partial class RemapDrawer : UserControl
{
    private bool _capturing;

    public RemapDrawer()
    {
        InitializeComponent();
    }

    public event EventHandler<RebindRequestedEventArgs>? RebindRequested;
    public event EventHandler? RestoreRequested;
    public event EventHandler? ResetAllRequested;

    // Called by the parent when selection changes. `currentBindingLabel` is
    // a human-readable description of the current binding (e.g. "W", "Esc",
    // "F3 (default)"). Pass null for multi-select-mixed.
    public void SetState(string title, string subtitle, string? currentBindingLabel)
    {
        TitleText.Text = title;
        CodeText.Text = subtitle;
        CurrentBindingText.Text = currentBindingLabel ?? "Mixed across selection";
        EmptyHint.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Visible;
        EndCapture();
    }

    public void ClearSelection()
    {
        TitleText.Text = "No key selected";
        EmptyHint.Visibility = Visibility.Visible;
        EditPanel.Visibility = Visibility.Collapsed;
        EndCapture();
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) { EndCapture(); return; }
        BeginCapture();
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        EndCapture();
        RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        EndCapture();
        ResetAllRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BeginCapture()
    {
        _capturing = true;
        CaptureButtonText.Text = "Listening… (Esc to cancel)";
        CaptureButton.Background = (System.Windows.Media.Brush)FindResource("DdSurfaceRow");
        CaptureHint.Visibility = Visibility.Visible;
        Focus();
        KeyboardKey.Focus(this);
    }

    private void EndCapture()
    {
        _capturing = false;
        CaptureButtonText.Text = "Rebind…";
        CaptureButton.Background = (System.Windows.Media.Brush)FindResource("DdAccent");
        CaptureHint.Visibility = Visibility.Collapsed;
    }

    // Public entry — the parent forwards PreviewKeyDown here while capturing.
    // Returns true if the event was consumed (binding accepted or cancelled).
    public bool TryHandleCaptureKey(KeyEventArgs e)
    {
        if (!_capturing) return false;
        if (e.Key == Key.Escape)
        {
            EndCapture();
            return true;
        }
        // Modifier-only press? Ignore — wait for the real key.
        if (IsPureModifier(e.Key)) return true;

        var hidCode = WpfKeyToHidUsage(e.Key);
        if (hidCode == 0) { EndCapture(); return true; }

        var label = HidUsageLabel(hidCode);
        CurrentBindingText.Text = label;
        EndCapture();
        RebindRequested?.Invoke(this, new RebindRequestedEventArgs(hidCode, label));
        return true;
    }

    private static bool IsPureModifier(Key k) =>
        k is Key.LeftShift or Key.RightShift
          or Key.LeftCtrl or Key.RightCtrl
          or Key.LeftAlt or Key.RightAlt
          or Key.LWin or Key.RWin
          or Key.System;

    // Convert a WPF Key to the HID Keyboard usage code (USB HID usage page 0x07).
    // Covers the common keys used in remap flows. Returns 0 if unmapped.
    public static byte WpfKeyToHidUsage(Key key) =>
        key switch
        {
            >= Key.A and <= Key.Z => (byte)(0x04 + (key - Key.A)),
            Key.D1 => 0x1E, Key.D2 => 0x1F, Key.D3 => 0x20, Key.D4 => 0x21, Key.D5 => 0x22,
            Key.D6 => 0x23, Key.D7 => 0x24, Key.D8 => 0x25, Key.D9 => 0x26, Key.D0 => 0x27,
            Key.Enter => 0x28, Key.Escape => 0x29, Key.Back => 0x2A, Key.Tab => 0x2B,
            Key.Space => 0x2C, Key.OemMinus => 0x2D, Key.OemPlus => 0x2E,
            Key.OemOpenBrackets => 0x2F, Key.Oem6 => 0x30,
            Key.Oem5 => 0x31, Key.OemSemicolon => 0x33, Key.OemQuotes => 0x34,
            Key.Oem3 => 0x35, Key.OemComma => 0x36, Key.OemPeriod => 0x37, Key.OemQuestion => 0x38,
            Key.CapsLock => 0x39,
            Key.F1 => 0x3A, Key.F2 => 0x3B, Key.F3 => 0x3C, Key.F4 => 0x3D, Key.F5 => 0x3E,
            Key.F6 => 0x3F, Key.F7 => 0x40, Key.F8 => 0x41, Key.F9 => 0x42, Key.F10 => 0x43,
            Key.F11 => 0x44, Key.F12 => 0x45,
            Key.PrintScreen => 0x46, Key.Scroll => 0x47, Key.Pause => 0x48,
            Key.Insert => 0x49, Key.Home => 0x4A, Key.PageUp => 0x4B,
            Key.Delete => 0x4C, Key.End => 0x4D, Key.PageDown => 0x4E,
            Key.Right => 0x4F, Key.Left => 0x50, Key.Down => 0x51, Key.Up => 0x52,
            Key.LeftCtrl => 0xE0, Key.LeftShift => 0xE1, Key.LeftAlt => 0xE2, Key.LWin => 0xE3,
            Key.RightCtrl => 0xE4, Key.RightShift => 0xE5, Key.RightAlt => 0xE6, Key.RWin => 0xE7,
            _ => 0,
        };

    // Reverse direction for displaying a previously-saved binding.
    public static string HidUsageLabel(byte usage) =>
        _hidLabels.TryGetValue(usage, out var lbl) ? lbl : $"0x{usage:X2}";

    private static readonly Dictionary<byte, string> _hidLabels = new()
    {
        [0x04] = "A", [0x05] = "B", [0x06] = "C", [0x07] = "D", [0x08] = "E",
        [0x09] = "F", [0x0A] = "G", [0x0B] = "H", [0x0C] = "I", [0x0D] = "J",
        [0x0E] = "K", [0x0F] = "L", [0x10] = "M", [0x11] = "N", [0x12] = "O",
        [0x13] = "P", [0x14] = "Q", [0x15] = "R", [0x16] = "S", [0x17] = "T",
        [0x18] = "U", [0x19] = "V", [0x1A] = "W", [0x1B] = "X", [0x1C] = "Y",
        [0x1D] = "Z",
        [0x1E] = "1", [0x1F] = "2", [0x20] = "3", [0x21] = "4", [0x22] = "5",
        [0x23] = "6", [0x24] = "7", [0x25] = "8", [0x26] = "9", [0x27] = "0",
        [0x28] = "Enter", [0x29] = "Esc", [0x2A] = "Backspace", [0x2B] = "Tab",
        [0x2C] = "Space", [0x2D] = "-", [0x2E] = "=", [0x2F] = "[", [0x30] = "]",
        [0x31] = "\\", [0x33] = ";", [0x34] = "'", [0x35] = "`", [0x36] = ",",
        [0x37] = ".", [0x38] = "/", [0x39] = "Caps Lock",
        [0x3A] = "F1", [0x3B] = "F2", [0x3C] = "F3", [0x3D] = "F4", [0x3E] = "F5",
        [0x3F] = "F6", [0x40] = "F7", [0x41] = "F8", [0x42] = "F9", [0x43] = "F10",
        [0x44] = "F11", [0x45] = "F12",
        [0x46] = "PrtSc", [0x47] = "ScrLk", [0x48] = "Pause",
        [0x49] = "Insert", [0x4A] = "Home", [0x4B] = "PgUp",
        [0x4C] = "Delete", [0x4D] = "End", [0x4E] = "PgDn",
        [0x4F] = "→", [0x50] = "←", [0x51] = "↓", [0x52] = "↑",
        [0xE0] = "L Ctrl", [0xE1] = "L Shift", [0xE2] = "L Alt", [0xE3] = "L Win",
        [0xE4] = "R Ctrl", [0xE5] = "R Shift", [0xE6] = "R Alt", [0xE7] = "R Win",
    };
}

public sealed class RebindRequestedEventArgs(byte hidUsage, string label) : EventArgs
{
    public byte HidUsage { get; } = hidUsage;
    public string Label { get; } = label;
}
