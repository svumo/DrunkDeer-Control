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
│   ├── MainWindow.xaml/cs          # Main UI
│   ├── App.xaml/cs                 # Application entry & DI setup
│   ├── Components/                 # Reusable UI components
│   ├── Hooks/                      # Windows event hooks
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
- [KeyboardManager.cs](e:\GitHub\DrunkDeerDriver\Driver\KeyboardManager.cs) - USB device detection (VID/PID filters)
- [KeyboardSpecs.cs](e:\GitHub\DrunkDeerDriver\Driver\KeyboardSpecs.cs) - Keyboard type identification
- [Packets.cs](e:\GitHub\DrunkDeerDriver\Driver\Packets.cs) - USB packet generation
- [HidDeviceExtensions.cs](e:\GitHub\DrunkDeerDriver\Driver\HidDeviceExtensions.cs) - HID read/write operations

### Profile Management
- [Profile.cs](e:\GitHub\DrunkDeerDriver\Driver\Profile.cs) - Profile data models (ProfileItem, KeyItem, RTP, etc.)
- [ProfileManager.cs](e:\GitHub\DrunkDeerDriver\WpfApp\Profile\ProfileManager.cs) - Profile CRUD, switching logic

### UI
- [MainWindow.xaml](e:\GitHub\DrunkDeerDriver\WpfApp\MainWindow.xaml) - Main window layout
- [App.xaml](e:\GitHub\DrunkDeerDriver\WpfApp\App.xaml) - Theme and global styles
- [TrayIcon.cs](e:\GitHub\DrunkDeerDriver\WpfApp\Components\TrayIcon.cs) - System tray integration

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

See [wobbly-stargazing-narwhal.md](C:\Users\skdes\.claude\plans\wobbly-stargazing-narwhal.md) for detailed implementation plan for A75 Pro modernization.

### Current Phase: Phase 0 - Baseline Testing

**Status**: Testing current application with A75 Pro
- ✅ Keyboard detected successfully
- ⚠️ Profile import failing (JSON schema mismatch)
- ⏳ Profile switching not yet tested
- ⏳ Process triggers not yet tested

---

## Quick Reference

### Add New Keyboard Support
1. Find VID/PID in Device Manager
2. Add to `DrunkDeerKeyboards` array in [KeyboardManager.cs:12](e:\GitHub\DrunkDeerDriver\Driver\KeyboardManager.cs#L12)
3. Test detection
4. If new keyboard type, update `GetKeyboardType()` in [KeyboardSpecs.cs:42](e:\GitHub\DrunkDeerDriver\Driver\KeyboardSpecs.cs#L42)

### Modify Profile Structure
1. Update models in [Profile.cs](e:\GitHub\DrunkDeerDriver\Driver\Profile.cs)
2. Add migration logic in ProfileManager if needed
3. Update packet generation in [Packets.cs](e:\GitHub\DrunkDeerDriver\Driver\Packets.cs)
4. Test import/export with both old and new formats

### Change UI Layout
1. Edit XAML in [MainWindow.xaml](e:\GitHub\DrunkDeerDriver\WpfApp\MainWindow.xaml)
2. Update code-behind if needed
3. Test data binding
4. Verify with different window sizes

---

## Contact & Support

- **Repository**: e:\GitHub\DrunkDeerDriver\
- **Issues**: Document bugs and feature requests in code comments or separate tracking
- **Testing**: Always test with actual DrunkDeer hardware before release

---

**Last Updated**: 2026-03-01
**Current Version**: In Development (A75 Pro Modernization)
