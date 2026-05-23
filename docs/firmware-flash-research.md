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

---

# Update — USB capture decoded 2026-05-23

USB capture taken during a real `DrunkdeerUpdater.exe` flash of
A75 Pro from firmware 0x17 to firmware 0x17 (same version reflash —
safest test). 32,120 frames captured; pcap saved at
`tools/captures/firmware-flash-v2.3.4.pcapng` (gitignored, ~14 MB).

## Sequence of events

| Frame | Event |
|---|---|
| 26 | Pre-flash app mode appears (bus 2, device 8, PID 0x2383, MI_01) |
| 12817 | Host sends identity ping `04 a0 02 00 00 00...` (matches our `IDENTITY_PACKET`) |
| **14223** | **Host sends BOOT TRIGGER: `04 a5 01 00 00 00...`** |
| 14997 | URB_FUNCTION_ABORT_PIPE on endpoint 0x81 (device disappearing) |
| 16311 | URB_FUNCTION_ABORT_PIPE on endpoint 0x82 |
| 16348 | Boot-mode device appears (bus 2, device 9, PID 0x1101) |
| 17732 | First boot-mode command from host: `getv` (get version) |
| 17736 | Device responds: `version: ...` + `RY720300200b0401` identifier |
| 17769 | First firmware write chunk starts (opcode `0x31`) |
| 26165 | Device sends `0x32` status (chunks complete?) |
| 26167 | Host sends `0x33` finalize with 8-byte CRC/signature |
| 26175 | Device acks `0x33` (status 0x34 = OK) |
| 26176+ | Boot-mode device disappears |
| 26633 | Post-flash app mode appears (bus 2, device 10, PID 0x2383) |

## Boot trigger packet (app mode → device, endpoint 0x03 OUT)

```
04 a5 01 00 00 00 00 00 00 00 00 00 00 00 00 00 ... (64 bytes total, rest zero)
```

- Byte 0: `0x04` — HID report ID (same one our app already uses)
- Byte 1: `0xa5` — **boot-trigger opcode**
- Byte 2: `0x01` — sub-command / parameter
- Bytes 3+: zero-padded

Trivially simple to send via our existing `WritePacketNoAck` after
opening the keyboard HID handle. Device disappears within ~700 ms
and re-enumerates at PID 0x1101 in ~1.3 sec (matches the `add_time
= 1000` from update_config.ini).

## Boot-mode protocol (PID 0x1101 → endpoint 0x04 OUT / 0x84 IN)

Every command is a 64-byte HID interrupt-transfer packet with a
structured header:

```
byte 0:     command opcode
bytes 1-3:  sequence number (little-endian, increments per command)
bytes 4-7:  flags / length (varies per command)
bytes 8-15: command-specific metadata
bytes 16-63: command-specific payload
```

### Get version (`getv`)

Host → device:
```
00 00 00 00 04 00 00 00 67 65 74 76 00 00 00 00 ... (rest zero)
```
- bytes 0-3: `00 00 00 00` (opcode 0 + seq 0)
- bytes 4-7: `04 00 00 00` (length = 4)
- bytes 8-11: `67 65 74 76` = ASCII `"getv"`
- rest: zero

Device → host (status 0x30 = OK):
```
30 00 00 00 20 00 00 00 76 65 72 73 69 6f 6e 3a b1 24 00 00 a1 00 00 00 52 59 37 32 30 33 30 30 32 30 30 62 30 34 30 31
```
- bytes 0-3: `30 00 00 00` (status OK)
- bytes 4-7: `20 00 00 00` (payload length = 32)
- bytes 8-15: `"version:"` ASCII
- bytes 16-23: `b1 24 00 00 a1 00 00 00` (version metadata — meaning TBD)
- bytes 24-39: `"RY720300200b0401"` ASCII identifier (the `Ry` SDK signature)

### Write firmware chunk (opcode `0x31`)

