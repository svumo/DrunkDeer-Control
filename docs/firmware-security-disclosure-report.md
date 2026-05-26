# DrunkDeer Firmware Update — Security Disclosure Report

**Classification:** CONFIDENTIAL — coordinated disclosure draft (not for
public release until vendor-agreed)
**Report version:** 1.0
**Date:** 2026-05-19
**Reporter:** _(fill in name / handle / contact email before sending)_
**Vendor:** DrunkDeer
**Analyst note:** This report is built entirely from static analysis of
publicly downloadable files plus a small set of authorised read-only
HTTP GETs. **No malicious firmware, exploit, or delivery mechanism was
created or used.** Impact is described in prose; reproduction steps are
non-destructive.

> This document supersedes and consolidates the working notes in
> `docs/firmware-updater-assessment.md` and
> `docs/firmware-update-disclosure-draft.md`. Send *this* file.

---

## Table of contents

1. Executive summary
2. Scope & methodology
3. Affected components & versions
4. Findings
   - F1 — No host-side cryptographic verification
   - F2 — Firmware confidentiality without integrity (`.enc`, AES-ECB)
   - F3 — Automatic flash with no user confirmation (web driver)
   - F4 — Web-reachable update surface (WebHID + endpoint)
   - F5 — Firmware images carry no authenticity data (NXP `.bin`)
   - F6 — DrunkDeer second-stage bootloader contains no crypto
5. The firmware-update protocol (vendor context)
6. Impact & threat model
7. Severity assessment
8. Reproduction — static (anyone can re-run, no hardware)
9. Reproduction — optional non-destructive hardware probe
10. Recommended remediation
11. Coordinated-disclosure terms
12. Appendix A — data tables
13. Appendix B — what was deliberately NOT done

---

## 1. Executive summary

DrunkDeer's firmware-update flow performs **no host-side cryptographic
verification** of the firmware image before it is written to the
keyboard, on **both** the official Antler web driver and the native
`DrunkdeerUpdater`. The web driver additionally **begins flashing
automatically, with no explicit user-confirmation step**, as soon as its
upgrade modal is shown, over a surface reachable from a web page
(WebHID) after a single per-device permission grant.

For the NXP-bootloader models (A75 Ultra, A75 Master, X60), every layer
of integrity gating is proven absent **statically**: the host does no
crypto (F1); the firmware images carry no signature, certificate, or
CRC and are flashed as plain raw images via `write-memory` (F5, with
two independent proofs that the factory ROM is not enforcing secure
boot); and DrunkDeer's own second-stage bootloader binary contains
**zero cryptographic capability** of any kind (F6 — it is a barely
modified NXP MCUXpresso SDK demo bootloader that can erase, program,
and jump, with no primitives to verify with). Net: these models will
accept arbitrary firmware. Malicious keyboard firmware is a persistent
host-compromise primitive because a keyboard injects keystrokes.

The encrypted (`.enc`) HID-app models (A75 Pro, A75, G65, G60) use
AES-ECB (confidentiality but **no integrity/authenticity**); their
device-side behaviour is not determinable from static analysis and is
reported conservatively.

**Headline:** *Unauthenticated, auto-started firmware update on a
web-reachable surface; for the NXP models the full chain (host → image
→ factory ROM → second-stage bootloader) has been independently shown
to have no integrity verification at any layer.*

## 2. Scope & methodology

- **Static, offline analysis** of publicly downloadable artifacts:
  `DrunkdeerUpdaterV2.3.1` (the official updater bundle) and the public
  Antler web-driver JavaScript bundle.
- **Authorised read-only HTTP GETs** (2026-05-19) to the public
  firmware endpoint to characterise its behaviour. No authentication
  used, no writes, no firmware flashed anywhere.
- Techniques: PE import-table parsing, Shannon entropy, AES/SHA/CRC
  constant scans, AES-ECB block-repeat analysis, ARM/NXP image-structure
  parsing, and substring extraction of the minified JS bundle.
