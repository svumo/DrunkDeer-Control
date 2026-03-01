#### Note: !! Tested on Windows 10/11 with DrunkDeer G65 (firmware v0.48) and A75 Pro (firmware v0.09). Use at your own risk, I'm not responsible for what happens to your keyboard.

### DrunkDeer Control
**Enhanced profile manager for DrunkDeer mechanical keyboards**

This program allows for easy switching between profiles for DrunkDeer keyboards (G65, A75 Pro, and more). It supports importing profiles exported from the DrunkDeer web driver with full compatibility for modern keyboards.
Minimizing the program puts it in system tray. Exiting the program closes it. The startup shortcut, created by checking Start on windows startup, points to the location of the exe so if the exe is moved you will need to unselect and select the start on windows startup again.

This App allows you to switch between the profiles in the following ways:
- *Right click the deer icon in the system tray and select one of the profiles there. The profile with the deer icon is the currently active one. Hover the deer icon in the tray to show the currently active one.
- *Use Ctrl + Alt + Enter to cycle through the profiles that are marked as quick switch.
- Select processes that will trigger a profile change, per profile. When 1 of these processes takes the foreground in windows the app will switch to the first associated profile.

Note: The above marked with * do work.
However, since the app will always push the (marked as) default profile when a process that is not associated with any profile takes the foreground, it will seem like it does not work.
If you wish to specifically use those 2 methods I suggest not selecting any profile as default.

### Changelog
- **v1.0** - DrunkDeer Control rebrand
  - Added A75 Pro support (PID 0x2383, 0x2a08)
  - Fixed .NET 8 compatibility (registry-based startup)
  - Added profile activation button and delete confirmation
  - Improved quick switch with helpful messages
  - Fixed tray icon double-click restore
  - Modernized JSON import (compatible with latest web driver exports)
- v0.2 - Added support for release double trigger, last win and key remapping
- v0.1 - Supports profiles and rapid trigger but not key remapping

### Supported Keyboards
- **DrunkDeer A75 Pro** (firmware v0.08-0.09) - Fully tested
- **DrunkDeer G65** (firmware v0.48) - Original support
- Other DrunkDeer keyboards with VID 0x352D should work

### System Requirements
- Windows 10/11 (64-bit)
- .NET 8.0 Runtime (included in single-file exe)
- No admin rights required

As I understood it the keyboard does not have internal storage to store more profiles other than the active one.
Therefore this App writes the default profile whenever it is started and starts tracking the active profile from there on.
If you change the active profile through the web driver this App will not know about it.

**If you know why windows defender gives a false positive for this App feel free to open an Issue for it.**

### Screenshots
**Main window**\
![Main window](https://i.imgur.com/cFSQTs8.png)

**Process selection**\
![Process selection](https://i.imgur.com/8Rbb4gX.png)

**Tray menu**\
![Tray menu](https://i.imgur.com/oyuNZyR.png)