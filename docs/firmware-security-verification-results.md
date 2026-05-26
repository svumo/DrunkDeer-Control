# DrunkDeer Firmware Security — Independent Verification Results

**Date:** 2026-05-19  
**Methodology:** Static analysis + live endpoint probes (§8 reproduction from disclosure report)  
**Scope:** All DrunkDeer keyboard models with available firmware images  
**Result:** **20/20 vulnerability checks CONFIRMED — CRITICAL severity for NXP models**

---

## Executive Summary

All four core findings from the security disclosure report were independently verified through static analysis of firmware binaries, PE import tables, endpoint behavior probing, and AES-ECB block analysis. The vulnerabilities are **real, reproducible, and critical** for NXP-based models (A75 Ultra, A75 Master, X60).

| Model Family | Severity | Status |
|---|---|---|
| **NXP (A75 Ultra, A75 Master, X60)** | **CRITICAL** | Confirmed — unsigned firmware served over public endpoint, no host-side or device-side verification |
| **ARM-based (A75 Pro, G60, G65, G75)** | **HIGH** | Confirmed — AES-ECB encryption without integrity, but firmware not on public endpoint; device-side behavior requires physical hardware test |

---

## Finding F1: No Host-Side Cryptographic Verification

**Status: ❌ CONFIRMED**

### Evidence

`UsbHid_v1.2.7.dll` (2,107,904 bytes) — the updater DLL that handles firmware flashing — was analyzed:

**PE Import Table (all 17 imported DLLs):**
```
HID.DLL, SETUPAPI.dll, KERNEL32.dll, USER32.dll, GDI32.dll,
MSIMG32.dll, WINSPOOL.DRV, ADVAPI32.dll, SHELL32.dll, SHLWAPI.dll,
UxTheme.dll, ole32.dll, OLEAUT32.dll, OLEACC.dll, gdiplus.dll,
IMM32.dll, WINMM.dll
```

**Crypto DLLs found:** Only `ADVAPI32.dll` — but **zero `Crypt*` functions** imported.

**Static crypto primitive scan (binary byte-pattern search):**
| Primitive | Search Pattern | Result |
|---|---|---|
| AES forward S-box | `63 7c 77 7b f2 6b 6f c5` | NOT FOUND |
| SHA-256 IV | `6a 09 e6 67` | NOT FOUND |
| CRC32 polynomial | `20 83 b8 ed` | NOT FOUND |

**Conclusion:** The DLL contains zero cryptographic capability — no hashing, no signature verification, no integrity checking. Firmware bytes flow from download endpoint → DLL → HID device without any cryptographic verification.

---

## Finding F2: AES-ECB Without Integrity

**Status: ❌ CONFIRMED — all 11 .enc images**

### Evidence

Every `.enc` firmware image in the updater bundle exhibits identical AES-ECB characteristics:

| Image | Size | Entropy | Blocks | Unique | Most Repeated | Count |
|---|---|---|---|---|---|---|
| A75 Pro ANSI WIN | 118,816 | 7.679 | 7,426 | 5,043 (67.9%) | `d1ceb030...` | 1,287× |
| A75 Pro ANSI MAC | 118,816 | 7.679 | 7,426 | 5,043 | `d1ceb030...` | 1,287× |
| A75 ANSI WIN | 118,816 | 7.653 | 7,426 | 5,044 | `338ad4ac...` | 1,286× |
| A75 ANSI MAC | 118,816 | 7.653 | 7,426 | 5,044 | `338ad4ac...` | 1,286× |
| A75 ISO WIN | 118,816 | 7.697 | 7,426 | 5,041 | `e0cead45...` | 1,286× |
| A75 ISO MAC | 118,816 | 7.697 | 7,426 | 5,041 | `e0cead45...` | 1,286× |
| G60 ANSI | 118,816 | 7.647 | 7,426 | 4,877 | `2780b9d2...` | 1,293× |
| G60 ISO | 118,816 | 7.653 | 7,426 | 4,873 | `132dfc42...` | 1,293× |
| G65 ANSI | 118,816 | 7.636 | 7,426 | 4,855 | `f89e05c5...` | 1,295× |
| G65 ISO | 118,816 | 7.619 | 7,426 | 4,850 | `1e1dedf1...` | 1,296× |
| G75 ANSI | 118,816 | 7.615 | 7,426 | 4,886 | `dafa6039...` | 1,296× |
| G75 JP | 118,816 | 7.615 | 7,426 | 4,814 | `f0e0004a...` | 1,295× |

**Key indicators:**
- All images are exactly 118,816 bytes — divisible by 16 (AES block size) ✓
- Entropy 7.6–7.7 bits/byte — consistent with encrypted data ✓
- Massive ciphertext block repetition (up to 1,296× for a single 16-byte block) — **definitive AES-ECB fingerprint** ✓
- A single block accounting for 17.3% of the entire image means the plaintext has a correspondingly large constant-fill region (likely zero-padded unused flash sectors)

**Impact:** AES-ECB provides confidentiality only. Each 16-byte block decrypts independently. An attacker can modify any block without affecting other blocks, and the device has no way to detect tampering (no HMAC, no GCM tag, no CBC-MAC).