- **Not done:** no device was flashed; no malicious firmware, payload,
  or delivery page was created (see Appendix B).

## 3. Affected components & versions

| Component | Identifier | Role |
|---|---|---|
| Antler web driver | bundle `index.*.js` (drunkdeer-antler.com) | WebHID configurator incl. firmware upgrade modal |
| Firmware endpoint | `https://api.drunkdeer.club/desktop/update_programs/download.json` | Serves raw firmware binaries |
| Native updater | `DrunkdeerUpdater.exe` v2.3.1 | Standalone firmware updater |
| HID transport DLL | `UsbHid_v1.2.7.dll` | Exports `chipGoToBootFirmware` / `chipAppUpdateFirmware` / `chipCloseFirmware` |
| NXP bootloader host | `blhost.exe` (stock NXP MCUBoot tool) | Flashes `.bin` for Ultra/Master/X60 |

Models, by update path:

- **Encrypted HID-app (`.enc`)** — A75 Pro, A75, G65, G60, G75, …
- **NXP bootloader (`.bin` via blhost)** — A75 Ultra (`UpdateType=1`),
  A75 Master (`UpdateType=2`), X60 (`drunkdeerx60future`, NXP LPC55S28).

## 4. Findings

### F1 — No host-side cryptographic verification

The host never decrypts, hashes, or signature-checks the firmware before
flashing it. Evidence:

- `UsbHid_v1.2.7.dll` imports from `ADVAPI32.dll` are **registry-only**
  (`RegOpenKeyExW`, `RegSetValueExW`, …) — **zero `Crypt*` functions**.
- No `bcrypt.dll` / `ncrypt.dll` / OpenSSL / mbedtls / wolfSSL linked by
  `UsbHid_v1.2.7.dll`, `DrunkdeerUpdater.exe`, or `blhost.exe`.
- No statically-compiled primitive in `UsbHid_v1.2.7.dll`: AES forward
  S-box, AES inverse S-box, SHA-256 IV (both endiannesses), and the
  standard CRC32 (`0xEDB88320`) table are all **absent**.
- Web driver JS: `crypto.subtle`, `.digest(`, `SHA-256/1`, `AES-`,
  `decrypt(`, `encrypt(`, `crc32`, `subtle` — **all zero occurrences**.
  Downloaded bytes pass straight from `.arrayBuffer()` into
  `setUpgradeFirmwareData(new Uint8Array(s))` unmodified.

Reproduction: §8.1, §8.4.

### F2 — Firmware confidentiality without integrity (`.enc`, AES-ECB)

The encrypted images for the HID-app models are a 16-byte block cipher
in **ECB mode** — confidentiality only, no integrity, no authenticity:

- All 12 `.enc` files are exactly 118 816 bytes (= 7426 × 16).
- Entropy ≈ 7.64–7.68 bit/byte (high but sub-random).
- ECB tell: in one image, of 7425 16-byte blocks one block repeats
  **1285×** and another **846×** (ciphertexts of large constant fill
  regions). A stream/CBC/CTR construction cannot produce repeated
  identical ciphertext blocks.

ECB encryption is **not** evidence that tampered/forged images are
rejected. Reproduction: §8.2.

### F3 — Automatic flash with no user confirmation (web driver)

The web driver's `UpgradeModal` Vue component runs the fetch-and-flash
routine from its `onMounted` hook (`Be(() => { t() })`). `t()`:

```
const a = "drunkdeer" + B.getKeyboardTypeTag.toLowerCase();   // X60 → +"future"
const r = location.href.includes("https://drunkdeer-antler.c") ? "release" : "beta";
const s = await (await mw({ search_name: a, category: r })).arrayBuffer();
s.byteLength > 0 && (
    A.keyboardComm.setUpgradeFirmwareData(new Uint8Array(s)),
    A.keyboardComm.upgradeFirmwareStart()      // flashing begins here
);
```

