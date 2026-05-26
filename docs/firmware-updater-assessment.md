# DrunkDeer Firmware Updater — Static Security Assessment

> Offline, read-only analysis of the publicly downloadable updater package.
> No hardware was connected or written to. Purpose: determine whether
> firmware-level bug fixes are feasible for the DrunkDeer-Control driver
> project, or whether we are limited to host-side workarounds.

## Scope & caveats

- **Version discrepancy:** the request named `DrunkdeerUpdaterV1.4.9.zip`.
  That file does not exist on this machine. What is present (and what
  `CLAUDE.md` references) is **`DrunkdeerUpdaterV2.3.1`** — assessed here.
  If V1.4.9 specifically matters, it must be sourced separately; the
  conclusions below are for V2.3.1.
- Purely static. No device was connected. Anything that happens *inside
  the keyboard's bootloader ROM* (key storage, post-decryption
  signature/CRC enforcement, anti-rollback) is **not determinable from
  these files** and is flagged as such.
- **Correction (supersedes an earlier draft).** An earlier pass claimed
  the web-driver Antler JS contained *no* firmware-flash code. That was
  wrong — an artifact of `ripgrep` silently failing to scan the 5.7 M-char
  single-line minified bundle. A Python substring scan finds a **complete
  WebHID flash implementation** in the JS. There are therefore **three**
  official channels (one shared HID-app wire format): the web driver
  (WebHID), the native updater HID-app path, and the native updater NXP
  path. The crypto conclusion is unchanged and in fact *reinforced* — see
  §3 and §4.1.
- Reproduction artifacts: PE/entropy/cipher probe at
  `C:\Users\skdes\Downloads\antler-work\_fwprobe.py`; JS extraction at
  `C:\Users\skdes\Downloads\antler-work\_jsfw.py`.

## 1. Package contents

| Component | Role |
|---|---|
| `DrunkdeerUpdater.exe` (16 MB, x86 MFC) | Orchestrator. Reads `config/config.ini` device table, drives both update paths. |
| `UsbHid_v1.2.7.dll` (x86 MFC) | HID transport. Exports `chipGoToBootFirmware` / `chipAppUpdateFirmware` / `chipCloseFirmware`. |
| `A75_master/blhost.exe` (471 KB) | **Stock NXP MCU Bootloader host tool**, unmodified (PDB `C:\work\mcu-boot-tools\tools\blhost\Release\blhost.pdb`, "Copyright 2016-2019 NXP"). Imports only KERNEL32 + SETUPAPI. |
| `<MODEL>/usb_hid_app_v1.0.0_<HASH>.enc` ×12 | **Encrypted** firmware blobs, all exactly **118 816 bytes** (= 7426 × 16). WIN/MAC variants of a model are byte-identical. |
| `A75Master.bin` / `A75Ultra.bin` (131 KB), `DualBankBoot.bin` (8.3 KB) | **Plaintext** ARM Cortex-M images (master/ultra only). |
| `update_config.ini` (per model), `config/config.ini` | Plaintext metadata: VID/PID, BOOT-mode PID, report ID, `encryption_en`, identity triples. |

There are **two distinct update paths**, selected by `UpdateType` in
`config.ini`:

- **HID-app path** (default — A75 Pro, A75, G60, G65, G75): encrypted
  `.enc` over the custom HID protocol.
- **NXP-bootloader path** (`UpdateType=1` A75 Ultra, `UpdateType=2`
  A75 Master): plaintext `.bin` via bundled `blhost.exe`.

## 2. Encrypted, signed, or plaintext?

### `.enc` blobs (A75 Pro etc.) — encrypted, AES-class ECB

