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

// Horizontal bar listing the user's Last Win pairs as removable chips, with
// a "+ Add pair" button that asks the parent (KeyboardDebugWindow) to enter
// pair-pick mode. Pairs are bidirectional in the UI ("W ↔ S") — the wire
// representation duplicates each as (a,b) and (b,a).
public partial class LastWinPairsBar : UserControl
{
    public LastWinPairsBar() { InitializeComponent(); }

    // Parent listens for these. Slot byte indices are firmware-relative
    // (matching KeyboardLayout.LayoutKey.KeyIndex).
    public event EventHandler? AddRequested;
    public event EventHandler<LwPairEventArgs>? RemoveRequested;

    // Push the current pair list into the UI. `labelFor` resolves a slot
    // index to its display label ("W", "Esc", "↑"). Pairs with unknown
    // slots fall back to "0x{hex}" so the user can still see + delete them.
    public void SetPairs(IReadOnlyList<(byte A, byte B)> pairs, Func<byte, string> labelFor)
    {
        var chips = new List<UIElement>();
        foreach (var (a, b) in pairs)
        {
            var chip = BuildChip(a, b, labelFor(a), labelFor(b));
            chips.Add(chip);
        }
        ChipsHost.ItemsSource = chips;
    }

    // Show / hide the pick-mode status hint. `step` is the user-facing
    // message — "Click the first key" then "Click the second key".
    public void SetPickStatus(string? step)
    {
        if (string.IsNullOrEmpty(step))
        {
            StatusText.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = true;
        }
        else
        {
            StatusText.Text = step;
            StatusText.Visibility = Visibility.Visible;
            AddButton.IsEnabled = false;
        }
    }

    private UIElement BuildChip(byte a, byte b, string labelA, string labelB)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("DdSurfaceRow"),
            BorderBrush = (Brush)FindResource("DdBorderThin"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("DdRadiusMd"),
            Padding = new Thickness(10, 4, 4, 4),
            Margin = new Thickness(0, 0, 6, 0),
        };
        var inner = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        inner.Children.Add(new TextBlock
        {
            Text = $"{labelA} ↔ {labelB}",
            FontFamily = (FontFamily)FindResource("DdFontSans"),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("DdFg1"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var removeBtn = new Button
        {
            Content = "×",
            Style = (Style)FindResource("ChipRemoveButton"),
            ToolTip = $"Remove pair {labelA} ↔ {labelB}",
        };
        removeBtn.Click += (_, _) => RemoveRequested?.Invoke(this, new LwPairEventArgs(a, b));
        inner.Children.Add(removeBtn);
        border.Child = inner;
        return border;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        AddRequested?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class LwPairEventArgs(byte slotA, byte slotB) : EventArgs
{
    public byte SlotA { get; } = slotA;
    public byte SlotB { get; } = slotB;
}