There is **no in-flow "confirm update?" step**. The only visible control
is a button labelled 「手动更新」 ("manual update") that closes the modal
but does not abort an in-progress flash. (The modal is visible and shows
progress — this is "no confirmation," not "covert"; both matter.)
Reproduction: §8.4.

### F4 — Web-reachable update surface (WebHID + endpoint)

The flash path is reachable from any web origin the user grants device
access to, via `navigator.hid` + `device.sendReport(reportId, …)`.
This is gated by an explicit browser device-picker grant (not
zero-click) but is socially engineerable ("update your keyboard"). The
firmware is fetched over plain HTTPS from a single endpoint
(`api.drunkdeer.club`), body = raw binary, with an optional
`Authorization: Bearer` header that is not required for the download.
Reproduction: §8.3.

### F5 — Firmware images carry no authenticity data (NXP `.bin`)

Structural analysis of the served **and** bundled images for the
NXP-bootloader models (A75 Ultra, A75 Master, X60, and the shipped
second-stage `DualBankBoot.bin`):

- NXP signed-image header at offsets `0x20`/`0x24`/`0x28` (imageLength /
  imageType / cert-block-offset) is **all-zero** → `imageType = 0`
  ("plain — no CRC, no signature").
- No legacy vector-table checksum (sum of first 8 vectors ≠ 0).
- No appended signature trailer (last-256-byte entropy ≈ 3.8–4.8 =
  ordinary ARM code/strings, not a ~7.9+ RSA/ECDSA blob).
