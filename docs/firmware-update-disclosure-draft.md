# Coordinated Vulnerability Disclosure — DRAFT (not for public release)

**Status:** internal draft for private vendor disclosure
**Date:** 2026-05-19
**Reporter:** _(fill in)_
**Vendor:** DrunkDeer
**Components:** Antler web driver (`drunkdeer-antler.com`), firmware
endpoint (`api.drunkdeer.club`), native `DrunkdeerUpdater` (v2.3.1
analysed)

> This document describes a weakness and how to *verify* it on
> researcher-owned hardware. It contains **no exploit, no malicious
> firmware, and no delivery mechanism**, by design. Do not attach a
> working drive-by to a disclosure — describe impact in prose.

---

## 1. Summary

The DrunkDeer firmware-update flow performs **no host-side
cryptographic verification** of the firmware image before flashing it to
the keyboard, and the web driver **begins flashing automatically with no
explicit user confirmation** once its upgrade modal is shown. The
update surface is reachable from a web page via WebHID after a single
per-device permission grant.

If the device bootloader does not itself authenticate the image
(undetermined by static analysis — see §5), this allows a
network-positioned or web-positioned actor to flash arbitrary firmware
to a keyboard. Malicious keyboard firmware is a persistent host
compromise primitive (a keyboard can inject arbitrary keystrokes),
making this potentially a high-severity issue. Severity is therefore
**conditional on §5** and stated as a range in §6.

## 2. Affected components

- **Antler web driver** — `UpgradeModal` component: on mount it fetches
  firmware and, on a successful (HTTP 200) response, immediately calls
  `setUpgradeFirmwareData()` + `upgradeFirmwareStart()`. No "confirm
  update?" step. The only visible control is a button labelled
  「手动更新」 ("manual update") that closes the modal but does not abort
  an in-progress flash.
- **Firmware endpoint** — `GET https://api.drunkdeer.club/desktop/
  update_programs/download.json?search_name=<name>&category=release|beta`.
  Response body is the raw firmware binary, streamed verbatim to the
  device. (Currently serves images only for the NXP-bootloader models;
  the encrypted-HID-app models return 404 — but the *client code path*
  is present and active.)
- **Native `DrunkdeerUpdater` v2.3.1** — same lack of host-side
  verification (see evidence in §3).

## 3. Technical findings (static, offline analysis)

Full method and artefacts: see `docs/firmware-updater-assessment.md`.

1. **No host-side crypto.** The native `UsbHid_v1.2.7.dll` imports only
   registry functions from ADVAPI32 (zero `Crypt*`); no
   `bcrypt`/`ncrypt`/OpenSSL/mbedtls linked anywhere; no AES S-box,
   SHA-256 IV, or CRC32 table compiled in. The web driver JS contains
   zero `crypto.subtle`/`digest`/`SHA`/`AES`/`decrypt`/`sign`/`crc32`
   usage. The downloaded image is **not** decrypted, hashed, or
   signature-checked on the host — it is passed through unmodified.
2. **Confidentiality without integrity.** The encrypted (`.enc`) images
   are a 16-byte block cipher in **ECB mode** (entropy ≈ 7.65; one
   16-byte ciphertext block repeats 1285×). ECB provides confidentiality
   only — **no integrity and no authenticity**. Encryption here is not
   evidence that tampered or forged images are rejected.
3. **Automatic flash, no confirmation (web).** The upgrade modal's
   `onMounted` hook runs the fetch-and-flash routine directly. The only
   user interaction is whatever opened the modal; there is no in-flow
   consent step.
4. **Web-reachable surface.** WebHID exposes the flash path to any
   origin the user grants device access to. This is gated by an explicit
   browser device-picker grant (not zero-click) but is socially
   engineerable ("update your keyboard").
5. **Firmware images carry no authenticity data (NXP `.bin` path).**
   Structural analysis of the actual served + bundled images for the
   NXP-bootloader models (A75 Ultra, A75 Master, X60, and the shipped
   second-stage `DualBankBoot.bin`): the NXP signed-image header at
   offsets `0x20`/`0x24`/`0x28` (imageLength / imageType /
   cert-block-offset) is **all-zero** → `imageType = 0` ("plain, no
   CRC, no signature"). No legacy vector-table checksum (sum of the
   first 8 vectors ≠ 0). No appended signature trailer (last-256-byte
   entropy ≈ 3.8–4.8 = ordinary ARM code, not a ~7.9+ RSA/ECDSA blob).
   The native updater flashes them with plain `blhost write-memory` to
   raw addresses — **not** `receive-sb-file` (NXP's signed/encrypted
   container). **Deduction (no hardware needed):** an NXP secure-boot-
   enabled part will not boot an unsigned `imageType = 0` image — it
   requires the signed header + certificate block these images lack.
   Since these are the production images and the devices run them,
   on-device secure boot is almost certainly **disabled**. The only
   residual unknown is the device's secure-boot **fuse state**, and the
   image format itself already strongly indicates it is off.

