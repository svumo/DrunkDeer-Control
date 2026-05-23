# Firmware-flash mechanism research — DrunkDeer Updater V2.3.4

Research notes from inspecting `DrunkdeerUpdater.exe` + `UsbHid_v1.2.7.dll`
+ Ghidra headless decompilation, 2026-05-23.

**Status: insufficient for safe implementation.** Findings here are
useful background but a USB packet capture during a real flash is the
next step before any wire-protocol code can be written with confidence.

---

## Why we care

A75 Pro firmware ≥ 0x11 self-reports `oldOpenHighPrecision=true`, putting
the official tool into the OldHighPrec dialect that caps AP at 2.0 mm.
User testing 2026-05-22 confirmed that downgrading firmware to a version
that reports Legacy dialect (`oldOpenHighPrecision=false`) gives full
3.3 mm AP — with feelable difference at the higher values.

If we can drive the firmware-flash protocol ourselves, our app could
offer an in-place "switch to Legacy-dialect firmware" toggle, restoring
the 3.3 mm AP range without making users run a separate updater.

---

## What we've definitively established

### Updater architecture
- **DrunkdeerUpdater.exe**: GUI wrapper (MFC + the `AVC*` proprietary
  Chinese widget framework). Reads `update_config.ini` to pick the
  per-keyboard firmware blob.
- **UsbHid_v1.2.7.dll** (file string: `Ry_Online_Update_Dll_ouput_v1.2.7_UsbHid_lib_unicode`):
  the actual flash engine. Four public exports:
  - `userSetCfgFilePath` — point at the `.ini`
  - `chipGoToBootFirmware` — reboot keyboard into bootloader mode
  - `chipAppUpdateFirmware` — flash + verify a firmware blob
  - `chipCloseFirmware` — cleanup
- The DLL is a **third-party MCU vendor's HID-update reference SDK** —
  the `chip*` prefix and "Online_Update_tool" branding suggest a
  Chinese MCU vendor's stock library. Not publicly indexed; no
  documentation findable via web search.

### USB device topology
From `A75_Pro_ANSI_WIN/update_config.ini`:

| Mode | VID | PID | Interface |
|---|---|---|---|
| App (normal use) | 0x352D | 0x2383 | MI_01, Col03, report_id=4 |
| Bootloader | 0x352D | 0x1101 | (re-enumerates as separate device) |

The keyboard physically detaches and re-enumerates with a different
PID when entering bootloader. Sequence:
1. Send "enter boot" command to PID 0x2383
2. Wait `add_time = 1000ms` for stable enumeration
3. Open device at PID 0x1101
4. Stream the encrypted firmware blob
5. Device reboots → re-appears at PID 0x2383

### Firmware blob format
- File: `usb_hid_app_v1.0.0_<HASH>.enc`, 118816 bytes for the
  A75 family, 134232 for A75 Ultra/Master (which also have a
  separate `DualBankBoot.bin`)
- **Encrypted** (`encryption_en=1` in update_config.ini)
- The keyboard's bootloader verifies the encryption + signature
  itself — we cannot modify the blob, only flash it as-is
- Different per (model, version) — V2.3.1 ships A75 Pro fw 0x0008
  (the Legacy-dialect target), V2.3.4 ships 0x0017 (current
  OldHighPrec)

### Win32 imports used by the DLL
From the import table:
- `kernel32.dll`: `CreateFileW`, `WriteFile`, `ReadFile`, `Sleep`,
  `CloseHandle`
- `hid.dll`: `HidD_GetHidGuid` (only one HidD_* — interesting that
  none of HidD_SetOutputReport / HidD_GetInputReport are imported)
- `setupapi.dll`: device enumeration helpers (not in our extracted
  imports — present though)

The single `HidD_GetHidGuid` import means the DLL enumerates HID
devices by class GUID, then **uses raw CreateFile + WriteFile/ReadFile
on the HID device node** rather than going through the HidD_*
Set/GetInput/OutputReport API. Same approach our app already uses
via HidSharpCore. So no special driver shenanigans on the host side.

---

## What we found in the decompilation

