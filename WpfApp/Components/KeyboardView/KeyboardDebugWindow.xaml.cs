using System.Collections.Generic;
using System.Windows;
using Driver;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;

namespace WpfApp.Components.KeyboardView;

// Static render of KeyboardLayout.A75Pro using KeyCap UserControls. No
// interaction, no selection — just visual verification that the layout matches
// the design-system JSX reference. Launched via the --keyboard-debug CLI flag.
//
// A few keys carry mock actuation values so we can eyeball every visual state:
//   * WASD / E: AP <= 1.0  → HeatLow (blue border)
//   * Space:    AP == 1.5  → neutral
//   * B:        AP >= 2.8  → HeatHigh (warm border)
//   * F:        DS/US > 0  → RT badge + warm border
public partial class KeyboardDebugWindow : Window
{
    private const double UnitWidth = 44.0;
    private const double KeyGap = 4.0;
    private const double NavGutter = 12.0;

    private static readonly Dictionary<string, (double Ap, double Ds, double Us)> Demo = new()
    {
        ["w"]     = (0.5, 0.0, 0.0),
        ["a"]     = (0.5, 0.0, 0.0),
        ["s"]     = (0.5, 0.0, 0.0),
        ["d"]     = (0.5, 0.0, 0.0),
        ["e"]     = (0.2, 0.0, 0.0),
        ["space"] = (1.5, 0.0, 0.0),
        ["b"]     = (3.3, 0.0, 0.0),
        ["f"]     = (0.5, 0.5, 0.5),
    };

    public KeyboardDebugWindow()
    {
        InitializeComponent();
        BuildRows();
    }

    private void BuildRows()
    {
        for (int rowIndex = 0; rowIndex < KeyboardLayout.A75Pro.Count; rowIndex++)
        {
            var row = KeyboardLayout.A75Pro[rowIndex];
            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, rowIndex == 0 ? 0 : KeyGap, 0, 0),
            };

            string? prevColumn = null;
            for (int i = 0; i < row.Count; i++)
            {
                var lk = row[i];

                // Inter-key gap (4px) + nav-column gutter (extra 12px before the
                // right-side nav column). Arrow keys are tighter — no gutter.
                double leftMargin = i == 0 ? 0 : KeyGap;
                if (lk.Column == "nav" && prevColumn != "nav")
                    leftMargin += NavGutter;

                var cap = new KeyCap
                {
                    Code = lk.Code,
                    Label = lk.Label,
                    SubLabel = lk.Sub,
                    UnitWidth = UnitWidth,
                    WidthMultiplier = lk.Width,
                    KeyType = lk.Type,
                    Uncertain = lk.Uncertain,
                    Margin = new Thickness(leftMargin, 0, 0, 0),
                };

                if (Demo.TryGetValue(lk.Code, out var demo))
                {
                    cap.ActuationPoint = demo.Ap;
                    cap.Downstroke = demo.Ds;
                    cap.Upstroke = demo.Us;
                }

                rowPanel.Children.Add(cap);
                prevColumn = lk.Column;
            }

            RowsHost.Children.Add(rowPanel);
        }
    }
}
