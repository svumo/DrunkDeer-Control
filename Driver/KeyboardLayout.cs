namespace Driver;

// Maps the A75 Pro's physical keyboard layout to slots in Profile.Keys_Array.
//
// Why this is two pieces of data, not one:
//   * Profile.Keys_Array is a fixed 126-slot array — the firmware's wire format.
//     Each model uses a different subset of slots; many slots are intentionally
//     blank. The slot index is what the firmware understands.
//   * The A75 Pro physical layout is 6 rows of caps with widths (1.0, 1.25,
//     1.5, 1.75, 2.0, 2.25, 6.25). That's what the user sees and clicks.
//
// LayoutKey ties the two together: a single key's visual properties + its
// slot index in Keys_Array. KeyboardLayout.A75Pro is the row-major list of
// LayoutKeys. Looking up by code or by KeyIndex is O(n) but n is ~80 so
// it doesn't matter.
//
// The KeyName → KeyIndex mapping was derived empirically from a real
// Profile.json dump — see plans/keyboard-performance-and-remap.md for the
// verification step. Keys marked "uncertain" need on-hardware confirmation
// once interaction is wired up in Phase B (click a cap, press the matching
// physical key, see whether the slot lights up).
public sealed record LayoutKey
{
    // Stable identifier used in code/CSS — lowercase, no spaces ("esc", "w", "lshift").
    public required string Code { get; init; }

    // What's painted on the cap ("Esc", "W", "Shift", "↑").
    public required string Label { get; init; }

    // Optional second character (e.g., "`" under "~" on the grave key).
    public string? Sub { get; init; }

    // Cap width as a multiple of 1u. Std keys are 1.0; Tab is 1.5; Caps is
    // 1.75; LShift/Enter are 2.0/2.25; LCtrl/LWin/LAlt are 1.25; Space is 6.25.
    public double Width { get; init; } = 1.0;

    // "mod" for modifier keys (Shift, Ctrl, Alt, Win, Fn, Menu). Triggers
    // dimmed label color in the UI.
    public string? Type { get; init; }

    // "nav" for the right-edge navigation column (Home/PgUp/PgDn/End) — adds
    // a gutter before the cap. "arrow" for arrow keys — pulls them closer.
    public string? Column { get; init; }

    // Index into Profile.Keys_Array. -1 means the key isn't in the firmware
    // protocol (shouldn't happen for A75 Pro keys, but safe default).
    public required int KeyIndex { get; init; }

    // The KeyName string used by the firmware/profile JSON. Useful for
    // round-tripping with existing profile files.
    public required string ProfileKeyName { get; init; }

    // Some A75 Pro slots are uncertain — they exist in the profile but the
    // physical key they map to on this model hasn't been verified on-hardware
    // yet. Mark them so we can flag them visually during Phase B testing.
    public bool Uncertain { get; init; }
}