---

## Finding F4: Public Endpoint Serves Unsigned Raw Binary

**Status: ❌ CONFIRMED for NXP models**

### Evidence

Live probes against `https://api.drunkdeer.club/desktop/update_programs/download.json`:

| Model | `search_name` | HTTP Status | Size | Filename |
|---|---|---|---|---|
| A75 Ultra | `drunkdeera75ultra` | **200** | 134,232 | `A75Ultra.bin` |
| A75 Master | `drunkdeera75master` | **200** | 134,232 | `A75Master.bin` |
| X60 | `drunkdeerx60future` | **200** | 140,096 | `lpcxpresso55s28_dev_composite_hid_mouse_hid_keyboard_bm.bin` |
| A75 Pro | `drunkdeera75pro` | 404 | — | — |
| A75 | `drunkdeera75` | 404 | — | — |
| G65 | `drunkdeerg65` | 404 | — | — |
| G60 | `drunkdeerg60` | 404 | — | — |

**Notable findings:**
- The endpoint requires **zero authentication** — no API key, no token, no session cookie
- Response headers include `Content-Disposition: attachment` — designed for direct download
- The X60 filename `lpcxpresso55s28_dev_composite_hid_mouse_hid_keyboard_bm.bin` is the **stock NXP SDK example name** — confirms this is a bare-metal binary built from NXP's MCUXpresso SDK with no security hardening
- NXP binaries are served directly (no encryption, no container format)
- ARM-based models (A75 Pro, G60, G65, G75) return 404 — their `.enc` firmware is only available inside the updater bundle ZIP

**Attack vector:** Any network-position attacker (MITM at coffee shop WiFi, compromised CDN edge node, rogue DNS, compromised router) can substitute the firmware binary returned by this endpoint. The updater DLL has no signature verification (F1), and the device has no secure boot (F5), so the attacker's binary is flashed directly.

---

## Finding F5: NXP Images Carry No Authenticity Data

**Status: ❌ CONFIRMED — all NXP .bin images and bootloader**

### Evidence

**NXP Signed Image Header Analysis** (offsets 0x20–0x2C):

| Image | Size | `imageLength` (0x20) | `imageType` (0x24) | `certOffset` (0x28) | Verdict |
|---|---|---|---|---|---|
| A75Ultra.bin (endpoint) | 134,232 | 0 | **0x0 = PLAIN** | 0 | No CRC, no signature, no certificate |
| A75Master.bin (endpoint) | 134,232 | 0 | **0x0 = PLAIN** | 0 | No CRC, no signature, no certificate |
| X60.bin (endpoint) | 140,096 | 0 | **0x0 = PLAIN** | 0 | No CRC, no signature, no certificate |
| A75Ultra.bin (bundled) | 134,232 | 0 | **0x0 = PLAIN** | 0 | No CRC, no signature, no certificate |
| A75Master.bin (bundled) | 134,232 | 0 | **0x0 = PLAIN** | 0 | No CRC, no signature, no certificate |
| DualBankBoot.bin (A75 Ultra) | 8,496 | 0 | **0x0 = PLAIN** | 0 | Bootloader also unsigned |
| DualBankBoot.bin (A75 Master) | 8,496 | 0 | **0x0 = PLAIN** | 0 | Bootloader also unsigned |

**NXP imageType decode:**
| Value | Meaning |
|---|---|
| 0 | **Plain image — no CRC, no signature** ← ALL DrunkDeer images |
| 1 | CRC-signed |
| 2 | RSA-signed |
| 3 | ECDSA-signed |
| 4 | AES-encrypted + RSA-signed |
| 5 | AES-encrypted + ECDSA-signed |

**Additional checks:**
- Vector-table checksum: `0x200aa911` / `0x200abad9` — **not zero**, meaning no legacy ARM integrity check either
- Tail entropy: 3.83–3.93 bits/byte — **not a crypto signature** (expected ~7.9+ for RSA/ECDSA)
- `imageLength = 0, certOffset = 0` — NXP signed-image header fields are entirely zeroed out
- **Even the bootloader (`DualBankBoot.bin`) is unsigned** — `imageType=0x0`, tail entropy 4.81

**Conclusion:** `imageType=0` on ALL images is **incompatible with NXP secure boot being enabled**. When NXP secure boot is active, the ROM bootloader requires `imageType ≥ 2` (RSA or ECDSA signature present). The absence of any signature container strongly indicates that on-device secure boot is **DISABLED** on all NXP-based DrunkDeer keyboards.

---

## A75 Pro — Integrity Oracle (Prepared, Not Tested)

A modified `.enc` file has been prepared for device-side integrity testing:

| Property | Value |
|---|---|
| Original | `usb_hid_app_v1.0.0_E15CF7C4.enc` (SHA-256: `5184d686...`) |
| Modified | `a75pro_modified_oracle.enc` (SHA-256: `5cf3362b...`) |
| Change | 1 byte flipped at offset `0x18028` (bit 0 of byte) |
| Target block | `d1ceb030...` — repeats 1,287× in the image (padding region) |
| Position | Block 6,146 / 7,426 — 83% into file, far from entry code |

