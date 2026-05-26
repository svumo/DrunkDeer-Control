using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Driver;
using WpfApp.Components.KeyboardView;
using WpfApp.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using KeyboardKey = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using Mouse = System.Windows.Input.Mouse;
using CaptureMode = System.Windows.Input.CaptureMode;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using UserControl = System.Windows.Controls.UserControl;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;

namespace WpfApp.Components.Lighting;

// Lightweight keyboard canvas for the Lighting tab. Re-uses KeyCap visuals
// and KeyboardCanvasViewModel selection logic, but isolates the marquee /
// click handling from KeyboardPerformanceView (which is too deeply coupled
// to AP/DS/US/remap/keystroke-tracking concerns to share cleanly today).
//
// Responsibilities:
//   - Render a row-major grid of KeyCap controls from a LayoutKey 2D array
//   - Forward click events to the SelectionVM (Select / Toggle / Range)
//   - Drag-marquee selection with Replace / Add (Ctrl) / Subtract (Alt) modes
//   - Esc / Ctrl+A keyboard shortcuts
//   - Per-key PaintTint application from a 126*3 byte[] colour map
//   - KeyColorPicked event for eyedropper integration
//
// Explicitly NOT responsible for:
//   - Loading from / writing to a ProfileItem (parent does that)
//   - Sync dispatch / packet building (parent does that)
//   - Brush palette UI / paint actions (LightingView does that)
//
// When the duplication with KeyboardPerformanceView's marquee starts to bite,
// the right next step is to extract a shared base UserControl — but at the
// time of writing, the two views differ in enough small ways (LightingCanvas
// has no AP/DS/US tinting, no LW/RDT pick-state, no remap drawer wiring)
// that keeping them separate-but-parallel is the lower-risk path.
public partial class LightingCanvas : UserControl
{
    private const double UnitWidth = 40.0;
    private const double KeyGap = 4.0;
    private const double NavGutter = 12.0;

    private readonly Dictionary<string, KeyCap> _caps = new();
    private IReadOnlyList<LayoutKey> _flatLayout = Array.Empty<LayoutKey>();

    // Selection model — owned by the parent so paint actions can read the
    // selected set without LightingCanvas leaking the VM out of its surface.
    private KeyboardCanvasViewModel? _vm;

    // 126*3 byte buffer matching LightingProfile.KeyColors. Read-only from
    // the canvas's perspective — paint actions in the parent mutate this
    // (via ApplyKeyColors) and then we re-render the KeyCap tints. Stored
    // as a reference so we don't allocate one per render.
    private byte[] _keyColors = Array.Empty<byte>();

    // Eyedropper mode. When true, the next click on a KeyCap raises
    // KeyColorPicked with that cap's current colour, then exits eyedropper
    // mode. Selection is not modified during eyedropper clicks.
    public bool EyedropperMode { get; set; }

    // ---- Drag-marquee state ----------------------------------------------
    private enum MarqueeMode { Replace, Add, Subtract }
    private bool _isDragging;
    private Point _dragStart;
    private MarqueeMode _dragMode;
    private (KeyCap Cap, string Code, Rect Rect)[]? _dragRects;

    // ---- Events ----------------------------------------------------------

    // Raised when the canvas's selection changes — convenience re-emit of
    // the SelectionVM event so the parent can subscribe to a single source.
    public event EventHandler? SelectionChanged;

    // Raised by an eyedropper click. The colour is the cap's CURRENT
    // PaintTint converted to a (R,G,B) byte triple. (0,0,0) for unpainted
    // keys.
    public event EventHandler<KeyColorPickedEventArgs>? KeyColorPicked;

    public LightingCanvas()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---- Public surface --------------------------------------------------

    // Sets the keyboard layout to render. Tears down any previously built
    // KeyCaps and builds fresh ones — cheap (max 82 caps for A75 Pro) and
    // means callers don't have to think about whether to call this on first
    // attach vs. on hot-plug. Safe to call repeatedly.
    public void SetLayout(IReadOnlyList<IReadOnlyList<LayoutKey>>? layout)
    {
        RowsHost.Children.Clear();
        _caps.Clear();
        _flatLayout = Array.Empty<LayoutKey>();
        if (layout is null) return;

        var flat = new List<LayoutKey>();
        for (int rowIndex = 0; rowIndex < layout.Count; rowIndex++)
        {
            var row = layout[rowIndex];
            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, rowIndex == 0 ? 0 : KeyGap, 0, 0),
            };