public static class KeyboardLayout
{
    // Row-major 6-row layout for the A75 Pro (ANSI). Mirrors
    // design-system/ui_kits/control_app/keyboard_layout.js. Each entry's
    // KeyIndex points into a real 126-slot Profile.Keys_Array.
    public static IReadOnlyList<IReadOnlyList<LayoutKey>> A75Pro { get; } =
    [
        // Row 0 — Esc, F-row, Del. The rotary encoder lives top-right but is
        // not in Keys_Array (it's a knob, not a switch with actuation tuning) —
        // so the nav column starts in row 1, not row 0.
        [
            new() { Code = "esc",  Label = "Esc",  KeyIndex = 0,  ProfileKeyName = "ESC" },
            new() { Code = "f1",   Label = "F1",   KeyIndex = 2,  ProfileKeyName = "F1" },
            new() { Code = "f2",   Label = "F2",   KeyIndex = 3,  ProfileKeyName = "F2" },
            new() { Code = "f3",   Label = "F3",   KeyIndex = 4,  ProfileKeyName = "F3" },
            new() { Code = "f4",   Label = "F4",   KeyIndex = 5,  ProfileKeyName = "F4" },
            new() { Code = "f5",   Label = "F5",   KeyIndex = 6,  ProfileKeyName = "F5" },
            new() { Code = "f6",   Label = "F6",   KeyIndex = 7,  ProfileKeyName = "F6" },
            new() { Code = "f7",   Label = "F7",   KeyIndex = 8,  ProfileKeyName = "F7" },
            new() { Code = "f8",   Label = "F8",   KeyIndex = 9,  ProfileKeyName = "F8" },
            new() { Code = "f9",   Label = "F9",   KeyIndex = 10, ProfileKeyName = "F9" },
            new() { Code = "f10",  Label = "F10",  KeyIndex = 11, ProfileKeyName = "F10" },
            new() { Code = "f11",  Label = "F11",  KeyIndex = 12, ProfileKeyName = "F11" },
            new() { Code = "f12",  Label = "F12",  KeyIndex = 13, ProfileKeyName = "F12" },
            new() { Code = "del",  Label = "Del",  KeyIndex = 14, ProfileKeyName = "KP7", Uncertain = true },
        ],
        // Row 1 — Grave, digits, Backspace, Home
        [
            new() { Code = "grave",    Label = "~", Sub = "`", KeyIndex = 21, ProfileKeyName = "SWUNG" },
            new() { Code = "1",        Label = "1", Sub = "!", KeyIndex = 22, ProfileKeyName = "1" },
            new() { Code = "2",        Label = "2", Sub = "@", KeyIndex = 23, ProfileKeyName = "2" },
            new() { Code = "3",        Label = "3", Sub = "#", KeyIndex = 24, ProfileKeyName = "3" },
            new() { Code = "4",        Label = "4", Sub = "$", KeyIndex = 25, ProfileKeyName = "4" },
            new() { Code = "5",        Label = "5", Sub = "%", KeyIndex = 26, ProfileKeyName = "5" },
            new() { Code = "6",        Label = "6", Sub = "^", KeyIndex = 27, ProfileKeyName = "6" },
            new() { Code = "7",        Label = "7", Sub = "&", KeyIndex = 28, ProfileKeyName = "7" },
            new() { Code = "8",        Label = "8", Sub = "*", KeyIndex = 29, ProfileKeyName = "8" },
            new() { Code = "9",        Label = "9", Sub = "(", KeyIndex = 30, ProfileKeyName = "9" },
            new() { Code = "0",        Label = "0", Sub = ")", KeyIndex = 31, ProfileKeyName = "0" },
            new() { Code = "minus",    Label = "-", Sub = "_", KeyIndex = 32, ProfileKeyName = "MINUS" },
            new() { Code = "equal",    Label = "=", Sub = "+", KeyIndex = 33, ProfileKeyName = "PLUS" },
            new() { Code = "bksp",     Label = "Backspace", Width = 2.0, KeyIndex = 34, ProfileKeyName = "BACK" },
            new() { Code = "home",     Label = "Home", KeyIndex = 35, ProfileKeyName = "KP4", Column = "nav", Uncertain = true },
        ],
        // Row 2 — Tab, top letter row, [ ] \, PgUp
        [
            new() { Code = "tab",      Label = "Tab", Width = 1.5, KeyIndex = 42, ProfileKeyName = "TAB" },
            new() { Code = "q",        Label = "Q", KeyIndex = 43, ProfileKeyName = "Q" },
            new() { Code = "w",        Label = "W", KeyIndex = 44, ProfileKeyName = "W" },
            new() { Code = "e",        Label = "E", KeyIndex = 45, ProfileKeyName = "E" },
            new() { Code = "r",        Label = "R", KeyIndex = 46, ProfileKeyName = "R" },
            new() { Code = "t",        Label = "T", KeyIndex = 47, ProfileKeyName = "T" },
            new() { Code = "y",        Label = "Y", KeyIndex = 48, ProfileKeyName = "Y" },
            new() { Code = "u",        Label = "U", KeyIndex = 49, ProfileKeyName = "U" },
            new() { Code = "i",        Label = "I", KeyIndex = 50, ProfileKeyName = "I" },
            new() { Code = "o",        Label = "O", KeyIndex = 51, ProfileKeyName = "O" },
            new() { Code = "p",        Label = "P", KeyIndex = 52, ProfileKeyName = "P" },
            new() { Code = "lbracket", Label = "[", Sub = "{", KeyIndex = 53, ProfileKeyName = "BRKTS_L" },
            new() { Code = "rbracket", Label = "]", Sub = "}", KeyIndex = 54, ProfileKeyName = "BRKTS_R" },
            new() { Code = "bslash",   Label = "\\", Sub = "|", Width = 1.5, KeyIndex = 55, ProfileKeyName = "SLASH_K29" },
            new() { Code = "pgup",     Label = "PgUp", KeyIndex = 56, ProfileKeyName = "KP1", Column = "nav", Uncertain = true },
        ],
        // Row 3 — Caps, home letter row, ; ', Enter, PgDn
        [
            new() { Code = "caps",  Label = "Caps", Width = 1.75, KeyIndex = 63, ProfileKeyName = "CAPS" },
            new() { Code = "a",     Label = "A", KeyIndex = 64, ProfileKeyName = "A" },
            new() { Code = "s",     Label = "S", KeyIndex = 65, ProfileKeyName = "S" },
            new() { Code = "d",     Label = "D", KeyIndex = 66, ProfileKeyName = "D" },
            new() { Code = "f",     Label = "F", KeyIndex = 67, ProfileKeyName = "F" },
            new() { Code = "g",     Label = "G", KeyIndex = 68, ProfileKeyName = "G" },
            new() { Code = "h",     Label = "H", KeyIndex = 69, ProfileKeyName = "H" },
            new() { Code = "j",     Label = "J", KeyIndex = 70, ProfileKeyName = "J" },
            new() { Code = "k",     Label = "K", KeyIndex = 71, ProfileKeyName = "K" },
            new() { Code = "l",     Label = "L", KeyIndex = 72, ProfileKeyName = "L" },
            new() { Code = "semi",  Label = ";", Sub = ":",  KeyIndex = 73, ProfileKeyName = "COLON" },
            new() { Code = "quote", Label = "'", Sub = "\"", KeyIndex = 74, ProfileKeyName = "QOTATN" },
            new() { Code = "enter", Label = "Enter", Width = 2.25, KeyIndex = 76, ProfileKeyName = "RETURN" },
            new() { Code = "pgdn",  Label = "PgDn",  KeyIndex = 78, ProfileKeyName = "KP0", Column = "nav", Uncertain = true },
        ],
        // Row 4 — LShift, bottom letter row, RShift, Up, End
        [
            new() { Code = "lshift", Label = "Shift", Width = 2.25, Type = "mod", KeyIndex = 84, ProfileKeyName = "SHF_L" },
            new() { Code = "z",      Label = "Z", KeyIndex = 86, ProfileKeyName = "Z" },
            new() { Code = "x",      Label = "X", KeyIndex = 87, ProfileKeyName = "X" },
            new() { Code = "c",      Label = "C", KeyIndex = 88, ProfileKeyName = "C" },
            new() { Code = "v",      Label = "V", KeyIndex = 89, ProfileKeyName = "V" },
            new() { Code = "b",      Label = "B", KeyIndex = 90, ProfileKeyName = "B" },
            new() { Code = "n",      Label = "N", KeyIndex = 91, ProfileKeyName = "N" },
            new() { Code = "m",      Label = "M", KeyIndex = 92, ProfileKeyName = "M" },
            new() { Code = "comma",  Label = ",", Sub = "<", KeyIndex = 93, ProfileKeyName = "COMMA" },
            new() { Code = "period", Label = ".", Sub = ">", KeyIndex = 94, ProfileKeyName = "PERIOD" },
            new() { Code = "slash",  Label = "/", Sub = "?", KeyIndex = 95, ProfileKeyName = "VIRGUE" },
            new() { Code = "rshift", Label = "Shift", Width = 1.75, Type = "mod", KeyIndex = 97, ProfileKeyName = "SHF_R" },
            new() { Code = "up",     Label = "↑", Column = "arrow", KeyIndex = 98, ProfileKeyName = "ARR_UP" },
            new() { Code = "end",    Label = "End", Column = "nav", KeyIndex = 100, ProfileKeyName = "NUMS", Uncertain = true },
        ],
        // Row 5 — LCtrl, LWin, LAlt, Spacebar, RAlt, Fn, Menu, arrows
        [
            new() { Code = "lctrl", Label = "Ctrl", Width = 1.25, Type = "mod", KeyIndex = 105, ProfileKeyName = "CTRL_L" },
            new() { Code = "lwin",  Label = "Win",  Width = 1.25, Type = "mod", KeyIndex = 106, ProfileKeyName = "WIN_L" },
            new() { Code = "lalt",  Label = "Alt",  Width = 1.25, Type = "mod", KeyIndex = 107, ProfileKeyName = "ALT_L" },
            new() { Code = "space", Label = "Spacebar", Width = 6.25, KeyIndex = 111, ProfileKeyName = "SPACE" },
            new() { Code = "ralt",  Label = "Alt",  Type = "mod", KeyIndex = 115, ProfileKeyName = "ALT_R" },
            new() { Code = "fn",    Label = "Fn",   Type = "mod", KeyIndex = 116, ProfileKeyName = "FN1" },
            new() { Code = "menu",  Label = "Menu", Type = "mod", KeyIndex = 117, ProfileKeyName = "APP" },
            new() { Code = "left",  Label = "←", Column = "arrow", KeyIndex = 118, ProfileKeyName = "ARR_L" },
            new() { Code = "down",  Label = "↓", Column = "arrow", KeyIndex = 119, ProfileKeyName = "ARR_DW" },
            new() { Code = "right", Label = "→", Column = "arrow", KeyIndex = 120, ProfileKeyName = "ARR_R" },
        ],
    ];

    // Flat row-major iteration order — useful for Shift-click range selection
    // (anchor → clicked, take everything in between).
    public static IReadOnlyList<LayoutKey> A75ProFlat { get; } =
        A75Pro.SelectMany(r => r).ToArray();

    public static LayoutKey? FindByCode(string code) =>
        A75ProFlat.FirstOrDefault(k => k.Code == code);

    public static LayoutKey? FindByKeyIndex(int idx) =>
        A75ProFlat.FirstOrDefault(k => k.KeyIndex == idx);

    public static LayoutKey? FindByProfileKeyName(string name) =>
        A75ProFlat.FirstOrDefault(k => k.ProfileKeyName == name);
}