First chunk (frame 17769):
```
31 00 00 74 cc 01 01 ff 00 d0 01 00 00 a1 00 00 0e 26 2e b6 9f c3 10 84 15 63 3a 65 a0 12 22 5c 6f 4e d9 34 82 fe 51 8d c0 21 e1 bb dd 3e cd da ...
```
- byte 0: `0x31` — write opcode
- bytes 1-3: `00 00 74` (sequence / checksum — `0x740000` LE)
- bytes 4-7: `cc 01 01 ff` (flags — unknown, possibly chunk size + write-mode flags)
- bytes 8-11: `00 d0 01 00` = `0x0001D000` LE — **target flash address** (0x1D000 = ~118 KB into flash)
- bytes 12-15: `00 a1 00 00` = `0x0000A100` LE — possibly length to write (41216 bytes? doesn't match total fw size 118816 directly — could be remaining-to-write or some sub-block size)
- bytes 16-63: 48 bytes of raw firmware data — **byte-for-byte identical to the first 48 bytes of `usb_hid_app_v1.0.0_593C5516.enc`**

Subsequent packets (frames 17774, 17781, ...): **NO header** — just 64 bytes of continuing firmware data. The first chunk's header initiates a write session; subsequent packets stream the rest of the firmware to the same session.

Device ACK after each write packet: zero-length URB on endpoint 0x84 IN.

### Finalize / reboot (opcode `0x33`)

Host → device (frame 26167):
```
33 e9 00 74 0c 00 01 ff c2 6e be 8a 17 05 ea 07 0d 00 03 00 00 00 ... (rest zero)
```
- byte 0: `0x33` — finalize opcode
- bytes 1-3: `e9 00 74` (sequence)
- bytes 4-7: `0c 00 01 ff` (flags — different from chunk write's `cc 01 01 ff`)
- bytes 8-15: `c2 6e be 8a 17 05 ea 07` — **8-byte CRC / signature / firmware hash**
- bytes 16-19: `0d 00 03 00` (parameters — meaning TBD)
- rest: zero

Device responds (frame 26175):
```
33 e9 00 74 01 00 01 ff 34 00 00 00 ... (rest zero)
```
- byte 0: `0x33` — echo of finalize opcode
- bytes 1-3: `e9 00 74` — echo of sequence
- bytes 4-7: `01 00 01 ff` (length = 1, flags ff01)
- bytes 8: `0x34` — **status: OK / finalize accepted**
- After this, device reboots → reappears at PID 0x2383

### Intermediate status `0x32`

Frame 26165 (device → host) before the finalize:
```
32 e8 00 74 01 00 01 ff 34 00 00 ...
```
Looks like an intermediate ACK with status 0x34 (OK). Likely the device's "all chunks received" status before the host sends the finalize.

## Critical unknowns before implementation

1. **What do bytes 4-7 of the chunk header (`cc 01 01 ff`) mean?**
   The same value appears in every chunk-related command. Possibly a
   protocol version + chunk size declaration. Need to compare against
   a different firmware (V2.3.1's 0x08) to see if it varies.

2. **How does the device know the firmware is complete?**
   No explicit "end of chunks" marker observed before the `0x32`
   status. Likely: it tracks bytes-received vs the `00 a1 00 00`
   length field in the first chunk header. If true, that field is
   the total payload bytes (after the header).

3. **What's the 8-byte CRC/signature in the finalize?**
   `c2 6e be 8a 17 05 ea 07`. Could be:
   - CRC32 of the encrypted blob (8 bytes is too many for CRC32 though)
   - First 8 bytes of a SHA-256 hash
   - A vendor-signed nonce that the .enc file embeds at a known offset

   If it's NOT derived from the firmware bytes deterministically,
   we'd need to extract it from the `.enc` file (likely at a fixed
   header offset). Without knowing the derivation, we can't generate
   it for a downgrade flash to a DIFFERENT firmware version. But for
   reflashing the SAME version, we could just store and replay the
   captured value.

4. **Error / retry behavior**
   The capture is a clean success path. We don't know what the
   device returns on bad CRC, bad chunk sequence, or interrupted
   flash.

5. **Bytes 1-3 of every command (`00 00 74`)**
   Constant `74` in byte 3 of EVERY observed command. Could be a
   protocol magic / endpoint identifier. If we send a different
   value the device might reject.

## Safe implementation path

Given the unknowns, the prudent shape for a flash implementation:

### Stage 1: Dry-run replay
Build a `FirmwareFlasher` class that takes a `.enc` file + a captured
`.pcapng` and **replays the host's exact packets byte-for-byte**.
No interpretation, no derivation — just bit-perfect replay. Test
against the same firmware version the capture was taken from.
Risk: low (we're sending exactly what the official tool sent).

### Stage 2: Different version replay
Same byte-for-byte replay logic, but swap the `.enc` file for a
different version's blob (e.g. V2.3.1's `E15CF7C4.enc` to downgrade
to Legacy dialect). Need to figure out:
- Whether the address field `00 d0 01 00` is the same across versions
- Whether the finalize CRC needs to be regenerated or can be read
  from a known offset in the `.enc` file

### Stage 3: Parameterised flash
Once dry-run + version-swap work, replace the replay with proper
parsing — read .enc, compute headers, send chunks, send finalize.

**Each stage gated on user confirmation + a working A75 Pro after
the test.** If anything misbehaves at any stage, the user can
recover by running the official `DrunkdeerUpdater.exe`.

## Ready to implement?

Technically yes — we have enough to start Stage 1 (byte-replay of
captured flash). All the necessary primitives exist in our HID
layer already.

**Recommendation**: park here for tonight. Next session: implement
Stage 1 as a `--firmware-replay <pcap>` CLI flag (NOT a UI button
yet), test against your A75 Pro, and only after that works
flawlessly start thinking about Stage 2.
