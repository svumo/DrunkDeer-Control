# DrunkDeer Control — Feature Implementation Tasks

## Status Legend
- [ ] Pending
- [x] Done
- [~] In progress

---

## Task List

- [x] **1. Fix help (?) tooltip rendering** — added `IsHitTestVisible="True"` and `Cursor="Help"` to all 6 PackIcons; bumped opacity 0.5→0.6
- [x] **2. Profile notes field** — added `Note` string to `ProfileItem` (Driver/Profile.cs); added Notes GlassCard with multi-line TextBox in detail panel; wired up `OnProfileNoteChanged` handler; note persists to JSON
- [x] **3. Active profile display** — title-bar pill now shows "Active: {name}"; Activate button renamed to "Activated" and turns green when viewing the currently active profile
- [x] **4. Rename UX overhaul** — pencil button (hover-visible) added to sidebar items; Rename added to right-click menu; context menu given rounded glass style; rename overlay (modal-within-window) added; old Rename card removed; right-click now correctly selects target item
- [x] **5. Help button in options popup** — added "Report an issue" button at bottom of options popup; opens https://github.com/svumo/DrunkDeer-Control/issues in browser
- [x] **6. Custom global keybind** — keybind badge in options popup is now clickable; click enters recording mode ("Press keys…"), any combo is captured and re-registered; `QuickSwitchKey`/`QuickSwitchModifiers` added to Settings and persisted to settings.json
- [x] **7. Per-profile direct-access keybind** — `DirectSwitchKey`/`DirectSwitchModifiers` added to `ProfileItem`; Direct Keybind row added to Automation card (clickable badge, records via window PreviewKeyDown, Escape clears); handlers registered/unregistered per profile on import, discovery, and close

---

## Session Log

- **Task 1 done** — Fixed tooltip rendering on all 6 (?) help icons in MainWindow.xaml: added `IsHitTestVisible="True"` and `Cursor="Help"` so hover events register correctly. Also updated Quick Switch tooltip text to not reference the hardcoded keybind.
- **Task 2 done** — Added `Note` string property to `ProfileItem` (Profile.cs). Added Notes GlassCard with multi-line TextBox below Automation card in detail panel. Wired `OnProfileNoteChanged` handler in MainWindow.xaml.cs. Notes save to profile JSON automatically.
- **Task 3 done** — Title-bar pill now reads "Active: {name}" (was just the name). Added `UpdateActivateButton()` which changes the Activate button to green "Activated" text when viewing the active profile, and back to "Activate" when viewing any other profile.
- **Task 4 done** — Sidebar items: added hover-visible pencil button (`BoolToOpacityConverter` via `IsMouseOver` RelativeSource binding). Context menu: added Rename item, styled with `GlassContextMenu`/`GlassMenuItem` styles (rounded, glass background). Rename modal overlay added to main content grid (semi-transparent backdrop, Enter/Escape keys supported). Old Rename GlassCard removed. `OnListBoxPreviewRightClick` ensures the correct item is selected on right-click. `BoolToOpacityConverter` added to Converters.cs.
- **Task 5 done** — Added GitHub Issues button at bottom of options popup (below keybinds divider). Click opens `https://github.com/svumo/DrunkDeer-Control/issues` in default browser via `Process.Start`.
- **Task 6 done** — Keybind badge now clickable (Cursor=Hand, ToolTip). Click → "Press keys…" recording mode captured via `PreviewKeyDown` on Window. Any key+modifier combo is accepted (Ctrl, Alt, Shift, none). Re-registers the hotkey immediately and persists via Settings. `QuickSwitchKey` and `QuickSwitchModifiers` properties added to Settings.cs. Settings injected into MainWindow constructor.
- **Task 7 done** — `DirectSwitchKey`/`DirectSwitchModifiers` added to `ProfileItem` (persist to JSON). Direct Keybind badge in Automation card: click to record, Escape to clear (sets to None). Separate `directHandlers` dictionary manages per-profile hotkeys. Handlers registered on profile discovery/import and unregistered on close/clear. Note in tooltip explains Fn key is hardware-only.
