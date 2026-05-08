# DrunkDeer Driver Development Protocol

## Project Overview
C# WPF desktop application for managing DrunkDeer mechanical keyboards (A75 Pro, G65, etc.) with custom profile support, USB HID communication, and system tray integration.

---

## Core Principles

1. **Hardware First**: All changes must preserve keyboard communication integrity. Never break USB HID protocol.
2. **User Data Protection**: Profile files are sacred. Never corrupt or lose user's profiles.
3. **Backward Compatibility**: Support both old (G65) and new (A75 Pro) keyboards when possible.
4. **Clean UI/UX**: Modern, intuitive interface that doesn't overwhelm users.

---

## Project Structure

```
DrunkDeerDriver/
├── Driver/                          # Core USB HID communication library
│   ├── KeyboardManager.cs          # Device detection & management
│   ├── KeyboardSpecs.cs            # Firmware version & keyboard type detection
│   ├── Packets.cs                  # USB packet construction
│   ├── Profile.cs                  # Profile data models
│   └── HidDeviceExtensions.cs      # HID communication helpers
├── WpfApp/                          # WPF Desktop Application
│   ├── MainWindow.xaml/cs          # Main UI (sidebar + detail panel dashboard)
│   ├── App.xaml/cs                 # Application entry & DI setup
│   ├── Themes/
│   │   └── CustomStyles.xaml       # Dashboard card styles, sidebar styles
│   ├── Components/
│   │   ├── Converters.cs           # Value converters (Null/Bool to Visibility, etc.)
│   │   ├── ComparerConverter.cs    # Equality comparison converter
│   │   ├── ProcessPathToImageConverter.cs  # Process icon extraction
│   │   ├── ProcessSelector.xaml/cs # Process trigger selection dialog
│   │   └── TrayIcon.cs            # System tray integration
│   ├── Hooks/                      # Windows event hooks
│   ├── Extensions/                 # Process & collection extensions
│   └── Profile/ProfileManager.cs   # Profile CRUD operations
```

---

## Development Workflow

### For Hardware/Protocol Changes
1. **Research First**: Review existing packet structures and keyboard specs
2. **Test Non-Destructively**: Always test new packet formats without overwriting working profiles
3. **Log Everything**: Add detailed logging for USB communication during development
4. **Verify on Hardware**: Test on actual keyboard before committing

### For UI Changes
1. **Material Design**: Follow Material Design 3 guidelines for WPF
2. **Data Binding**: Use MVVM pattern with INotifyPropertyChanged
3. **Responsive**: Test window resizing and different DPI settings
4. **Dark Theme**: Maintain dark theme consistency

### For Profile Changes
1. **Schema Validation**: Verify JSON structure matches current format
2. **Migration Strategy**: If changing format, provide migration for old profiles
3. **Backup**: Always backup before modifying profile files
4. **Error Handling**: Gracefully handle corrupted or malformed profiles

---

## Critical Files Reference

### Hardware Communication
- [KeyboardManager.cs](Driver/KeyboardManager.cs) - USB device detection (VID/PID filters)
- [KeyboardSpecs.cs](Driver/KeyboardSpecs.cs) - Keyboard type identification
- [Packets.cs](Driver/Packets.cs) - USB packet generation
- [HidDeviceExtensions.cs](Driver/HidDeviceExtensions.cs) - HID read/write operations

### Profile Management
- [Profile.cs](Driver/Profile.cs) - Profile data models (ProfileItem, KeyItem, RTP, etc.)
- [ProfileManager.cs](WpfApp/Profile/ProfileManager.cs) - Profile CRUD, switching logic

### UI
- [MainWindow.xaml](WpfApp/MainWindow.xaml) - Dashboard layout (header, sidebar, detail cards, status bar)
- [App.xaml](WpfApp/App.xaml) - Theme and global styles
- [CustomStyles.xaml](WpfApp/Themes/CustomStyles.xaml) - Card styles, sidebar styles, section headers
- [Converters.cs](WpfApp/Components/Converters.cs) - Value converters for data binding
- [ProcessSelector.xaml](WpfApp/Components/ProcessSelector.xaml) - Process trigger selection dialog
- [TrayIcon.cs](WpfApp/Components/TrayIcon.cs) - System tray integration

