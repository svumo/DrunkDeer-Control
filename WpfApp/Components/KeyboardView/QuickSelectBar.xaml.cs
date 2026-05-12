using System;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using KeyboardKey = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;

namespace WpfApp.Components.KeyboardView;

// Horizontal pill bar above the keyboard canvas. Each pill names a logical
// group of keys (WASD, all letters, modifiers, etc.) and clicking selects
// that group with the same modifier semantics as the marquee:
//
//   click       Replace — set selection to this group
//   Ctrl+click  Add     — union with current selection
//   Alt+click   Subtract — remove this group from current selection
//
// The parent (KeyboardDebugWindow) translates pill names into key codes by
// inspecting the currently-active LayoutKey list — that way the same pill bar
// works for A75 Pro / G65 / G60 / A75 Ultra without per-layout copies.
public partial class QuickSelectBar : UserControl
{
    public QuickSelectBar()
    {
        InitializeComponent();
    }

    /// <summary>Fired on every pill click. Parent resolves the group name into
    /// concrete key codes and applies the selection per the chosen mode.</summary>
    public event EventHandler<QuickSelectEventArgs>? PillClicked;

    private void OnPillClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string group) return;

        var mods = KeyboardKey.Modifiers;
        var mode = (mods & ModifierKeys.Control) != 0
            ? QuickSelectMode.Add
            : (mods & ModifierKeys.Alt) != 0
                ? QuickSelectMode.Subtract
                : QuickSelectMode.Replace;

        PillClicked?.Invoke(this, new QuickSelectEventArgs(group, mode));
    }
}

public enum QuickSelectMode { Replace, Add, Subtract }

public sealed class QuickSelectEventArgs(string group, QuickSelectMode mode) : EventArgs
{
    public string Group { get; } = group;
    public QuickSelectMode Mode { get; } = mode;
}