- `update_config.ini` → `[ENCRY_EN] encryption_en=1`.
- Entropy ≈ 7.65 bit/byte (high but sub-random — structure remains).
- **ECB confirmed by block-repeat test:** of 7425 16-byte blocks, one
  block repeats **1285×**, another **846×** (these are the ciphertexts
  of the firmware's large `0x00` / `0xFF` fill regions). A stream cipher
  or CBC/CTR cannot produce identical repeated ciphertext blocks.
  → **16-byte block cipher in ECB mode, no IV, block-aligned** —
  consistent with AES-128/256-ECB over the raw image.
- No cryptographic signature is present in, or applied by, the host
  (see §3). The 8-hex filename suffix (`_E15CF7C4`) is a 32-bit tag —
  an image identifier / integrity value, **not** a signature (too short;
  the host never computes it).

### `.bin` blobs (A75 Master/Ultra) — plaintext, unsigned by the host

- Visible ARM Cortex-M vector table: first word `00 00 03 20` =
  initial SP `0x20030000`. Entropy ≈ 7.07 (normal compiled ARM). Not
  encrypted, not wrapped in an NXP SB container — flashed as raw
  `write-memory`.

## 3. Crypto verification before flashing?

**Host-side: none — on both the native updater and the web driver.**
Evidenced four independent ways:

- `UsbHid.dll`'s ADVAPI32 imports are **registry-only** —
  `RegOpenKeyExW`, `RegSetValueExW`, … and **zero `Crypt*` functions**.
- No `bcrypt.dll` / `ncrypt.dll` / OpenSSL / mbedtls / wolfSSL linked by
  any binary.
- No statically-compiled primitive: AES forward S-box, AES inverse
  S-box, SHA-256 IV (both endiannesses), and the standard CRC32 table
  are all **absent** from `UsbHid.dll`.
- **Web driver JS:** `crypto.subtle`, `.digest(`, `SHA-256/1`, `AES-`,
  `decrypt(`, `encrypt(`, `crc32`, `subtle` — **all zero occurrences**
  in the bundle. The downloaded bytes go straight from `.arrayBuffer()`
  into `setUpgradeFirmwareData(new Uint8Array(s))` with no transform.

The host therefore **does not decrypt, hash-verify, or signature-check**
the image. It ships the `.enc` to the device **verbatim**. Decryption
(and any signature/CRC enforcement) necessarily happens **on-device in
the bootloader ROM**, using a key the host never sees and that is
**not in this package**. Whether the device additionally enforces a
signature or anti-rollback after decryption **cannot be determined
statically** — that requires the hardware.

## 4. Flash protocol

### 4.1 HID-app path — exact wire format (from web-driver JS)

This is the protocol both the web driver and the native updater's
`chipAppUpdateFirmware` use. The web-driver JS gives it exactly.

**Firmware download** (web driver):

```
GET https://api.drunkdeer.club/desktop/update_programs/download.json
        ?search_name=drunkdeer<typeTag>        (X60 → drunkdeer<tag>future)
        &category=release                       (if page host is drunkdeer-antler.c*)
        &category=beta                          (otherwise)
```

Plain HTTPS via a `fetch` wrapper (`Ke = "https://api.drunkdeer.club"`,
10 s timeout, optional `Authorization: Bearer <token>` from
`localStorage` — not required for the download). Despite the `.json`
route name the **response body is the raw firmware binary**, consumed via
`(await mw(...)).arrayBuffer()`. No manifest, no per-image hash, no
signature field is fetched or checked.

**Flash transport** — WebHID `device.sendReport(reportId, body)` where
`reportId` is the device's configured report ID (= **4** for the app
device per `update_config.ini`). Each `body` is a 63-byte report:

| Phase | byte[0] | byte[1] | byte[2] | byte[3..6] (uint32 LE) | byte[7..62] |
|---|---|---|---|---|---|
| START | `0xF9` | `0x00` | `0x00` | total firmware length | — |
| DATA  | `0xF9` | `0x01` | chunk len (≤56) | sequence index | ≤56 payload bytes |
| END   | `0xF9` | `0x02` | `0x00` | — | — |

- Chunk stride **56 bytes**; sequence index increments per DATA packet.
- Sequence: START → `sleep(200 ms)` → DATA × N (progress = `floor(off/total*100)`)
  → END → 2 s later the UI shows “固件升级成功，重启中…” (upgrade
  succeeded, rebooting).