---

## Design System (REQUIRED reading before shipping UI)

**Before adding or changing any user-facing UI, read [design-system/README.md](design-system/README.md) and [design-system/SKILL.md](design-system/SKILL.md). New components must use existing tokens; do not introduce ad-hoc colors, font sizes, or radii.**

### Source of truth
- [design-system/README.md](design-system/README.md) — color, type, spacing, motion, copy voice, and rules ("no gradients on surfaces", "no emoji as icons", 1.45 line-height for body, etc.).
- [design-system/colors_and_type.css](design-system/colors_and_type.css) — canonical token names (`--dd-accent`, `--dd-fg-1`, `--dd-surface-2`, …). WPF brushes in [Themes/CustomStyles.xaml](WpfApp/Themes/CustomStyles.xaml) map 1:1 to these tokens via the `Dd*` prefix (e.g. `DdAccent` ↔ `--dd-accent`). The full mapping lives at [design-system/MAPPING.md](design-system/MAPPING.md).
- [design-system/ui_kits/control_app/](design-system/ui_kits/control_app/) — interactive JSX reference. Includes the current shell (TitleBar with green connection pill, Sidebar with green active-profile dot, tabs with accent underline) **plus** the new full-keyboard Performance view (mode-toggle cards, keyboard center, preset bar) and the Set-Action-Point drawer that slides in from the right.

### Rules
- **Never** put raw hex colors, raw `FontSize` numbers, or raw `CornerRadius` values in XAML. Reference a brush key or named resource defined in `CustomStyles.xaml`.
- New tokens land in `colors_and_type.css` first, then propagate to `CustomStyles.xaml` as a `Dd*` brush. The CSS file stays the single source of truth.
- The `Dd*` brush prefix marks design-system-aligned brushes. Legacy names (`CardBg`, `BorderThin`, `AccentSoft`, …) are being removed — see commit history of `feature/design-system-migration`.

---

## Testing Checklist

Before committing changes, verify:

- [ ] **Keyboard Detection**: App detects all supported DrunkDeer keyboards
- [ ] **Profile Import**: Can import profiles from DrunkDeer web driver
- [ ] **Profile Export**: Profiles save correctly to JSON
- [ ] **Profile Switching**: All three methods work (hotkey, tray, process triggers)
- [ ] **Packet Communication**: USB packets send successfully without errors
- [ ] **Settings Persist**: Startup toggle, last used profile, etc. save correctly
- [ ] **UI Rendering**: No visual glitches, responsive layout, dark theme consistent
- [ ] **Error Handling**: Graceful failure with user-friendly messages
- [ ] **Design System**: UI changes reviewed against [design-system/README.md](design-system/README.md) (tokens used, no ad-hoc values)

---

## Known Issues & Limitations

### Hardware Support
- **Supported PIDs**: 0x2383, 0x2386, 0x2382, 0x2384, 0x024f, 0x2391, 0x2a08 (A75 Pro)
- **Key Count**: Hardcoded to 126 keys (maximum protocol supports)
- **Firmware**: Tested on v0.48 (G65) and v0.08-0.09 (A75 Pro)

### Profile Format
- Profiles exported from DrunkDeer web driver may have schema differences
- RTP (Rapid Trigger Plus) settings are optional
- Remap profiles are separate JSON files that link to main profiles

### Windows Compatibility
- Requires .NET 8.0 Runtime
- Startup shortcut uses Windows Startup folder
- HID communication requires no admin privileges (filters to vendor-defined usage page)

---

## Debugging Tips

### Keyboard Not Detected
1. Check Device Manager for VID_352D devices
2. Verify PID is in DrunkDeerKeyboards array
3. Check MaxOutputReportLength and MaxInputReportLength >= 64
4. Look for "HID-compliant vendor-defined device" with UsagePage 0xFF00