### `chipGoToBootFirmware`
Bulk of the function is MFC `CString` reading from `update_config.ini`
for `bootdev_info`, `appdev_info`, `appdev_type`, `appdev_report_id`.
Then calls `FUN_10004110`, the actual worker.

### `FUN_10004110` (boot-mode trigger)
At [Tools/ghidra-out.txt:1287-1294]:

```c
cVar6 = FUN_10006480();                       // reads PRODUCT type
local_267 = 0;
local_266 = 0x40000;                          // 4 bytes
local_261 = 0x74656700;                       // \0 'g' 'e' 't' (LE)
local_25d[0] = 0x76;                          // 'v'
local_268 = (-(cVar6 != '\0') & 0xd0U) + 0x30; // 0x30 or 0xE0
local_262 = DAT_101d28e8 << 4;                // shifted flag byte
cVar6 = FUN_100030d0();                       // SEND
```

Followed by a response-verification loop ([Tools/ghidra-out.txt:1302]):

```c
if (((local_54[0] | 0x30) == 0x30) &&        // response byte 0 has 0x30 OR'd in
    ((short)local_54._4_4_ == 0x10 ||         // response[4..5] == 0x10
     (short)local_54._4_4_ == 0x20)) {        //   or 0x20
  // accept with size-threshold checks at 0x123 / 0x124
}
```

**What's identifiable but UNRELIABLE** without seeing the actual
assembly addresses:
- A boot-trigger packet is built with bytes including `\0 g e t v`
  — possibly "getv" repurposed as a magic key
- A type byte (0x30 family) and a length-like field (0x40000) are
  involved
- Responses with byte[0]&0x30=0x30 and bytes[4..5]∈{0x10, 0x20} are
  accepted

**Why this isn't safe to ship code from**: MFC stack-layout decompiler
output muddles the actual buffer offsets. The variables `local_268`,
`local_267`, `local_266` etc. are NOT necessarily adjacent in the
order they appear in source — they're at independent stack offsets.
Building a packet from this guesswork could send arbitrary garbage
that the keyboard might interpret as a different command, or worse,
trigger an unrecoverable bootloader state.

### `chipAppUpdateFirmware` + `chipCloseFirmware`
Similar MFC config-reading boilerplate. The actual flash loop is
behind multiple layers of wrapper functions (the kernel32
`WriteFile` import isn't called directly in any of the extracted
top-level functions — it's wrapped). Tracing further would require
deeper Ghidra interaction or disassembly tooling.

---

## What we need before implementation

### Option A: USB packet capture (lowest risk, fastest)
Install [Wireshark](https://www.wireshark.org/) + [USBPcap](https://desowin.org/usbpcap/),
record one real flash with the official updater, decode the
captured `.pcapng`. Gives us:
- Exact bytes of the "enter boot mode" packet
- Exact handshake on PID 0x1101
- Chunk size, ACK pattern, terminator
- Total flash duration, retry behavior, error states

~30 min setup, one capture session, then we have everything.

### Option B: Deeper Ghidra dive
Interactive Ghidra GUI session to trace through the wrapper
functions to find the actual `WriteFile` calls and their buffer
contents. Multi-hour. Still needs USB capture for verification.

### Option C: Risk it
Implement based on guesswork from current findings. **DO NOT DO
THIS.** Bricking an A75 Pro is a real outcome — the bootloader
verifies the firmware blob but the "enter boot mode" command
itself, if malformed, could leave the keyboard in an undefined
state where the bootloader is active but won't talk to any host.

---

## Recommendation

Either:

1. **USB capture next** (Option A) → continue this investigation in
   a follow-up session once a capture is available.
2. **Park indefinitely** → ship the dialect-aware UI we have
   (which gives users on Legacy-dialect firmware the full 3.3 mm
   range), document that A75 Pro 0x17+ users need to downgrade
   via the official updater for 3.3 mm AP, and revisit if/when
   we get a clean capture or DrunkDeer publishes the protocol.

Implementation should not proceed without either a USB capture or
a Ghidra-traced + assembly-verified packet specification. The
risk-reward on "brick your A75 Pro for a 1.3 mm AP range
extension" is poor without certainty.