- Flow control: single packet in flight; queue pumped with
  `handshake_tickvalue = 5` credits, a 50 ms link-release timeout, and a
  400 ms watchdog tick that raises a stall error if credits drain.
- Note: the web path sends `0xF9` START/DATA/END **to the running app
  device (report ID 4)** — it does *not* perform an explicit
  `chipGoToBootFirmware` re-enumeration to BOOT PID `0x1101`. The app
  firmware itself consumes the `0xF9` opcodes (stages the image and
  reboots internally). The native updater's `chipGoToBootFirmware` →
  BOOT PID `0x1101` → `chipAppUpdateFirmware` → `chipCloseFirmware`
  sequence (from `update_config.ini`: `add_time=1000 ms`,
  `time_out=30 s`) is the same image stream wrapped in an explicit
  bootloader re-enumeration.

### 4.2 NXP path (A75 Master/Ultra)

The exact `blhost` command script is embedded verbatim in
`DrunkdeerUpdater.exe`:

```
blhost -u 0x1FC9,0x0021 -- flash-erase-all
blhost -u 0x1FC9,0x0021 -- write-memory 0x0        (DualBankBoot.bin)
blhost -u 0x1FC9,0x0021 -- write-memory 0x010000   (A75Master.bin / A75Ultra.bin)
blhost -u 0x1FC9,0x0021 -- reset
```

`-u 0x1FC9,0x0021` = NXP's standard ROM-bootloader USB-HID device
(VID **0x1FC9 = NXP**). MCU is an NXP part with a dual-bank boot scheme;
images are flashed as **raw plaintext** at offsets `0x0` and `0x10000`.
`updatelog.log` shows a real run: A75 Master `0.25 → 0.38`, ~6 s,
"complete with result:0".

## 5. Live endpoint probe (2026-05-19)

Read-only `GET`s to the production endpoint
`https://api.drunkdeer.club/desktop/update_programs/download.json`
(`search_name` = `"drunkdeer" + typeTag.toLowerCase()` per the JS map
`AB`; X60 → `drunkdeerx60future`). nginx/1.26.3, Rails-style backend.

Two distinct 404 JSON bodies:

- **33 B** `{"error":"找不到该设备。"}` — *"device not found"*: server
  does not recognise the `search_name` (G75, G75JP, G65Lite, A75IOS_UK
  as derived from the JS — their backend names differ or are absent).
- **60 B** `{"error":"没有当前设备当前分类的更新程序。"}` — *"no update
  program for this device / category"*: server **recognises** the
  device but has **no firmware** for that channel.

| `search_name` | release | beta |
|---|---|---|
| `drunkdeera75pro` | **404 — device known, no firmware** | **404 — device known, no firmware** |
| `drunkdeera75` | 404 — known, no firmware | 404 — known, no firmware |
| `drunkdeerg65` | 404 — known, no firmware | 404 — known, no firmware |
| `drunkdeerg60` | 404 — known, no firmware | 404 — known, no firmware |
| `drunkdeera75ultra` | **200** `A75Ultra.bin` 134 232 B | **200** 134 224 B |
| `drunkdeera75master` | **200** `A75Master.bin` 134 232 B | **200** 134 224 B |
| `drunkdeerx60future` | **200** `lpcxpresso55s28_…_bm.bin` 140 096 B | **200** 140 104 B |

Findings:

- **No A75 Pro firmware exists upstream** — in *either* channel. A75 Pro
  is in the "device recognised, no program" class, so this is a
  deliberate backend state, not a wrong-name artifact. The web driver's
  "upgrade firmware" button for an A75 Pro silently no-ops (the JS guards
  on `s.byteLength > 0`). This **confirms** CLAUDE.md's "No in-place
  firmware update path exists for A75 Pro at the moment" with a dated,
  live data point.
