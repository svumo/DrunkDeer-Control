using System;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;

namespace WpfApp.Components.KeyboardView;

// Bottom-bar presets: FPS tuning (WASD shallow + space short), Typing (all
// letters deep), Reset (everything back to 2.0 mm default).
//
// Apply-target rule (parent enforces):
//   - If a selection is active, apply only to selected keys ∩ preset's
//     natural target set. Lets the user "select X, click FPS" workflow.
//   - If no selection, apply to the preset's natural target set across the
//     entire profile.
//
// Live counts on the right are updated by the parent — drawer mirrors the
// state of the underlying caps after every edit / sync.
public partial class PresetBar : UserControl
{
    public PresetBar()
    {
        InitializeComponent();
    }

    public event EventHandler<PresetClickedEventArgs>? PresetClicked;

    public void UpdateCounts(int customized, int rapidTrigger)
    {
        CustomizedCount.Text = $"{customized} customized";
        RtCount.Text = $"{rapidTrigger} in Rapid Trigger";
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string preset)
            PresetClicked?.Invoke(this, new PresetClickedEventArgs(preset));
    }
}

public sealed class PresetClickedEventArgs(string preset) : EventArgs
{
    public string Preset { get; } = preset; // "fps", "typing", "reset"
}