            string? prevColumn = null;
            for (int i = 0; i < row.Count; i++)
            {
                var lk = row[i];
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
                cap.CapClick += OnCapClick;
                _caps[lk.Code] = cap;
                flat.Add(lk);
                rowPanel.Children.Add(cap);
                prevColumn = lk.Column;
            }
            RowsHost.Children.Add(rowPanel);
        }
        _flatLayout = flat;

        RefreshAllTints();
    }

    // Attaches the selection ViewModel. Must be called after SetLayout if
    // selection-driven visuals should update on VM mutations. Safe to call
    // multiple times — unsubscribes from the previous VM first.
    public void AttachSelectionViewModel(KeyboardCanvasViewModel vm)
    {
        if (_vm is not null)
        {
            _vm.SelectedKeys.CollectionChanged -= OnSelectionChanged;
        }
        _vm = vm;
        _vm.SelectedKeys.CollectionChanged += OnSelectionChanged;
        RefreshSelectionVisuals();
    }

    // Sets the per-key colour buffer (LightingProfile.KeyColors). Triggers a
    // full tint refresh. The buffer is held by reference, so the parent can
    // mutate it and call RefreshAllTints() to update visuals without
    // reallocating.
    public void SetKeyColors(byte[]? keyColors)
    {
        _keyColors = keyColors ?? Array.Empty<byte>();
        RefreshAllTints();
    }

    // Apply a single colour to the supplied codes. Updates the underlying
    // byte buffer in place (allocating one of size 378 if it wasn't sized
    // yet) and refreshes the affected caps' tints. Returns the new buffer
    // length so the caller can detect a first-paint allocation if needed.
    //
    // R/G/B clamp at byte boundaries by construction. Black (0,0,0) is a
    // valid "off" colour — call ClearKeys to express the same intent more
    // clearly when the colour shouldn't be persisted.
    public int PaintKeys(IEnumerable<string> codes, byte r, byte g, byte b)
    {
        EnsureKeyColorBuffer();
        foreach (var code in codes)
        {
            var lk = FindByCode(code);
            if (lk is null || lk.KeyIndex < 0 || lk.KeyIndex >= 126) continue;
            int off = lk.KeyIndex * 3;
            _keyColors[off]     = r;
            _keyColors[off + 1] = g;
            _keyColors[off + 2] = b;
            if (_caps.TryGetValue(code, out var cap))
            {
                cap.PaintTint = (r == 0 && g == 0 && b == 0)
                    ? null
                    : new SolidColorBrush(Color.FromRgb(r, g, b));
                // Hover-tooltip: human-readable label + hex. Cleared back to
                // null when the key is unpainted so the cap doesn't carry a
                // stale colour string in (0,0,0) state.
                cap.ToolTip = (r == 0 && g == 0 && b == 0)
                    ? null
                    : $"{lk.Label} · #{r:X2}{g:X2}{b:X2}";
            }
        }
        return _keyColors.Length;
    }

    // Convenience: paint everything in the current selection (or no-op if
    // nothing is selected).
    public bool PaintSelected(byte r, byte g, byte b)
    {
        if (_vm is null || _vm.SelectedKeys.Count == 0) return false;
        PaintKeys(_vm.SelectedKeys.ToArray(), r, g, b);
        return true;
    }

    // Clears the supplied codes to (0,0,0) — same byte pattern as
    // unpainted slots. Updates the buffer + tints.
    public void ClearKeys(IEnumerable<string> codes) => PaintKeys(codes, 0, 0, 0);

    // Clears every visible key. Does NOT zero out the rest of the 126-slot
    // array (firmware-reserved entries stay whatever they were, but they're
    // ignored by the wire builder anyway).
    public void ClearAll()
    {
        EnsureKeyColorBuffer();
        foreach (var lk in _flatLayout)
        {
            if (lk.KeyIndex < 0 || lk.KeyIndex >= 126) continue;
            int off = lk.KeyIndex * 3;
            _keyColors[off]     = 0;
            _keyColors[off + 1] = 0;
            _keyColors[off + 2] = 0;
        }
        foreach (var cap in _caps.Values)
        {
            cap.PaintTint = null;
            cap.ToolTip = null;
        }
    }

    // Returns the canvas's current view of the underlying buffer — same
    // reference the parent passed in. Provided so paint actions don't have
    // to keep an external copy.
    public byte[] KeyColors => _keyColors;

    // ---- Construction internals -----------------------------------------

    private LayoutKey? FindByCode(string code) =>
        _flatLayout.FirstOrDefault(k => k.Code == code);

    private void EnsureKeyColorBuffer()
    {
        if (_keyColors.Length >= 126 * 3) return;
        var grown = new byte[126 * 3];
        Array.Copy(_keyColors, grown, Math.Min(_keyColors.Length, grown.Length));
        _keyColors = grown;
    }

    // ---- Visual refresh --------------------------------------------------

    public void RefreshAllTints()
    {
        foreach (var lk in _flatLayout)
        {
            if (!_caps.TryGetValue(lk.Code, out var cap)) continue;
            var tint = ResolveTintFor(lk);
            cap.PaintTint = tint;
            // Mirror the per-key tooltip rule from PaintKeys: human-readable
            // "<Label> · #RRGGBB" for painted caps; null for unpainted.
            if (tint is SolidColorBrush scb)
            {
                var c = scb.Color;
                cap.ToolTip = $"{lk.Label} · #{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            else
            {
                cap.ToolTip = null;
            }
        }
    }

    private Brush? ResolveTintFor(LayoutKey lk)
    {
        if (lk.KeyIndex < 0 || lk.KeyIndex >= 126) return null;
        int off = lk.KeyIndex * 3;
        if (off + 3 > _keyColors.Length) return null;
        byte r = _keyColors[off], g = _keyColors[off + 1], b = _keyColors[off + 2];
        if (r == 0 && g == 0 && b == 0) return null; // unpainted — fall back to default cap visual
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void RefreshSelectionVisuals()
    {
        if (_vm is null)
        {
            foreach (var cap in _caps.Values) cap.IsSelected = false;
            return;
        }
        foreach (var (code, cap) in _caps)
            cap.IsSelected = _vm.SelectedKeys.Contains(code);
    }

    private void OnSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshSelectionVisuals();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Input handlers --------------------------------------------------

    private void OnCapClick(object? sender, KeyCapClickEventArgs e)
    {
        if (!_caps.ContainsKey(e.Code)) return;

        if (EyedropperMode)
        {
            // Pick the current colour, fire the event, exit eyedropper mode.
            // Don't mutate selection — the user is sampling, not editing.
            var lk = FindByCode(e.Code);
            byte r = 0, g = 0, b = 0;
            if (lk is not null && lk.KeyIndex >= 0 && lk.KeyIndex < 126)
            {
                int off = lk.KeyIndex * 3;
                if (off + 3 <= _keyColors.Length)
                {
                    r = _keyColors[off];
                    g = _keyColors[off + 1];
                    b = _keyColors[off + 2];
                }
            }
            KeyColorPicked?.Invoke(this, new KeyColorPickedEventArgs(e.Code, r, g, b));
            EyedropperMode = false;
            return;
        }

        if (_vm is null) return;
        if (e.Shift)      _vm.SelectRange(e.Code);
        else if (e.Ctrl)  _vm.ToggleSelection(e.Code);
        else              _vm.Select(e.Code);
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        // Eyedropper mode swallows background clicks (no marquee).
        if (EyedropperMode) { e.Handled = true; return; }

        var mods = KeyboardKey.Modifiers;
        _dragMode = (mods & ModifierKeys.Control) != 0
            ? MarqueeMode.Add
            : (mods & ModifierKeys.Alt) != 0
                ? MarqueeMode.Subtract
                : MarqueeMode.Replace;
        _dragStart = e.GetPosition(CanvasBorder);
        _isDragging = true;

        // Snapshot cap rects — reused per Move tick. Same optimisation as
        // KeyboardPerformanceView's marquee path.
        var rects = new List<(KeyCap, string, Rect)>(_caps.Count);
        foreach (var (code, cap) in _caps)
        {
            if (!cap.IsLoaded || cap.ActualWidth == 0) continue;
            var r = cap.TransformToVisual(CanvasBorder)
                       .TransformBounds(new Rect(0, 0, cap.ActualWidth, cap.ActualHeight));
            rects.Add((cap, code, r));
        }
        _dragRects = rects.ToArray();

        if (_dragMode == MarqueeMode.Replace && (mods & ModifierKeys.Shift) == 0)
            _vm.ClearSelection();

        Mouse.Capture(CanvasBorder, CaptureMode.SubTree);
        UpdateMarqueeRect();
        MarqueeRect.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        UpdateMarqueeRect();
        UpdateMarqueePreview();
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        CommitMarquee();
        EndDrag();
        e.Handled = true;
    }

    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        foreach (var cap in _caps.Values) cap.IsMarqueePreview = false;
        _isDragging = false;
        _dragRects = null;
        MarqueeRect.Visibility = Visibility.Collapsed;
        MarqueeRect.Width = 0;
        MarqueeRect.Height = 0;
    }

    private void UpdateMarqueeRect()
    {
        var current = Mouse.GetPosition(CanvasBorder);
        var x = Math.Min(_dragStart.X, current.X);
        var y = Math.Min(_dragStart.Y, current.Y);
        var w = Math.Abs(current.X - _dragStart.X);
        var h = Math.Abs(current.Y - _dragStart.Y);
        Canvas.SetLeft(MarqueeRect, x);
        Canvas.SetTop(MarqueeRect, y);
        MarqueeRect.Width = w;
        MarqueeRect.Height = h;
    }

    private Rect MarqueeRectInCanvas() => new(
        Canvas.GetLeft(MarqueeRect),
        Canvas.GetTop(MarqueeRect),
        MarqueeRect.Width,
        MarqueeRect.Height);

    private void UpdateMarqueePreview()
    {
        if (_dragRects is null || _vm is null) return;
        var rect = MarqueeRectInCanvas();
        for (int i = 0; i < _dragRects.Length; i++)
        {
            var (cap, code, capRect) = _dragRects[i];
            var intersects = rect.IntersectsWith(capRect);
            cap.IsMarqueePreview = _dragMode switch
            {
                MarqueeMode.Replace  => intersects,
                MarqueeMode.Add      => intersects,
                MarqueeMode.Subtract => intersects && _vm.SelectedKeys.Contains(code),
                _ => false,
            };
        }
    }

    private void CommitMarquee()
    {
        if (_vm is null) return;
        var rect = MarqueeRectInCanvas();
        var intersected = new List<string>();
        if (_dragRects is not null)
        {
            for (int i = 0; i < _dragRects.Length; i++)
            {
                var (cap, code, capRect) = _dragRects[i];
                if (rect.IntersectsWith(capRect)) intersected.Add(code);
                cap.IsMarqueePreview = false;
            }
        }
        switch (_dragMode)
        {
            case MarqueeMode.Replace:  _vm.SelectedKeys.ReplaceAll(intersected); break;
            case MarqueeMode.Add:      _vm.SelectedKeys.UnionWith(intersected);   break;
            case MarqueeMode.Subtract: _vm.SelectedKeys.ExceptWith(intersected); break;
        }
    }

    private void EndDrag()
    {
        _isDragging = false;
        _dragRects = null;
        MarqueeRect.Visibility = Visibility.Collapsed;
        MarqueeRect.Width = 0;
        MarqueeRect.Height = 0;
        if (Mouse.Captured == CanvasBorder) CanvasBorder.ReleaseMouseCapture();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Escape)
        {
            if (_isDragging)
            {
                Mouse.Capture(null);
                e.Handled = true;
                return;
            }
            if (EyedropperMode)
            {
                EyedropperMode = false;
                e.Handled = true;
                return;
            }
            if (_vm.SelectedKeys.Count > 0)
            {
                _vm.ClearSelection();
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.A && (KeyboardKey.Modifiers & ModifierKeys.Control) != 0)
        {
            _vm.SelectedKeys.ReplaceAll(_flatLayout.Select(k => k.Code));
            e.Handled = true;
        }
    }
}

public sealed class KeyColorPickedEventArgs(string code, byte r, byte g, byte b) : EventArgs
{
    public string Code { get; } = code;
    public byte R { get; } = r;
    public byte G { get; } = g;
    public byte B { get; } = b;
}
