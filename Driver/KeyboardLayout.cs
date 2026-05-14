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
// The KeyName → KeyIndex mapping was originally derived from a Profile.json
// dump and the JSX reference; it is now verified against the authoritative
// 19-model layout catalog in Driver/KeyboardModels.cs (extracted from the
// official DrunkDeer driver's JS bundle and cross-checked against a real
// A75 Ultra Profile1.json export — 91/91 non-empty slots match).
//
// A75 Pro has 91 named firmware slots; this visual layout currently renders
// the 82 that map to physical caps on the standard ANSI 75% body. The
// extra 9 (KP9 / KP5 / KP6 / KP2 / KP3 / KP_DEL / EUR_K45 / etc.) are
// firmware-reserved or Fn-layer and stay out of the visible rendering.
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
            new() { Code = "del",  Label = "Del",  KeyIndex = 14, ProfileKeyName = "KP7" },
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
            // A75 Pro variant B: Home at slot 36 (slot 35 is a firmware gap).
            // Verified against `g(36,252,"Home",...,74)` in dd-index.js.
            new() { Code = "home",     Label = "Home", KeyIndex = 36, ProfileKeyName = "KP4", Column = "nav" },
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
            // Variant B: PgUp at slot 57 (slot 56 is a firmware gap).
            new() { Code = "pgup",     Label = "PgUp", KeyIndex = 57, ProfileKeyName = "KP1", Column = "nav" },
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
            new() { Code = "pgdn",  Label = "PgDn",  KeyIndex = 78, ProfileKeyName = "KP0", Column = "nav" },
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
            new() { Code = "end",    Label = "End", Column = "nav", KeyIndex = 100, ProfileKeyName = "NUMS" },
        ],
        // Row 5 — LCtrl, LWin, LAlt, Spacebar, RAlt, Fn, Menu, arrows
        [
            new() { Code = "lctrl", Label = "Ctrl", Width = 1.25, Type = "mod", KeyIndex = 105, ProfileKeyName = "CTRL_L" },
            new() { Code = "lwin",  Label = "Win",  Width = 1.25, Type = "mod", KeyIndex = 106, ProfileKeyName = "WIN_L" },
            new() { Code = "lalt",  Label = "Alt",  Width = 1.25, Type = "mod", KeyIndex = 107, ProfileKeyName = "ALT_L" },
            new() { Code = "space", Label = "Spacebar", Width = 6.25, KeyIndex = 111, ProfileKeyName = "SPACE" },
            new() { Code = "ralt",  Label = "Alt",  Type = "mod", KeyIndex = 115, ProfileKeyName = "ALT_R" },
            new() { Code = "fn",    Label = "Fn",   Type = "mod", KeyIndex = 116, ProfileKeyName = "FN1" },
            // Slot 117 is Fn2 on the user's A75 Pro firmware (0x0009 ANSI),
            // not "Menu" / "APP" as the original `ddeerA75ProProfile` slot
            // map suggested. The official JS bundle has two variants of the
            // A75 Pro layout: variant A puts APP at 117 with arrows at
            // 118/119/120, variant B puts Fn2 at 117 with arrows at
            // 119/120/121. Hardware testing on factory firmware 0x0009
            // confirmed variant B — see commit history for the down-arrow
            // regression that surfaced this.
            new() { Code = "fn2",   Label = "Fn",   Type = "mod", KeyIndex = 117, ProfileKeyName = "FN2" },
            // Slot 118 is a firmware-reserved gap on variant B (no physical
            // key). Skipping it in the visual layout means our remap stream
            // writes keyCode=0 there, which is exactly what the JS bundle
            // does for variant B (`new g(118, 0, "", "", 0)`).
            new() { Code = "left",  Label = "←", Column = "arrow", KeyIndex = 119, ProfileKeyName = "ARR_L" },
            new() { Code = "down",  Label = "↓", Column = "arrow", KeyIndex = 120, ProfileKeyName = "ARR_DW" },
            new() { Code = "right", Label = "→", Column = "arrow", KeyIndex = 121, ProfileKeyName = "ARR_R" },
        ],
    ];

    // Flat row-major iteration order — useful for Shift-click range selection
    // (anchor → clicked, take everything in between).
    public static IReadOnlyList<LayoutKey> A75ProFlat { get; } =
        A75Pro.SelectMany(r => r).ToArray();

    // A75 Ultra — physically identical to A75 Pro (75% ANSI with rotary encoder
    // area). Only difference: the End cap maps to firmware slot 99 (NUMS)
    // instead of 100. Same 82 visible caps.
    public static IReadOnlyList<IReadOnlyList<LayoutKey>> A75Ultra { get; } =
    [
        // Row 0 — Esc, F-row, Del
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
            new() { Code = "del",  Label = "Del",  KeyIndex = 14, ProfileKeyName = "KP7" },
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
            new() { Code = "home",     Label = "Home", KeyIndex = 35, ProfileKeyName = "KP4", Column = "nav" },
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
            new() { Code = "pgup",     Label = "PgUp", KeyIndex = 56, ProfileKeyName = "KP1", Column = "nav" },
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
            new() { Code = "pgdn",  Label = "PgDn",  KeyIndex = 78, ProfileKeyName = "KP0", Column = "nav" },
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
            new() { Code = "end",    Label = "End", Column = "nav", KeyIndex = 99, ProfileKeyName = "NUMS" },
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

    public static IReadOnlyList<LayoutKey> A75UltraFlat { get; } =
        A75Ultra.SelectMany(r => r).ToArray();

    // G65 — 65% ANSI with arrow cluster. No F-row, no rotary encoder.
    // Row 0 begins with grave/digits; right-side nav column carries End/PgUp/PgDn.
    public static IReadOnlyList<IReadOnlyList<LayoutKey>> G65 { get; } =
    [
        // Row 0 — Grave, digits, Backspace, Delete
        [
            new() { Code = "grave", Label = "~", Sub = "`", KeyIndex = 21, ProfileKeyName = "ESC" },
            new() { Code = "1",     Label = "1", Sub = "!", KeyIndex = 22, ProfileKeyName = "1" },
            new() { Code = "2",     Label = "2", Sub = "@", KeyIndex = 23, ProfileKeyName = "2" },
            new() { Code = "3",     Label = "3", Sub = "#", KeyIndex = 24, ProfileKeyName = "3" },
            new() { Code = "4",     Label = "4", Sub = "$", KeyIndex = 25, ProfileKeyName = "4" },
            new() { Code = "5",     Label = "5", Sub = "%", KeyIndex = 26, ProfileKeyName = "5" },
            new() { Code = "6",     Label = "6", Sub = "^", KeyIndex = 27, ProfileKeyName = "6" },
            new() { Code = "7",     Label = "7", Sub = "&", KeyIndex = 28, ProfileKeyName = "7" },
            new() { Code = "8",     Label = "8", Sub = "*", KeyIndex = 29, ProfileKeyName = "8" },
            new() { Code = "9",     Label = "9", Sub = "(", KeyIndex = 30, ProfileKeyName = "9" },
            new() { Code = "0",     Label = "0", Sub = ")", KeyIndex = 31, ProfileKeyName = "0" },
            new() { Code = "minus", Label = "-", Sub = "_", KeyIndex = 32, ProfileKeyName = "MINUS" },
            new() { Code = "equal", Label = "=", Sub = "+", KeyIndex = 33, ProfileKeyName = "PLUS" },
            new() { Code = "bksp",  Label = "Backspace", Width = 2.0, KeyIndex = 34, ProfileKeyName = "BACK" },
            new() { Code = "del",   Label = "Del", KeyIndex = 35, ProfileKeyName = "DELETE", Column = "nav" },
        ],
        // Row 1 — Tab, top letter row, [ ] \, End
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
            new() { Code = "end",      Label = "End", KeyIndex = 56, ProfileKeyName = "END", Column = "nav" },
        ],
        // Row 2 — Caps, home letter row, ; ', Enter, PgUp
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
            new() { Code = "pgup",  Label = "PgUp", KeyIndex = 77, ProfileKeyName = "PAGEUP", Column = "nav" },
        ],
        // Row 3 — LShift, bottom letter row, RShift, Up, PgDn
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
            new() { Code = "rshift", Label = "Shift", Width = 1.75, Type = "mod", KeyIndex = 96, ProfileKeyName = "SHF_R" },
            new() { Code = "up",     Label = "↑", Column = "arrow", KeyIndex = 97, ProfileKeyName = "ARR_UP" },
            new() { Code = "pgdn",   Label = "PgDn", KeyIndex = 98, ProfileKeyName = "PAGEDW", Column = "nav" },
        ],
        // Row 4 — LCtrl, LWin, LAlt, Spacebar, RAlt, Fn, arrows
        [
            new() { Code = "lctrl", Label = "Ctrl", Width = 1.25, Type = "mod", KeyIndex = 105, ProfileKeyName = "CTRL_L" },
            new() { Code = "lwin",  Label = "Win",  Width = 1.25, Type = "mod", KeyIndex = 106, ProfileKeyName = "WIN_L" },
            new() { Code = "lalt",  Label = "Alt",  Width = 1.25, Type = "mod", KeyIndex = 107, ProfileKeyName = "ALT_L" },
            new() { Code = "space", Label = "Spacebar", Width = 6.25, KeyIndex = 111, ProfileKeyName = "SPACE" },
            new() { Code = "ralt",  Label = "Alt",  Type = "mod", KeyIndex = 114, ProfileKeyName = "ALT_R" },
            new() { Code = "fn",    Label = "Fn",   Type = "mod", KeyIndex = 115, ProfileKeyName = "FN1" },
            new() { Code = "left",  Label = "←", Column = "arrow", KeyIndex = 117, ProfileKeyName = "ARR_L" },
            new() { Code = "down",  Label = "↓", Column = "arrow", KeyIndex = 118, ProfileKeyName = "ARR_DW" },
            new() { Code = "right", Label = "→", Column = "arrow", KeyIndex = 119, ProfileKeyName = "ARR_R" },
        ],
    ];

    public static IReadOnlyList<LayoutKey> G65Flat { get; } =
        G65.SelectMany(r => r).ToArray();

    // G60 — 60% ANSI. No F-row, no arrow cluster, no nav column.
    // RShift is the wide 2.75u variant (replaces the entire nav slot).
    public static IReadOnlyList<IReadOnlyList<LayoutKey>> G60 { get; } =
    [
        // Row 0 — Grave, digits, Backspace
        [
            new() { Code = "grave", Label = "~", Sub = "`", KeyIndex = 21, ProfileKeyName = "ESC" },
            new() { Code = "1",     Label = "1", Sub = "!", KeyIndex = 22, ProfileKeyName = "1" },
            new() { Code = "2",     Label = "2", Sub = "@", KeyIndex = 23, ProfileKeyName = "2" },
            new() { Code = "3",     Label = "3", Sub = "#", KeyIndex = 24, ProfileKeyName = "3" },
            new() { Code = "4",     Label = "4", Sub = "$", KeyIndex = 25, ProfileKeyName = "4" },
            new() { Code = "5",     Label = "5", Sub = "%", KeyIndex = 26, ProfileKeyName = "5" },
            new() { Code = "6",     Label = "6", Sub = "^", KeyIndex = 27, ProfileKeyName = "6" },
            new() { Code = "7",     Label = "7", Sub = "&", KeyIndex = 28, ProfileKeyName = "7" },
            new() { Code = "8",     Label = "8", Sub = "*", KeyIndex = 29, ProfileKeyName = "8" },
            new() { Code = "9",     Label = "9", Sub = "(", KeyIndex = 30, ProfileKeyName = "9" },
            new() { Code = "0",     Label = "0", Sub = ")", KeyIndex = 31, ProfileKeyName = "0" },
            new() { Code = "minus", Label = "-", Sub = "_", KeyIndex = 32, ProfileKeyName = "MINUS" },
            new() { Code = "equal", Label = "=", Sub = "+", KeyIndex = 33, ProfileKeyName = "PLUS" },
            new() { Code = "bksp",  Label = "Backspace", Width = 2.0, KeyIndex = 34, ProfileKeyName = "BACK" },
        ],
        // Row 1 — Tab, top letter row, [ ] \
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
        ],
        // Row 2 — Caps, home letter row, ; ', Enter
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
        ],
        // Row 3 — LShift, bottom letter row, RShift (wide 2.75u, no arrow cluster)
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
            new() { Code = "rshift", Label = "Shift", Width = 2.75, Type = "mod", KeyIndex = 97, ProfileKeyName = "SHF_R" },
        ],
        // Row 4 — LCtrl, LWin, LAlt, Spacebar, RAlt, Fn, Fn2, RCtrl
        [
            new() { Code = "lctrl", Label = "Ctrl", Width = 1.25, Type = "mod", KeyIndex = 105, ProfileKeyName = "CTRL_L" },
            new() { Code = "lwin",  Label = "Win",  Width = 1.25, Type = "mod", KeyIndex = 106, ProfileKeyName = "WIN_L" },
            new() { Code = "lalt",  Label = "Alt",  Width = 1.25, Type = "mod", KeyIndex = 107, ProfileKeyName = "ALT_L" },
            new() { Code = "space", Label = "Spacebar", Width = 6.25, KeyIndex = 111, ProfileKeyName = "SPACE" },
            new() { Code = "ralt",  Label = "Alt",  Type = "mod", KeyIndex = 115, ProfileKeyName = "ALT_R" },
            new() { Code = "fn",    Label = "Fn",   Type = "mod", KeyIndex = 116, ProfileKeyName = "FN1" },
            new() { Code = "fn2",   Label = "Fn",   Type = "mod", KeyIndex = 117, ProfileKeyName = "FN2" },
            new() { Code = "rctrl", Label = "Ctrl", Type = "mod", KeyIndex = 118, ProfileKeyName = "CTRL_R" },
        ],
    ];

    public static IReadOnlyList<LayoutKey> G60Flat { get; } =
        G60.SelectMany(r => r).ToArray();

    // G75 — full-size 96% with F-row, right-edge nav column (Home/PgUp/PgDn/End),
    // and arrow keys. Row 0 has 16 keys including PrintScreen/Insert/Delete on
    // the right. EUR_K42 (slot 75) and EUR_K45 (slot 85) are ISO-only break
    // keys — not visible on the ANSI G75 body, so they're skipped here.
    public static IReadOnlyList<IReadOnlyList<LayoutKey>> G75 { get; } =
    [
        // Row 0 — Esc, F-row, PrintScreen, Insert, Delete
        [
            new() { Code = "esc",   Label = "Esc",   KeyIndex = 0,  ProfileKeyName = "ESC" },
            new() { Code = "f1",    Label = "F1",    KeyIndex = 1,  ProfileKeyName = "F1" },
            new() { Code = "f2",    Label = "F2",    KeyIndex = 2,  ProfileKeyName = "F2" },
            new() { Code = "f3",    Label = "F3",    KeyIndex = 3,  ProfileKeyName = "F3" },
            new() { Code = "f4",    Label = "F4",    KeyIndex = 4,  ProfileKeyName = "F4" },
            new() { Code = "f5",    Label = "F5",    KeyIndex = 5,  ProfileKeyName = "F5" },
            new() { Code = "f6",    Label = "F6",    KeyIndex = 6,  ProfileKeyName = "F6" },
            new() { Code = "f7",    Label = "F7",    KeyIndex = 7,  ProfileKeyName = "F7" },
            new() { Code = "f8",    Label = "F8",    KeyIndex = 8,  ProfileKeyName = "F8" },
            new() { Code = "f9",    Label = "F9",    KeyIndex = 9,  ProfileKeyName = "F9" },
            new() { Code = "f10",   Label = "F10",   KeyIndex = 10, ProfileKeyName = "F10" },
            new() { Code = "f11",   Label = "F11",   KeyIndex = 11, ProfileKeyName = "F11" },
            new() { Code = "f12",   Label = "F12",   KeyIndex = 12, ProfileKeyName = "F12" },
            new() { Code = "print", Label = "PrtSc", KeyIndex = 13, ProfileKeyName = "PRINT" },
            new() { Code = "ins",   Label = "Ins",   KeyIndex = 14, ProfileKeyName = "INSERT" },
            new() { Code = "del",   Label = "Del",   KeyIndex = 15, ProfileKeyName = "DELETE" },
        ],
        // Row 1 — Grave, digits, Backspace, Home
        [
            new() { Code = "grave", Label = "~", Sub = "`", KeyIndex = 21, ProfileKeyName = "SWUNG" },
            new() { Code = "1",     Label = "1", Sub = "!", KeyIndex = 22, ProfileKeyName = "1" },
            new() { Code = "2",     Label = "2", Sub = "@", KeyIndex = 23, ProfileKeyName = "2" },
            new() { Code = "3",     Label = "3", Sub = "#", KeyIndex = 24, ProfileKeyName = "3" },
            new() { Code = "4",     Label = "4", Sub = "$", KeyIndex = 25, ProfileKeyName = "4" },
            new() { Code = "5",     Label = "5", Sub = "%", KeyIndex = 26, ProfileKeyName = "5" },
            new() { Code = "6",     Label = "6", Sub = "^", KeyIndex = 27, ProfileKeyName = "6" },
            new() { Code = "7",     Label = "7", Sub = "&", KeyIndex = 28, ProfileKeyName = "7" },
            new() { Code = "8",     Label = "8", Sub = "*", KeyIndex = 29, ProfileKeyName = "8" },
            new() { Code = "9",     Label = "9", Sub = "(", KeyIndex = 30, ProfileKeyName = "9" },
            new() { Code = "0",     Label = "0", Sub = ")", KeyIndex = 31, ProfileKeyName = "0" },
            new() { Code = "minus", Label = "-", Sub = "_", KeyIndex = 32, ProfileKeyName = "MINUS" },
            new() { Code = "equal", Label = "=", Sub = "+", KeyIndex = 33, ProfileKeyName = "PLUS" },
            new() { Code = "bksp",  Label = "Backspace", Width = 2.0, KeyIndex = 34, ProfileKeyName = "BACK" },
            new() { Code = "home",  Label = "Home", KeyIndex = 36, ProfileKeyName = "HOME", Column = "nav" },
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
            new() { Code = "pgup",     Label = "PgUp", KeyIndex = 57, ProfileKeyName = "PAGEUP", Column = "nav" },
        ],
        // Row 3 — Caps, home letter row, ; ', Enter, PgDn. EUR_K42 (slot 75)
        // is the ISO Enter break; ANSI G75 doesn't carry it.
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
            new() { Code = "pgdn",  Label = "PgDn", KeyIndex = 78, ProfileKeyName = "PAGEDW", Column = "nav" },
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
            new() { Code = "rshift", Label = "Shift", Width = 1.75, Type = "mod", KeyIndex = 96, ProfileKeyName = "SHF_R" },
            new() { Code = "up",     Label = "↑", Column = "arrow", KeyIndex = 97, ProfileKeyName = "ARR_UP" },
            new() { Code = "end",    Label = "End", Column = "nav", KeyIndex = 99, ProfileKeyName = "END" },
        ],
        // Row 5 — LCtrl, LWin, LAlt, Spacebar, RAlt, Fn, Fn2, arrows
        [
            new() { Code = "lctrl", Label = "Ctrl", Width = 1.25, Type = "mod", KeyIndex = 105, ProfileKeyName = "CTRL_L" },
            new() { Code = "lwin",  Label = "Win",  Width = 1.25, Type = "mod", KeyIndex = 106, ProfileKeyName = "WIN_L" },
            new() { Code = "lalt",  Label = "Alt",  Width = 1.25, Type = "mod", KeyIndex = 107, ProfileKeyName = "ALT_L" },
            new() { Code = "space", Label = "Spacebar", Width = 6.25, KeyIndex = 111, ProfileKeyName = "SPACE" },
            new() { Code = "ralt",  Label = "Alt",  Type = "mod", KeyIndex = 114, ProfileKeyName = "ALT_R" },
            new() { Code = "fn",    Label = "Fn",   Type = "mod", KeyIndex = 115, ProfileKeyName = "FN1" },
            new() { Code = "fn2",   Label = "Fn",   Type = "mod", KeyIndex = 117, ProfileKeyName = "FN2" },
            new() { Code = "left",  Label = "←", Column = "arrow", KeyIndex = 118, ProfileKeyName = "ARR_L" },
            new() { Code = "down",  Label = "↓", Column = "arrow", KeyIndex = 119, ProfileKeyName = "ARR_DW" },
            new() { Code = "right", Label = "→", Column = "arrow", KeyIndex = 120, ProfileKeyName = "ARR_R" },
        ],
    ];

    public static IReadOnlyList<LayoutKey> G75Flat { get; } =
        G75.SelectMany(r => r).ToArray();

    // X60 — physically identical to A75 Ultra per KeyboardModels (the X60Layout
    // and A75UltraLayout arrays are byte-for-byte equal). End sits at slot 99
    // (NUMS), arrows in row 4/5, no rotary on this body.
    public static IReadOnlyList<IReadOnlyList<LayoutKey>> X60 { get; } =
    [
        // Row 0 — Esc, F-row, Del
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
            new() { Code = "del",  Label = "Del",  KeyIndex = 14, ProfileKeyName = "KP7" },
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
            new() { Code = "home",     Label = "Home", KeyIndex = 35, ProfileKeyName = "KP4", Column = "nav" },
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
            new() { Code = "pgup",     Label = "PgUp", KeyIndex = 56, ProfileKeyName = "KP1", Column = "nav" },
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
            new() { Code = "pgdn",  Label = "PgDn",  KeyIndex = 78, ProfileKeyName = "KP0", Column = "nav" },
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
            new() { Code = "end",    Label = "End", Column = "nav", KeyIndex = 99, ProfileKeyName = "NUMS" },
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

    public static IReadOnlyList<LayoutKey> X60Flat { get; } =
        X60.SelectMany(r => r).ToArray();

    // A75 ISO — A75 UK / FR / DE all share the same physical body (locale
    // variants differ only in keycap printing, which we don't render). Same
    // as A75 Pro plus an extra 1u EUR_K42 cap at slot 75, sitting between the
    // apostrophe and a shortened ISO Enter. End sits at slot 100 (NUMS), same
    // as A75 Pro.
    public static IReadOnlyList<IReadOnlyList<LayoutKey>> A75ISO { get; } =
    [
        // Row 0 — Esc, F-row, Del
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
            new() { Code = "del",  Label = "Del",  KeyIndex = 14, ProfileKeyName = "KP7" },
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
            new() { Code = "home",     Label = "Home", KeyIndex = 35, ProfileKeyName = "KP4", Column = "nav" },
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
            new() { Code = "pgup",     Label = "PgUp", KeyIndex = 56, ProfileKeyName = "KP1", Column = "nav" },
        ],
        // Row 3 — Caps, home letter row, ; ', EUR_K42 (ISO break), Enter, PgDn
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
            new() { Code = "iso",   Label = "#", Sub = "~",  KeyIndex = 75, ProfileKeyName = "EUR_K42" },
            new() { Code = "enter", Label = "Enter", Width = 1.25, KeyIndex = 76, ProfileKeyName = "RETURN" },
            new() { Code = "pgdn",  Label = "PgDn",  KeyIndex = 78, ProfileKeyName = "KP0", Column = "nav" },
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
            new() { Code = "end",    Label = "End", Column = "nav", KeyIndex = 100, ProfileKeyName = "NUMS" },
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

    public static IReadOnlyList<LayoutKey> A75ISOFlat { get; } =
        A75ISO.SelectMany(r => r).ToArray();

    public static LayoutKey? FindByCode(string code) =>
        A75ProFlat.FirstOrDefault(k => k.Code == code);

    public static LayoutKey? FindByKeyIndex(int idx) =>
        A75ProFlat.FirstOrDefault(k => k.KeyIndex == idx);

    public static LayoutKey? FindByProfileKeyName(string name) =>
        A75ProFlat.FirstOrDefault(k => k.ProfileKeyName == name);

    // Default USB HID Keyboard usage code for a LayoutKey.Code value. The JS
    // bundle's `sendRemapKeyData` iterates the in-memory keymap and emits an
    // entry whenever `k.keyCmd` is truthy — which is always, because every
    // slot is populated with its factory default (keyCmd=0xFC, keyCode=HID
    // usage). For partial remaps to commit on the firmware we have to mirror
    // that: the wire keymap must carry default entries for every slot, with
    // the user's overrides on top. Returns 0 for codes that aren't standard
    // HID keys (Fn, Fn2, Menu, ISO) — those are firmware-handled and we
    // leave their entry empty rather than risk a bogus override.
    public static byte DefaultHidUsage(string code) => code switch
    {
        "esc" => 0x29,
        "f1" => 0x3A, "f2" => 0x3B, "f3" => 0x3C, "f4" => 0x3D,
        "f5" => 0x3E, "f6" => 0x3F, "f7" => 0x40, "f8" => 0x41,
        "f9" => 0x42, "f10" => 0x43, "f11" => 0x44, "f12" => 0x45,
        "print" => 0x46,
        "del" => 0x4C, "ins" => 0x49,
        "home" => 0x4A, "end" => 0x4D,
        "pgup" => 0x4B, "pgdn" => 0x4E,
        "grave" => 0x35,
        "1" => 0x1E, "2" => 0x1F, "3" => 0x20, "4" => 0x21, "5" => 0x22,
        "6" => 0x23, "7" => 0x24, "8" => 0x25, "9" => 0x26, "0" => 0x27,
        "minus" => 0x2D, "equal" => 0x2E,
        "bksp" => 0x2A, "tab" => 0x2B, "enter" => 0x28,
        "lbracket" => 0x2F, "rbracket" => 0x30, "bslash" => 0x31,
        "semi" => 0x33, "quote" => 0x34,
        "comma" => 0x36, "period" => 0x37, "slash" => 0x38,
        "caps" => 0x39, "space" => 0x2C,
        // Modifier defaults — HID usage codes (0xE0..0xE7). With our wire
        // layout (keyType=0, keyCmd=0xFC, byte[3]=code), the firmware reads
        // byte[3] as a HID usage and matches modifier semantics from the
        // 0xE0-range usage IDs. The JS bundle uses small mask values (1,2,
        // 4,32,64) for these slots but it pairs them with a different
        // keyType where byte[2] is the modifier mask — we don't use that
        // path, so HID usages are correct for OUR encoding.
        "lshift" => 0xE1, "rshift" => 0xE5,
        "lctrl" => 0xE0, "rctrl" => 0xE4,
        "lalt" => 0xE2, "ralt" => 0xE6,
        "lwin" => 0xE3,
        "up" => 0x52, "down" => 0x51, "left" => 0x50, "right" => 0x4F,
        "a" => 0x04, "b" => 0x05, "c" => 0x06, "d" => 0x07, "e" => 0x08,
        "f" => 0x09, "g" => 0x0A, "h" => 0x0B, "i" => 0x0C, "j" => 0x0D,
        "k" => 0x0E, "l" => 0x0F, "m" => 0x10, "n" => 0x11, "o" => 0x12,
        "p" => 0x13, "q" => 0x14, "r" => 0x15, "s" => 0x16, "t" => 0x17,
        "u" => 0x18, "v" => 0x19, "w" => 0x1A, "x" => 0x1B, "y" => 0x1C,
        "z" => 0x1D,
        "iso" => 0x32,
        _ => 0, // fn / fn2 / menu / unmapped — firmware-handled
    };

    // Build a 126-slot keymap pre-populated with factory HID usage codes for
    // every slot present in `flat`. Slots not represented in `flat` (firmware-
    // reserved Fn-layer slots etc.) stay 0. Caller layers user overrides on
    // top of the returned array.
    public static byte[] BuildDefaultHidUsageMap(IReadOnlyList<LayoutKey> flat)
    {
        var map = new byte[126];
        foreach (var lk in flat)
        {
            if (lk.KeyIndex < 0 || lk.KeyIndex >= 126) continue;
            byte hid = DefaultHidUsage(lk.Code);
            if (hid != 0) map[lk.KeyIndex] = hid;
        }
        return map;
    }

    // Pick the visual layout for a given model. The 19-entry catalog in
    // Driver/KeyboardModels.cs has many models that share the same physical
    // body (A75 Pro / A75 / A75 Master use the same caps; G65 family is all
    // identical; G60 family is all identical) — collapse those down here.
    // Returns null when no visual layout has been hand-built yet for that
    // model; callers should fall back to A75 Pro.
    public static IReadOnlyList<IReadOnlyList<LayoutKey>>? VisualFor(KeyboardModel? model) =>
        model?.ProfilePrefix switch
        {
            "ddeerA75ProProfile"    => A75Pro,
            "ddeerA75Profile"       => A75Pro, // identical to A75 Pro per KeyboardModels
            "ddeerA75MasterProfile" => A75Ultra, // A75 Master matches Ultra on the End/NUMS slot
            "ddeerA75UltraProfile"  => A75Ultra,
            "ddeerA75UKProfile"
                or "ddeerA75FRProfile"
                or "ddeerA75DEProfile" => A75ISO,
            "ddeerG75Profile"
                or "ddeerG75JPProfile" => G75, // G75 JP shares the body; JP-specific caps not rendered
            "ddeerX60Profile"       => X60,
            "ddeerG65Profile"
                or "ddeerG65liteProfile"
                or "ddeerG65m1Profile"
                or "ddeerG65m2Profile"
                or "ddeerG65m3Profile" => G65,
            "ddeerG60Profile"
                or "ddeerG60m1Profile"
                or "ddeerG60m2Profile"
                or "ddeerG60m3Profile" => G60,
            _ => null,
        };

    public static IReadOnlyList<LayoutKey>? VisualFlatFor(KeyboardModel? model) =>
        model?.ProfilePrefix switch
        {
            "ddeerA75ProProfile"    => A75ProFlat,
            "ddeerA75Profile"       => A75ProFlat,
            "ddeerA75MasterProfile" => A75UltraFlat,
            "ddeerA75UltraProfile"  => A75UltraFlat,
            "ddeerA75UKProfile"
                or "ddeerA75FRProfile"
                or "ddeerA75DEProfile" => A75ISOFlat,
            "ddeerG75Profile"
                or "ddeerG75JPProfile" => G75Flat,
            "ddeerX60Profile"       => X60Flat,
            "ddeerG65Profile"
                or "ddeerG65liteProfile"
                or "ddeerG65m1Profile"
                or "ddeerG65m2Profile"
                or "ddeerG65m3Profile" => G65Flat,
            "ddeerG60Profile"
                or "ddeerG60m1Profile"
                or "ddeerG60m2Profile"
                or "ddeerG60m3Profile" => G60Flat,
            _ => null,
        };
}