- Native updater flashes them with plain `blhost ... write-memory` to
  raw addresses `0x0` and `0x010000` — **not** `receive-sb-file`
  (NXP's signed/encrypted Secure Binary container).

**Deduction (no hardware required):** an NXP secure-boot-enabled part
will **not** boot an unsigned `imageType = 0` image — it requires the
signed header + certificate block these images lack. Since these are the
production images and the devices run them, on-device secure boot is
**disabled**. Independent corroboration: DrunkDeer's own native updater
flashes the bootloader with plain `blhost ... write-memory 0x0` — a
secure-boot-enabled NXP part rejects raw `write-memory` and requires a
`receive-sb-file` (signed Secure Binary container) instead. Two
independent structural proofs that the factory ROM is **not** enforcing
secure boot. See F6 for the matching proof on DrunkDeer's second-stage
bootloader. Reproduction: §8.5; optional confirmation §9.

### F6 — DrunkDeer second-stage bootloader contains no crypto

Static analysis of `DualBankBoot.bin` — the 8 496-byte bootloader
DrunkDeer flashes to address `0x0` on the A75 Ultra / A75 Master MCUs
(byte-identical across both models: sha256
`710ce1cb1d57f9ffc04da970f122d24d4ccc107a1f3f6871b1c0e009c80ed092`):

- **Zero crypto-primitive constants** of 17 fingerprints scanned: no
  AES forward or inverse S-box, no SHA-256 H0 or K constants (either
  endianness), no SHA-1, no MD5, no CRC32 polynomial or table, no RSA
  exponent 65537, no NIST P-256 prime, no Curve25519 basepoint.
- **No references to NXP on-chip crypto peripherals**: no HASHCRYPT
  (`0x400A4000`), no CASPER (`0x400A5000`), no PUF, no PRINCE. The only
  MMIO it references is the flash controller (`0x40034000` /
  `0x40020000`) — it can erase and program flash, nothing more.
- **No bignum routines** (~10 Thumb-2 `UMULL`/`UMLAL`-like halfwords in
  8 KB; RSA/ECC needs hundreds — this is background noise).
- **No embedded key, certificate, or signature**: whole-file entropy
  6.83; **zero 256-byte windows above entropy 7.0**. A real RSA public
  key would appear as a ~256-byte high-entropy island; absent.
- **Strings positively identify it as an NXP MCUXpresso SDK *demo*
  bootloader**: `"Bootloader demo run"`, `"Flash init successfull!!.
  Halting..."` (the typo is in the original; demo-quality code),
  `"jump flash flag Erase / Program / read statue = %d"`, plus
  `kFLASH_PropertyPflashBlockBaseAddr / PageSize / SectorSize /
  TotalSize` — the exact constant names from NXP's `fsl_flash.h` FTFx
  driver. The only verbs in evidence are *erase, program, read, jump*.
  No `verify`, no `sign`, no `hash`, no `check`, no `crc`.

**Conclusion:** the second-stage bootloader has *no cryptographic
capability whatsoever*. It cannot perform signature verification, hash
verification, CRC check, or any form of authenticity test on an app
image; it can only write what it is handed. Combined with F5's proof
that the factory ROM is not enforcing secure boot, **both halves of the
device-side verification question are now answered statically — no
hardware test required.** Reproduction: §8.6.

**Anticipated reviewer concern — "but we don't have the mask-ROM
binary":** the factory ROM's enforcement is constrained by the image
formats it will boot. NXP secure-boot ROMs reject `imageType = 0`
plain images by design; DualBankBoot.bin is `imageType = 0` and runs.
Independently, DrunkDeer's tooling uses plain `write-memory` rather
than `receive-sb-file`, which a secure-boot-enabled part would refuse.
The ROM's source is not needed to know its enforcement is off — the
observable boundary conditions prove it.

## 5. The firmware-update protocol (vendor context)

Provided so the vendor can scope the affected surface; this is
documentation of DrunkDeer's *own* existing protocol, not an exploit.

**Download:** `GET …/download.json?search_name=drunkdeer<typeTag>&category=release|beta`.
Body is the raw firmware binary. `typeTag` from the JS model map;
`category=release` only when page host is `drunkdeer-antler.c*`.

**HID-app flash transport** (web driver; `device.sendReport(reportId, body63)`,
report ID 4 for the app device per `update_config.ini`):

| Phase | byte[0] | byte[1] | byte[2] | byte[3..6] (u32 LE) | byte[7..62] |
|---|---|---|---|---|---|
| START | `0xF9` | `0x00` | `0x00` | total length | — |
| DATA | `0xF9` | `0x01` | chunk len ≤56 | sequence index | ≤56 payload |
| END | `0xF9` | `0x02` | `0x00` | — | — |

56-byte chunk stride; single-packet-in-flight handshake
(`handshake_tickvalue = 5`, 50 ms link-release, 400 ms watchdog);
200 ms pause after START.

**NXP `.bin` flash** (native updater, embedded verbatim):

```
blhost -u 0x1FC9,0x0021 -- flash-erase-all
blhost -u 0x1FC9,0x0021 -- write-memory 0x0        (DualBankBoot.bin)
blhost -u 0x1FC9,0x0021 -- write-memory 0x010000   (A75Master.bin / A75Ultra.bin)
blhost -u 0x1FC9,0x0021 -- reset
```

`-u 0x1FC9,0x0021` = NXP ROM-bootloader USB-HID device (VID 0x1FC9 NXP).

## 6. Impact & threat model

Malicious keyboard firmware can emulate arbitrary keystrokes →
attacker-controlled command execution on the host, persistence that
survives OS reinstallation, and portability to any machine the keyboard
is later connected to (the classic "malicious USB-HID firmware" class,
here made **web-reachable**).

Reachability conditions: (a) a WebHID device grant for the keyboard
(user-initiated, social-engineerable); (b) the upgrade modal is reached;
(c) the firmware source returns an image — a malicious/compromised page,
a compromised firmware endpoint, or a TLS-MITM position. Given F1 and
F5, the image does not need to be validly signed for the NXP models.

## 7. Severity assessment

- **NXP `.bin` models (A75 Ultra / Master / X60): Critical, confirmed
  on static evidence.** Three independent layers of integrity gating are
  proven absent: F1 (host does no crypto), F5 (image carries no
  signature/cert/CRC; factory ROM is not enforcing secure boot, proven
  via image-format and `write-memory`-vs-`receive-sb-file` arguments),
  F6 (DrunkDeer's second-stage bootloader binary contains zero
  cryptographic capability). The full chain — fetch → host → ROM →
  second-stage → flash — has no verification at any layer. The §9
  hardware test is no longer needed to substantiate severity; it would
  only re-confirm what the bootloader binary already shows.
- **Encrypted `.enc` models (A75 Pro / A75 / G65 / G60): conditional /
  informational.** Host-side gaps (F1) and ECB-without-integrity (F2)
  are real defence-in-depth/UX issues, but device-side authenticity is
  indeterminate and no public forgeable image exists. Not asserted as
  high.

Do not overstate the `.enc` path; do not understate the `.bin` path.

## 8. Reproduction — static (no hardware, re-runnable by anyone)

All scripts are benign analysis tooling. `python` 3.x, `curl`.
`BASE` = the extracted `DrunkdeerUpdaterV2.3.1` folder.

**Runnable scripts in this repo (under `tools/`):**

| Script | Covers |
|---|---|
| [`tools/firmware-security-verify.py`](../tools/firmware-security-verify.py) | F1 PE imports + F2 ECB stats + F4 endpoint probe + F5 image-header analysis |
| [`tools/analyze-extracted.py`](../tools/analyze-extracted.py) | Bundle-level F1/F2 cross-checks (DLL crypto scan, `.enc` ECB stats, bundled-vs-endpoint diffs) |
| [`tools/bootloader-crypto-scan.py`](../tools/bootloader-crypto-scan.py) | F6 second-stage bootloader crypto-capability scan |

The inline snippets in §8.1–§8.6 below are minimal self-contained
versions of the same checks for vendors who prefer not to run a script.

### 8.1 No host-side crypto — PE import table

```python
import struct
def imported_dlls(path):
    d=open(path,'rb').read(); pe=struct.unpack('<I',d[0x3c:0x40])[0]
    nsec=struct.unpack('<H',d[pe+6:pe+8])[0]; opt=pe+24
    is64=struct.unpack('<H',d[opt:opt+2])[0]==0x20b
    dd=opt+(112 if is64 else 96)
    imp=struct.unpack('<I',d[dd+8:dd+12])[0]
    sh=opt+struct.unpack('<H',d[pe+20:pe+22])[0]; secs=[]
    for i in range(nsec):
        o=sh+40*i; vs,vr,rs,rp=struct.unpack('<IIII',d[o+8:o+24]); secs.append((vr,vs,rp,rs))
    r2o=lambda x:next((rp+(x-vr) for vr,vs,rp,rs in secs if vr<=x<vr+max(vs,rs)),None)
    o=r2o(imp); out=[]
    while True:
        e=d[o:o+20]
        if len(e)<20 or e==b'\x00'*20: break
        nr=struct.unpack('<I',e[12:16])[0]
        if not nr: break
        no=r2o(nr); out.append(d[no:d.find(b'\x00',no)].decode('latin1')); o+=20
    return out
print(imported_dlls(r"<BASE>\UsbHid_v1.2.7.dll"))
```

Expected: no `bcrypt.dll`/`ncrypt.dll`/crypto library. Resolving the
ADVAPI32 thunks (see `docs/firmware-updater-assessment.md` for the full
parser) yields only `Reg*` functions, no `Crypt*`.

### 8.2 `.enc` is AES-ECB — entropy + block-repeat

```python
import collections, math, glob, os
def ent(b):
    f=[0]*256
    for x in b: f[x]+=1
    n=len(b); return -sum((c/n)*math.log2(c/n) for c in f if c)
p=glob.glob(r"<BASE>\**\*.enc", recursive=True)[0]
d=open(p,'rb').read()
print("size",len(d),"entropy",round(ent(d),3))
bl=[d[i:i+16] for i in range(0,len(d)-16,16)]
c=collections.Counter(bl)
print("blocks",len(bl),"unique",len(c),"top",[(b.hex(),n) for b,n in c.most_common(2)])
```

Expected: size 118816, entropy ≈ 7.65, one 16-byte block repeating
~1285×.

### 8.3 Endpoint serves unsigned binary (read-only)

```
curl -s -D - -o out.bin "https://api.drunkdeer.club/desktop/update_programs/download.json?search_name=drunkdeera75ultra&category=release"
```

Expected: `HTTP 200`, `content-type: application/octet-stream`,
`content-disposition: attachment; filename="A75Ultra.bin"`. (Encrypted
models e.g. `drunkdeera75pro` return `HTTP 404` JSON — no firmware
served; see Appendix A.)

### 8.4 Web driver does no crypto / auto-flashes (JS)

The minified bundle is one ~5.7 M-char line; ripgrep under-scans it —
use Python substring extraction:

```python
js=open("index.<hash>.js",encoding="utf-8",errors="replace").read()
for t in ["crypto.subtle",".digest(","SHA-256","AES-","decrypt(","sign(","crc32"]:
    print(t, js.count(t))                      # all 0 (sign/md5 incidental)
i=js.find("upgradeFirmwareStart"); print(js[i-200:i+700])   # onMounted t(); 0xF9 builders
```

Expected: all crypto tokens 0; the `onMounted` `t()` flow and the
`sendUpgradeFirmwareData* … [0]=249` (`0xF9`) packet builders are
present.

### 8.5 NXP images carry no authenticity data

```python
import struct
for p in [r"<BASE>\A75_ultra\A75Ultra.bin", r"<BASE>\A75_master\A75Master.bin"]:
    d=open(p,'rb').read(); w=struct.unpack('<8I',d[:32])
    il,it,co=struct.unpack('<I',d[0x20:0x24])[0],struct.unpack('<I',d[0x24:0x28])[0],struct.unpack('<I',d[0x28:0x2c])[0]
    print(p, "imageType=0x%x"%it, "hdr0x20=%d/%d/%d"%(il,it,co),
          "vecsum=0x%08x"%(sum(w)&0xffffffff))
```

Expected: `imageType = 0`, header fields all 0, vector sum ≠ 0 → no
signed-image container, no CRC, no legacy checksum.

### 8.6 Second-stage bootloader has no crypto (F6)

```
python tools/bootloader-crypto-scan.py \
    <BASE>/A75_master/DualBankBoot.bin \
    <BASE>/A75_ultra/DualBankBoot.bin
```

Expected output (verbatim):

```
=== .../A75_master/DualBankBoot.bin
    size=8496  sha256=710ce1cb1d57f9ffc04da970f122d24d4ccc107a1f3f6871b1c0e009c80ed092
    SP=0x20030000  Reset=0x00000181  ...
[crypto primitive fingerprints: 0/17]
    none found  ->  bootloader cannot hash, sign, or verify
[NXP peripheral / flash MMIO references]
    FLASH controller (LPC55)   0x40034000  @0x950
    Kinetis FTFx flash         0x40020000  @0x5D4
[entropy]  whole-file: 6.828   no >=7.0 windows  ->  no embedded key, cert, or signature
[security/boot strings] 'Bootloader demo run', 'Flash init successfull!!. Halting...',
    'jump flash flag Erase/Program/read statue = %d',
    'kFLASH_PropertyPflashBlockBaseAddr/PageSize/SectorSize/TotalSize = 0x%X', ...
=> NO CRYPTO CAPABILITY DETECTED
```

Both bundled copies are byte-identical (same sha256). Script exits `0`
on the expected "no crypto" result, non-zero if any primitive is found.

## 9. Reproduction — optional non-destructive hardware probe

**Purpose:** physically re-confirm what F5 + F6 already prove statically
(does the device reject an unsigned/modified image?). **No longer
required** for the report — F1 + F5 + F6 together close the chain — but
included for vendors who want a hardware data point. **Researcher-owned
hardware only. Use the official updater / `blhost`. No payload, no
injected code, no custom HID flasher.**

1. Use an NXP `.bin` model you own (Ultra/Master/X60).
2. Take the **vendor's known-good** image.
3. Apply a **single functionally inert** change: flip one byte in a
   trailing-padding / unused region. **Not** the vector table, **not**
   code, **not** the HID report descriptor. The change must do nothing,
   so the test is a pure integrity oracle and nothing harmful can run.
4. Flash via the **official** updater / `blhost` (not custom tooling).
5. Observe: **rejected** → device has an authenticity check (finding
   downgrades to defence-in-depth). **Accepted / boots normally** →
   missing authenticity confirmed; §5 fuse-state question resolved.
6. Record the result for §F5 / §7.

**Risks:** bricking. Rely on the device's dual-bank / `blhost` recovery.
Do not perform on hardware you cannot afford to lose. This step does
**not** produce or require malicious firmware.

## 10. Recommended remediation

1. **Sign firmware images** (asymmetric signature over the plaintext
   image); **verify on the host before flashing** *and*, independently,
   **in the device bootloader before committing** (defence in depth).
2. Replace **ECB** with an **authenticated** construction (AEAD, or
   encrypt-then-MAC) so tampering is detectable even with the key.
3. Add an **explicit user confirmation** step before any flash, showing
   current vs target version; make "cancel" actually abort.
4. **Verify the download**: pin the firmware host; validate a published
   hash/signature out of band; fail closed on mismatch.
5. Enable the MCU's **secure boot** (signed-image enforcement) and
   **anti-rollback** (monotonic version) on the NXP models.

## 11. Coordinated-disclosure terms (proposed)

Private report to a DrunkDeer security contact. _(Locate one: a
`security@` address, a `SECURITY.md` / advisory page, or product
support. If none exists, that absence is itself a finding worth stating,
and a national CERT or a coordinator can broker contact.)_ Proposed
embargo: **90 days** from acknowledgement, or until a fix ships,
whichever is sooner, before any public write-up. **No public
proof-of-concept** will be released. The reporter offers to validate
fixes at no cost.

## 12. Appendix A — endpoint probe matrix (2026-05-19, read-only)

| `search_name` | release | beta |
|---|---|---|
| `drunkdeera75pro` | 404 — device known, no firmware | 404 |
| `drunkdeera75` | 404 — device known, no firmware | 404 |
| `drunkdeerg65` | 404 — device known, no firmware | 404 |
| `drunkdeerg60` | 404 — device known, no firmware | 404 |
| `drunkdeera75ultra` | **200** `A75Ultra.bin` 134 232 B | 200 134 224 B |
| `drunkdeera75master` | **200** `A75Master.bin` 134 232 B | 200 134 224 B |
| `drunkdeerx60future` | **200** LPC55S28 image 140 096 B | 200 140 104 B |
| `drunkdeerg75` / `g75jp` / `g65lite` / `a75ios_uk` | 404 — "device not found" | 404 |

Two 404 classes: 60 B `{"error":"没有当前设备当前分类的更新程序。"}`
("device recognised, no program for this category"); 33 B
`{"error":"找不到该设备。"}` ("device not found").

## 13. Appendix B — what was deliberately NOT done

To keep this strictly within responsible-research bounds:

- No firmware was flashed to any device by the analyst.
- **No malicious firmware, keystroke-injection payload, implant, or
  modified executable image was created.**
- **No web-delivery / drive-by page was built**; the impact in §6 is
  described, not implemented.
- No attempt was made to extract or recover the on-device `.enc`
  decryption key.
- The only network activity was a small set of authorised read-only
  `GET`s to the public firmware endpoint (no auth, no writes).
- The optional §9 hardware step is an integrity oracle on
  researcher-owned hardware using vendor tooling and an inert
  modification — it is not, and does not require, an exploit.

---

*Prepared from static analysis of publicly available artifacts. Send the
finalised version privately to the vendor; do not publish until
vendor-coordinated per §11.*