### Profile Won't Import
1. Validate JSON structure against Profile.cs models
2. Check for required vs optional properties
3. Look for schema differences between G65 and A75 Pro exports
4. Add try-catch with detailed error logging

### Profile Not Applying
1. Verify keyboard is connected (KeyboardManager.IsConnected())
2. Check packet generation in BuildPackets()
3. Log packet send failures
4. Verify actuation point ranges (0.2-3.8mm)

### UI Issues
1. Check XAML binding errors in Output window
2. Verify DataContext is set correctly
3. Test with different Windows scaling (100%, 125%, 150%)
4. Check MaterialDesign theme resources are loaded

---

## Code Style

### C# Conventions
- Use modern C# features (records, pattern matching, null-coalescing)
- Prefer expression-bodied members for simple properties/methods
- Use `var` for obvious types, explicit types for clarity
- Follow Microsoft naming conventions (PascalCase for public, camelCase for private)

### XAML Conventions
- Use data binding over code-behind when possible
- Name controls only when accessed in code-behind
- Use MaterialDesign styles and components
- Keep XAML clean with proper indentation

### Error Handling
- Use specific exceptions (InvalidOperationException, ArgumentException, etc.)
- Catch specific exceptions, not generic Exception
- Provide user-friendly error messages
- Log stack traces for debugging

---

## Build & Run

### Requirements
- Visual Studio 2022 or later
- .NET 8.0 SDK
- Windows 10/11

### Build Commands
```bash
# Open solution
start DrunkDeerDriver.sln

# Or build from command line
dotnet build DrunkDeerDriver.sln

# Run application
dotnet run --project WpfApp/WpfApp.csproj

# Run with console logging
dotnet run --project WpfApp/WpfApp.csproj -- --console

# Run minimized to tray
dotnet run --project WpfApp/WpfApp.csproj -- --start-minimized
```

### NuGet Packages
- **HidSharpCore 1.2.1.1**: USB HID device communication
- **MaterialDesignThemes.Wpf 5.1.0**: Modern UI components
- **DK.WshRuntime 4.1.3**: Windows shortcut creation
- **Microsoft.Extensions.DependencyInjection**: DI container

---

## Implementation Plan

### Current Phase: Phase 1 - GUI Redesign (Complete, Needs Testing)

**Status**: Dashboard UI redesign implemented, awaiting Windows testing
- Replaced flat DataGrid with modern sidebar + detail panel dashboard layout
- Header bar with keyboard status, status bar with firmware info and hotkey hint
- Profile list in sidebar with active indicators
- Detail panel with cards: Profile Overview, Actuation Settings, Key Remapping, Automation
- ProcessSelector redesigned with ListBox cards and MaterialDesign icons
- Custom styles in `Themes/CustomStyles.xaml`, new converters in `Components/Converters.cs`
- All existing features preserved (hotkeys, tray, process triggers, quick switch)

**Next**: Build and test on Windows machine to verify rendering and functionality

---

## Quick Reference

### Add New Keyboard Support
1. Find VID/PID in Device Manager
2. Add to `DrunkDeerKeyboards` array in [KeyboardManager.cs](Driver/KeyboardManager.cs)
3. Test detection
4. If new keyboard type, update `GetKeyboardType()` in [KeyboardSpecs.cs](Driver/KeyboardSpecs.cs)

### Modify Profile Structure
1. Update models in [Profile.cs](Driver/Profile.cs)
2. Add migration logic in ProfileManager if needed
3. Update packet generation in [Packets.cs](Driver/Packets.cs)
4. Test import/export with both old and new formats

### Change UI Layout
1. Edit XAML in [MainWindow.xaml](WpfApp/MainWindow.xaml) - dashboard layout structure
2. Update code-behind in [MainWindow.xaml.cs](WpfApp/MainWindow.xaml.cs) - event handlers, SelectedProfile property
3. Add/modify styles in [CustomStyles.xaml](WpfApp/Themes/CustomStyles.xaml)
4. Add converters in [Converters.cs](WpfApp/Components/Converters.cs) if needed
5. Test data binding and verify with different window sizes