## 4. Impact / threat model

Malicious keyboard firmware can emulate arbitrary keystrokes, yielding
attacker-controlled command execution on the host, persistence that
survives OS reinstallation, and portability to any machine the keyboard
is later connected to. Reachability: a malicious or compromised page (or
a compromised firmware endpoint / TLS-MITM position) that obtains a
WebHID grant could drive the existing, confirmation-free flash path.
This is the standard "malicious USB-HID firmware" class, made
web-reachable.

## 5. What is NOT proven (residual unknown — now narrow)

Two paths, very different residual uncertainty:

- **NXP `.bin` path (A75 Ultra / Master / X60):** §3.5 narrows this from
  "unknown" to a single residual question — the device's secure-boot
  **fuse state** — and the image structure already strongly implies it
  is off (a secure-boot device cannot run the unsigned `imageType = 0`
  images that ship and run today). Confidence that this path accepts
  arbitrary firmware is **high on static evidence alone**; the hardware
  test below is final fuse confirmation, not the sole basis.
- **Encrypted `.enc` path (A75 Pro / A75 / G65 / G60):** still genuinely
  indeterminate. Decryption necessarily occurs on-device (host ships
  ciphertext; ciphertext cannot execute), but whether the device
  *authenticates* is unknowable statically, and an image cannot even be
  forged without the on-device key. Not the practical attack surface;
  report on §3 evidence, do not overstate.

**Non-destructive verification (researcher-owned hardware only):** on a
plaintext-`.bin` model the researcher owns, take the vendor's
known-good image, apply a *benign, functionally inert* modification
(flip one byte in a padding/unused region — **no injected code, no
payload**), attempt the **official** update path, and observe whether
the bootloader **rejects** the modified image (would indicate an
authenticity check) or accepts/boots it (confirms missing authenticity
and resolves the §5 fuse-state question). Note brick risk; rely on the
device's dual-bank / `blhost` recovery path; this is **optional** —
§3 (incl. §3.5) already justifies the report without it.

## 6. Severity

- **NXP `.bin` models (A75 Ultra / Master / X60): High / Critical.**
  Static evidence (no host check, no signature/cert/CRC in the image,
  plain `write-memory`, image format incompatible with on-device secure
  boot) makes web-reachable persistent host compromise the strongly
  indicated case. Present as High with the §3.5 structural basis;
  the optional hardware test in §5 upgrades it from "strongly indicated"
  to "confirmed."
- **Encrypted `.enc` models (A75 Pro / A75 / G65 / G60): conditional /
  informational** — host-side gaps and the ECB-without-integrity choice
  are real defence-in-depth/UX issues, but device-side authenticity is
  indeterminate and no public forgeable image exists. Do not assert the
  high case here.

Do not overstate the `.enc` path; do not understate the `.bin` path.

## 7. Recommended remediation

1. **Sign firmware images** (asymmetric signature over the plaintext
   image) and **verify the signature on the host before flashing** and,
   independently, **in the device bootloader before committing**.
2. Replace **ECB** with an **authenticated** construction (AEAD, or
   encrypt-then-MAC) so tampering is detectable even with the key.
3. Add an **explicit user confirmation** step before any flash, showing
   current vs target version; ensure "cancel" actually aborts.
4. **Verify the download**: pin the firmware host, validate a published
   hash/signature out of band, fail closed on mismatch.
5. Consider **anti-rollback** (monotonic version) to block downgrade to
   known-vulnerable firmware.

## 8. Disclosure terms (proposed)

Private report to DrunkDeer security contact. _(Identify a contact:
security@ / product support / `SECURITY.md` / advisory page; if none
exists, that absence is itself worth noting and a public coordinator
(e.g. a CERT) can broker.)_ Standard timeline (e.g. 90 days) before any
public write-up. **No public proof-of-concept**; impact is described in
prose. Reporter offers to assist with and validate fixes at no cost.

---

*Evidence basis: static analysis in `docs/firmware-updater-assessment.md`
(PE import tables, entropy/cipher-mode analysis, JS extraction) plus the
2026-05-19 read-only endpoint probe. No malicious artefact was produced.*
