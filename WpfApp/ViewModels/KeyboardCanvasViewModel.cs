using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Driver;
using WpfApp.Utilities;

namespace WpfApp.ViewModels;

// View-model backing the full-keyboard multi-select canvas (Phase D of the
// keyboard rebuild). Owns the SelectedKeys set and the click-anchor; exposes
// derived bits the drawer/UI binds to (DrawerOpen, SelectionDescription).
//
// Selection-changing methods mutate SelectedKeys directly — that's where the
// CollectionChanged signal comes from for the canvas. This VM separately
// raises PropertyChanged for DrawerOpen and SelectionDescription, which key
// off Count and (for the single-key case) the contents of the set.
public sealed class KeyboardCanvasViewModel : INotifyPropertyChanged
{
    private string? selectionAnchor;

    public KeyboardCanvasViewModel()
    {
        SelectedKeys = new ObservableSet<string>();
        // Whenever the set changes, the derived strings/bools change too. The
        // set already raises CollectionChanged for the keyboard view; we just
        // need to forward Count-driven property changes for VM bindings.
        SelectedKeys.CollectionChanged += OnSelectedKeysChanged;
    }

    public ObservableSet<string> SelectedKeys { get; }

    public string? SelectionAnchor
    {
        get => selectionAnchor;
        private set
        {
            if (selectionAnchor == value) return;
            selectionAnchor = value;
            OnPropertyChanged();
        }
    }

    public bool DrawerOpen => SelectedKeys.Count > 0;

    public string SelectionDescription
    {
        get
        {
            return SelectedKeys.Count switch
            {
                0 => "No keys selected",
                1 => $"Key · {LabelFor(SelectedKeys.First())}",
                _ => $"{SelectedKeys.Count} keys selected",
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Plain click: replace selection with this one key, anchor it.
    public void Select(string code)
    {
        SelectedKeys.ReplaceAll(new[] { code });
        SelectionAnchor = code;
    }

    // Ctrl+click-add semantics: include without disturbing existing selection
    // or anchor. (Pure "add", not toggle — that's ToggleSelection.)
    public void AddToSelection(string code)
    {
        SelectedKeys.Add(code);
    }

    // Ctrl+click toggle: remove if present, add if absent. Anchor stays put
    // because the user might shift+click next from their last single-pick.
    public void ToggleSelection(string code)
    {
        SelectedKeys.Toggle(code);
    }

    // Shift+click: select the inclusive range from anchor to toCode in
    // row-major iteration order. If there's no anchor yet we degrade to a
    // single-key Select — matches the JSX reference's behavior.
    public void SelectRange(string toCode)
    {
        if (selectionAnchor is null)
        {
            Select(toCode);
            return;
        }

        var flat = KeyboardLayout.A75ProFlat;
        var anchorIdx = IndexOfCode(flat, selectionAnchor);
        var endIdx = IndexOfCode(flat, toCode);
        if (anchorIdx < 0 || endIdx < 0)
        {
            // One of the codes isn't in the layout (e.g., stale anchor after
            // layout swap). Treat as a single-key click to keep state sane.
            Select(toCode);
            return;
        }

        var (lo, hi) = anchorIdx <= endIdx ? (anchorIdx, endIdx) : (endIdx, anchorIdx);
        var range = new List<string>(hi - lo + 1);
        for (var i = lo; i <= hi; i++)
        {
            range.Add(flat[i].Code);
        }
        SelectedKeys.ReplaceAll(range);
        // Anchor stays at the original anchor — that's the convention so the
        // user can keep extending the range with further shift-clicks.
    }

    public void ClearSelection()
    {
        SelectedKeys.Clear();
        SelectionAnchor = null;
    }

    public void SelectAll()
    {
        var all = KeyboardLayout.A75ProFlat.Select(k => k.Code).ToArray();
        SelectedKeys.ReplaceAll(all);
        // Don't change the anchor — leave whatever the user last clicked so
        // a follow-up shift+click still has a useful pivot.
    }

    private void OnSelectedKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Any change to the set changes both derived strings. Raise both.
        OnPropertyChanged(nameof(DrawerOpen));
        OnPropertyChanged(nameof(SelectionDescription));
    }

    private static int IndexOfCode(IReadOnlyList<LayoutKey> flat, string code)
    {
        for (var i = 0; i < flat.Count; i++)
        {
            if (flat[i].Code == code) return i;
        }
        return -1;
    }

    private static string LabelFor(string code) =>
        KeyboardLayout.FindByCode(code)?.Label ?? code;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