### Cut a Release
The in-app update notifier (Options overlay) compares
`Assembly.Version` to the latest GitHub release tag, so the csproj version
**must** be bumped in lockstep with the git tag — otherwise users who
already have the new build will still see "Update available."

1. Bump `<Version>`, `<FileVersion>`, `<AssemblyVersion>` in
   [WpfApp/WpfApp.csproj](WpfApp/WpfApp.csproj) (e.g. `1.1.0` → `1.2.0`).
2. Commit + push the bump on `main`.
3. `git tag vX.Y.Z && git push origin vX.Y.Z`.
4. `dotnet publish WpfApp/WpfApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`.
5. Copy/rename the published exe to `DrunkDeer-Control.exe` (hyphen) — the
   release asset filename is what the in-app "Download" button hits via
   `releases/latest/download/DrunkDeer-Control.exe`. Don't change it.
6. `gh release create vX.Y.Z <path-to-exe> --title "..." --notes "..."`.

Update logic lives in [WpfApp/UpdateChecker.cs](WpfApp/UpdateChecker.cs)
and is wired in [MainWindow.xaml.cs](WpfApp/MainWindow.xaml.cs) inside
`OnSourceInitialized`.

#### Auto-update flow (v1.3.0+)

Clicking **Install** in the banner runs the swap entirely in-process —
no helper script:

1. [AutoUpdater.cs](WpfApp/AutoUpdater.cs) streams the new exe to
   `%LocalAppData%\DrunkDeer Control\update\staged.exe`.
2. Probes write access in the running exe's directory; falls back to
   the browser flow if it's a protected location (Program Files etc.).
3. `File.Move(currentExe, currentExe + ".bak")` — Windows allows
   renaming a running .exe within the same directory because the file
   handle is bound to the inode, not the path.
4. `File.Move(staged, currentExe)` — new build occupies the original
   path. On failure here, rolls back `.bak → currentExe`.
5. Launches `currentExe` (now the new version) and shuts the old
   process down. The single-instance mutex from v1.0.5 handles the
   brief overlap.
6. Next launch: `OnSourceInitialized` calls
   `AutoUpdater.CleanupBakIfPresent()` which deletes the leftover
   `.bak`. The running exe couldn't delete itself, so this is the
   only chance.

**If you see a stray `<exe>.bak` next to the running exe** — the most
recent install completed but cleanup hasn't run yet. Restart the app
and it'll be removed.

#### Canonical install + redirect (v1.4.0+)

To prevent the "I auto-updated my Downloads copy then opened an older
copy from somewhere else and ended up on a stale version" foot-gun,
v1.4 introduces a single canonical install location:

- **Canonical exe**: `%LocalAppData%\DrunkDeer Control\bin\DrunkDeer-Control.exe`
- **Registry pointer**: `HKCU\Software\DrunkDeer Control\{InstalledExePath, InstalledVersion}`

Logic on every launch
([InstallationManager.HandleLaunch](WpfApp/InstallationManager.cs)):

1. Running from canonical → stamp registry, continue.
2. Running from elsewhere, canonical exists, my version ≤ canonical →
   `InstallDialog.ShowOpenInstalled` → "Open installed" by default
   (3-second autoselect).
3. Running from elsewhere, canonical exists, my version > canonical →
   silently auto-upgrade canonical (move self over canonical, stamp
   registry, exit, launch canonical).
4. Running from elsewhere, no canonical → `InstallDialog.ShowFirstLaunch`
   → "Install" or "Just run once".

Profiles + settings live at `%LocalAppData%\DrunkDeer Control\` (not
under `bin\`), so they're shared between any install/portable copies
and aren't moved by canonical install.

---

## Contact & Support

- **Issues**: Document bugs and feature requests in GitHub Issues
- **Testing**: Always test with actual DrunkDeer hardware before release

---

**Last Updated**: 2026-05-06 (Update notifier + ProfileItem record-equality fix)
**Current Version**: In Development (A75 Pro Modernization)