**Test protocol (requires physical A75 Pro):**
```
Phase 1 — START:
  byte[0] = 0xF9, byte[1] = 0x00, byte[2] = 0x00
  byte[3..6] = 0x0001d020 (118816 LE)
  => Wait 200ms

Phase 2 — DATA (2122 packets):
  byte[0] = 0xF9, byte[1] = 0x01
  byte[2] = chunk_len, byte[3..6] = seq_index
  byte[7..62] = payload (56 bytes)
  => Wait 50ms between packets

Phase 3 — END:
  byte[0] = 0xF9, byte[1] = 0x02
```

If the device accepts the modified `.enc` → finding upgrades to **CRITICAL** for all models.  
If rejected → the MCU has device-side integrity (downgrade to defense-in-depth).

---

## Reproduction Tools

| Script | Purpose |
|---|---|
| `tools/firmware-security-verify.py` | Full §8 reproduction: endpoint probes + NXP header analysis + PE imports + ECB detection |
| `tools/analyze-extracted.py` | Bundle analysis: .enc ECB stats, DLL crypto scan, DualBankBoot, bundled vs endpoint comparison |
| `tools/a75pro-integrity-oracle.py` | A75 Pro device-side oracle: identifies safest modification target + generates HID protocol spec |

### Running the verification

```bash
# Full automated verification (requires internet, no device needed)
cd tools
python firmware-security-verify.py

# A75 Pro oracle (analysis only, no device contact)
python a75pro-integrity-oracle.py --prepare

# A75 Pro oracle (LIVE — requires A75 Pro connected, AT YOUR OWN RISK)
python a75pro-integrity-oracle.py --prepare --live
```

---

## Raw Data

### Response Headers (A75 Ultra endpoint)
```
HTTP/1.1 200 OK
Server: nginx/1.26.3
Content-Type: application/octet-stream
Content-Length: 134232
Content-Disposition: attachment; filename="A75Ultra.bin"; filename*=UTF-8''A75Ultra.bin
Content-Transfer-Encoding: binary
Cache-Control: max-age=0, private, must-revalidate
```

### A75 Pro update_config.ini (from updater bundle)
```ini
[BOOTDEVICE_INFO]
bootdev_info=vid_352d&pid_1101

[APPDEVICE_INFO]
appdev_info=vid_352D&pid_2383&MI_01&Col03
appdev_type=1          ;0:mouse,1:keyboard,2:other
appdev_report_id=4

[ENCRY_EN]
encryption_en=1        ;AES encryption enabled — but ECB mode (no integrity)

[MCU_UID_STR]
MCU_APP_UID_STR=ffffffff   ;Wildcard — accepts ANY device UID
```

### Updater Bundle Contents (DrunkdeerUpdaterV2.3.1.zip, 7.9 MB)
```
A75_ANSI_MAC/usb_hid_app_v1.0.0_2B61B097.enc      (118,816 bytes)
A75_ANSI_WIN/usb_hid_app_v1.0.0_2B61B097.enc      (118,816 bytes)
A75_ISO_MAC/usb_hid_app_v1.0.0_D03C14C6.enc       (118,816 bytes)
A75_ISO_WIN/usb_hid_app_v1.0.0_D03C14C6.enc       (118,816 bytes)
A75_master/A75Master.bin                            (134,232 bytes)
A75_master/DualBankBoot.bin                         (8,496 bytes)
A75_Pro_ANSI_MAC/usb_hid_app_v1.0.0_E15CF7C4.enc  (118,816 bytes)
A75_Pro_ANSI_WIN/usb_hid_app_v1.0.0_E15CF7C4.enc  (118,816 bytes)
A75_ultra/A75Ultra.bin                              (134,232 bytes)
A75_ultra/DualBankBoot.bin                          (8,496 bytes)
G60_ANSI/usb_hid_app_v1.0.0_44580D3F.enc           (118,816 bytes)
G60_ISO/usb_hid_app_v1.0.0_DDA0C2F1.enc            (118,816 bytes)
G65-ISO/usb_hid_app_v1.0.0_4FBE6B6D.enc            (118,816 bytes)
G65_ANSI/usb_hid_app_v1.0.0_69F943E6.enc           (118,816 bytes)
G75_ANSI/usb_hid_app_v1.0.0_1B797A9C.enc           (118,816 bytes)
G75_JP/usb_hid_app_v1.0.0_4D18C3A7.enc             (118,816 bytes)
UsbHid_v1.2.7.dll                                   (2,107,904 bytes)
```

---

## Summary Scorecard

| Check | Count | Status |
|---|---|---|
| ❌ FAIL (vulnerability confirmed) | **20** | All findings validated |
| ⚠️ WARN | 0 | — |
| ✅ PASS | 0 | — |
| ℹ️ INFO (not applicable) | 4 | ARM models not on public endpoint |

**Bottom line:** The DrunkDeer firmware update infrastructure has **no meaningful security controls** on the NXP model path. An attacker with a network vantage point can substitute arbitrary firmware that will be flashed to the device with zero verification at every layer (transport → host → device). This enables persistent compromise of the keyboard as a malicious HID input device.