- **Every model the endpoint serves is an NXP `.bin`/blhost device**
  (A75 Ultra, A75 Master, X60 — the last is an NXP **LPC55S28**, image
  `lpcxpresso55s28_dev_composite_hid_mouse_hid_keyboard_bm.bin`).
  **Zero `.enc` HID-app models** (A75 Pro, A75, G65, G60) have any
  downloadable firmware. The encrypted-HID-app update path has client
  code in the web driver but is **dormant in production**.
- Served binaries are plaintext ARM (SP `0x20030000`) and **not**
  byte-identical to the V2.3.1 bundle (`A75Ultra.bin`: same 134 232 B,
  different SHA-256; release vs beta differ by 8 B). The OTA endpoint is
  maintained independently of, and diverges from, the static bundle.

## Feasibility verdict for DrunkDeer-Control

| | A75 Pro / G65 / G60 / G75 (`.enc`) | A75 Master / Ultra (`.bin`) |
|---|---|---|
| Image form | AES-ECB encrypted, key on-device only | Plaintext ARM, in this package |
| Custom firmware feasible from this package? | **No** — no host key, no plaintext, on-device decrypt | **Plausibly yes**, *if* the NXP MCU's ROM does not enforce secure-boot / signed images (a device-side ROM property not knowable statically) |

**Bottom line:** for the A75 Pro line (the LW-pair / remap-commit
firmware questions), **firmware-level fixes are not feasible** from
public artifacts — the `.enc` key is in device ROM, unrecoverable
offline. The project remains correctly limited to **host-side
workarounds**. The only path with any firmware-modding surface is
A75 Master/Ultra (plaintext + stock NXP blhost), and even that hinges
on the MCU's secure-boot fuse state, which cannot be confirmed without
the hardware and is outside the driver project's scope. The
ECB-without-integrity observation is a noteworthy weakness in the
official scheme but is **not actionable** without the on-device key.

> **Update (post-§5 probe + bootloader analysis):** the device-side
> verification question is now answered statically for the NXP path —
> `DualBankBoot.bin` contains zero cryptographic capability (0/17
> crypto-primitive fingerprints, no HASHCRYPT/CASPER references, no
> bignum, no embedded key/cert/sig blob, strings identify it as an NXP
> MCUXpresso SDK *demo* bootloader). Combined with the factory ROM
> being provably not in secure-boot mode (image format + tooling
> argument), every layer of the NXP chain has no integrity gate.
> See `docs/firmware-security-disclosure-report.md` F6 and the runnable
> [`tools/bootloader-crypto-scan.py`](../tools/bootloader-crypto-scan.py).
> Project conclusion (below) is unaffected — DrunkDeer-Control still
> doesn't flash anything.

**The "reinstall stock firmware" angle is dead for A75 Pro** (revised
after the §5 probe). The transport is fully known and HidSharp-
implementable, but there is **no A75 Pro image to fetch** — DrunkDeer's
production endpoint serves firmware only for the NXP `.bin`/blhost
models (Ultra/Master/X60), which already have the official native
updater and need no help from us. For the `.enc` HID-app line (A75 Pro,
A75, G65, G60) there is *no public firmware artifact at all*: the
bundled `.enc` is encrypted with an on-device-only key, and the OTA
endpoint returns "no update program." A firmware feature in
DrunkDeer-Control is therefore **not viable** for the models the
project targets. The conclusion is unchanged and now airtight:
**stay host-side-workaround-only.**

---

*Method: PE import-table parsing, Shannon entropy, ARM vector-table
identification, AES/SHA/CRC constant scans, AES-ECB block-repeat
analysis, and string extraction over `DrunkdeerUpdaterV2.3.1`; plus
Python substring extraction of the minified Antler web bundle
`index.CJWCGjvj.live-2026-05-19.js` (ripgrep is unreliable on its
multi-megabyte single line — substring scan was used instead).
Static/offline except §5, which is a set of authorised read-only
`GET`s to the public production endpoint on 2026-05-19 (no auth, no
writes, firmware bodies not flashed anywhere).*
