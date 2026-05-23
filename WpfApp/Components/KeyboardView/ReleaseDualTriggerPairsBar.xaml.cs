using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using UserControl = System.Windows.Controls.UserControl;

namespace WpfApp.Components.KeyboardView;

// Horizontal bar listing the user's Release Dual-Trigger pairs as removable
// chips, with a "+ Add pair" button that asks the parent to enter pair-pick
// mode. Pairs are ORDERED (press → release) — the chip arrow renders that
// direction, and slot ordering is preserved end-to-end through the
// persistence layer to the firmware's Type-4 remap posInGroup byte.
public partial class ReleaseDualTriggerPairsBar : UserControl
{
    public ReleaseDualTriggerPairsBar() { InitializeComponent(); }

    // Parent listens for these. Slot byte indices are firmware-relative
    // (matching KeyboardLayout.LayoutKey.KeyIndex).
    public event EventHandler? AddRequested;
    public event EventHandler<RdtPairEventArgs>? RemoveRequested;
    public event EventHandler<RdtPairEventArgs>? EditRequested;

    // Push the current pair list into the UI. `labelFor` resolves a slot
    // index to its display label ("E", "T", "↑"). Pairs with unknown slots
    // fall back to "0x{hex}" so the user can still see + delete them.
    public void SetPairs(IReadOnlyList<(byte Press, byte Release)> pairs, Func<byte, string> labelFor)
    {
        var chips = new List<UIElement>();
        foreach (var (press, release) in pairs)
        {
            var chip = BuildChip(press, release, labelFor(press), labelFor(release));
            chips.Add(chip);
        }
        ChipsHost.ItemsSource = chips;
    }

    // Show / hide the pick-mode status hint.
    //   isError = false (default) → calm accent-tinted "next-step" prompt.
    //   isError = true            → red warning chip for rejection messages
    //                                ("X is already used"). Stays visible until
    //                                the next pick attempt; designed to be
    //                                impossible to miss in normal flow.
    public void SetPickStatus(string? step, bool isError = false)
    {
        if (string.IsNullOrEmpty(step))
        {
            StatusBorder.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = true;
            return;
        }
        StatusText.Text = step;
        StatusBorder.Visibility = Visibility.Visible;
        if (isError)
        {
            StatusBorder.Background = (Brush)FindResource("DdWarnSoft");
            StatusBorder.BorderBrush = (Brush)FindResource("DdWarn");
            StatusText.Foreground = (Brush)FindResource("DdWarn");
        }
        else
        {
            StatusBorder.Background = (Brush)FindResource("DdAccentSoft");
            StatusBorder.BorderBrush = (Brush)FindResource("DdAccent");
            StatusText.Foreground = (Brush)FindResource("DdAccent");
        }
        AddButton.IsEnabled = false;
    }

    private UIElement BuildChip(byte press, byte release, string labelPress, string labelRelease)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("DdSurfaceRow"),
            BorderBrush = (Brush)FindResource("DdBorderThin"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("DdRadiusMd"),
            Padding = new Thickness(10, 4, 4, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"{labelPress} press → {labelRelease} release. Click to edit AP/DS/US.",
        };
        var inner = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        inner.Children.Add(new TextBlock
        {
            Text = $"{labelPress} → {labelRelease}",
            FontFamily = (FontFamily)FindResource("DdFontSans"),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("DdFg1"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            IsHitTestVisible = false,
        });
        var removeBtn = new Button
        {
            Content = "×",
            Style = (Style)FindResource("ChipRemoveButton"),
            ToolTip = $"Remove pair {labelPress} → {labelRelease}",
        };
        removeBtn.Click += (_, _) => RemoveRequested?.Invoke(this, new RdtPairEventArgs(press, release));
        inner.Children.Add(removeBtn);
        border.Child = inner;
        border.MouseLeftButtonUp += (_, e) =>
        {
            // Only treat clicks on the chip body as edit-requests — the
            // remove button raises its own Click which bubbles up here, so
            // skip when the source was the × button.
            if (e.OriginalSource is Button) return;
            EditRequested?.Invoke(this, new RdtPairEventArgs(press, release));
        };
        return border;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        AddRequested?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class RdtPairEventArgs(byte pressSlot, byte releaseSlot) : EventArgs
{
    public byte PressSlot { get; } = pressSlot;
    public byte ReleaseSlot { get; } = releaseSlot;
}
