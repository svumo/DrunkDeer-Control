#!/usr/bin/env python3
"""
DrunkDeer second-stage bootloader (DualBankBoot.bin) — crypto-capability scan.

Purpose
-------
Answer the device-side question for the NXP `.bin` update path:
*does DrunkDeer's second-stage bootloader perform any cryptographic
verification of the app image before committing it?*

Static, offline. Reads a publicly-distributed binary. Cannot connect
to or modify any device. Outputs a structured report to stdout and
exits 0 when no cryptographic capability is found (the expected
"vulnerability confirmed" result), 1 otherwise.

What this checks
----------------
1. Known crypto-primitive constants (AES S-boxes, SHA-256/SHA-1/MD5
   IVs and round constants, CRC32 polynomial + table, RSA exponent
   65537, NIST P-256 prime, Curve25519 basepoint).
2. References to NXP on-chip crypto peripheral MMIO (HASHCRYPT,
   CASPER, PUF, PRINCE) and flash-controller MMIO (LPC55 / Kinetis
   FTFx).
3. Thumb-2 bignum-multiply opcode density (UMULL/UMLAL ≈ 0xFBA./0xFBE.)
   as a hint for RSA/ECC routines.
4. 256-byte sliding-window entropy to flag any embedded high-entropy
   key/cert/signature blob.
5. ASCII strings filtered for security/boot/version vocabulary.

The interpretation rule is simple: a bootloader cannot verify what it
has no primitive to compute. If items 1-4 are all clean and the
strings show only erase/program/jump vocabulary, the bootloader is
incapable of signature/hash verification of the app image. Combined
with the imageType=0 finding in `firmware-security-verify.py` and the
plain `blhost write-memory` command extracted from
`DrunkdeerUpdater.exe`, this closes the device-side question
statically.

Usage
-----
    python bootloader-crypto-scan.py <path/to/DualBankBoot.bin> [more.bin ...]

If no paths are given and `tools/updater_bundle/` exists, both bundled
copies (A75_master, A75_ultra) are analysed and compared.
"""

from __future__ import annotations
import sys, os, struct, math, re, hashlib, glob
from pathlib import Path

# ---- crypto fingerprints --------------------------------------------------

AES_SBOX = bytes([0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,
                  0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76])
AES_INV  = bytes([0x52,0x09,0x6a,0xd5,0x30,0x36,0xa5,0x38,
                  0xbf,0x40,0xa3,0x9e,0x81,0xf3,0xd7,0xfb])
SHA256_H0_LE = struct.pack("<2I", 0x6a09e667, 0xbb67ae85)
SHA256_H0_BE = struct.pack(">2I", 0x6a09e667, 0xbb67ae85)
SHA256_K_BE  = struct.pack(">4I", 0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5)
SHA256_K_LE  = struct.pack("<4I", 0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5)
SHA1_H_BE    = struct.pack(">5I", 0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0)
SHA1_K0_LE   = struct.pack("<I", 0x5a827999)
SHA1_K0_BE   = struct.pack(">I", 0x5a827999)
MD5_INIT     = struct.pack("<4I", 0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476)
CRC32_POLY_R = struct.pack("<I", 0xedb88320)     # reflected
CRC32_T1     = struct.pack("<I", 0x77073096)     # standard table[1]
CRC32_POLY_F = struct.pack("<I", 0x04c11db7)     # forward
RSA_EXP_LE   = b"\x01\x00\x01\x00"
RSA_EXP_BE   = b"\x00\x01\x00\x01"
P256_PRIME   = bytes.fromhex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff")
CURVE25519_9 = b"\x09" + b"\x00" * 31

PRIMITIVES = [
    ("AES forward S-box",          AES_SBOX),
    ("AES inverse S-box",          AES_INV),
    ("SHA-256 H0 (LE)",            SHA256_H0_LE),
    ("SHA-256 H0 (BE)",            SHA256_H0_BE),
    ("SHA-256 K[0..3] (BE)",       SHA256_K_BE),
    ("SHA-256 K[0..3] (LE)",       SHA256_K_LE),
    ("SHA-1 H (BE)",               SHA1_H_BE),
    ("SHA-1 K0 0x5A827999 (LE)",   SHA1_K0_LE),
    ("SHA-1 K0 0x5A827999 (BE)",   SHA1_K0_BE),
    ("MD5 init",                   MD5_INIT),
    ("CRC32 reflected poly",       CRC32_POLY_R),
    ("CRC32 table[1] 0x77073096",  CRC32_T1),
    ("CRC32 forward poly",         CRC32_POLY_F),
    ("RSA exp 65537 (LE)",         RSA_EXP_LE),
    ("RSA exp 65537 (BE)",         RSA_EXP_BE),
    ("NIST P-256 prime",           P256_PRIME),
    ("Curve25519 basepoint 9",     CURVE25519_9),
]

PERIPHERALS = [
    ("HASHCRYPT (LPC55 HW AES/SHA)", 0x400A4000),
    ("CASPER (LPC55 HW bignum)",     0x400A5000),
    ("PUF (LPC55)",                  0x4003A000),
    ("PRINCE (LPC55 on-the-fly)",    0x4003B000),
    ("ROM API table (LPC55)",        0x1301FE00),
    ("FLASH controller (LPC55)",     0x40034000),
    ("Kinetis FTFx flash",           0x40020000),
]

STRING_KW = re.compile(
    r"(verif|sign|sha|rsa|ecdsa|ecc|crc|hash|cert|secure|encrypt|decrypt|aes|"
    r"invalid|fail|error|boot|version|rollback|update|flash|erase|drunk|dual|bank|app)",
    re.I,
)


def entropy(b: bytes) -> float:
    if not b:
        return 0.0
    f = [0] * 256
    for x in b:
        f[x] += 1
    n = len(b)
    return -sum((c / n) * math.log2(c / n) for c in f if c)


def analyse(path: Path) -> dict:
    d = path.read_bytes()
    result = {
        "path": str(path),
        "size": len(d),
        "sha256": hashlib.sha256(d).hexdigest(),
        "primitive_hits": [],
        "peripheral_refs": [],
        "umull_density": 0,
        "entropy_whole": 0.0,
        "high_entropy_windows": [],
        "strings": [],
        "vectors": None,
    }
    if len(d) >= 64:
        v = struct.unpack("<16I", d[:64])
        result["vectors"] = {"SP": v[0], "Reset": v[1], "NMI": v[2], "HardFault": v[3]}
    for label, pat in PRIMITIVES:
        i = d.find(pat)
        if i >= 0:
            result["primitive_hits"].append((label, i))
    for label, addr in PERIPHERALS:
        i = d.find(struct.pack("<I", addr))
        if i >= 0:
            result["peripheral_refs"].append((label, addr, i))
    # Thumb-2 UMULL/UMLAL: 0xFBA./0xFBE. (rough halfword count)
    result["umull_density"] = len(re.findall(rb"[\xa0-\xaf\xe0-\xef]\xfb", d))
    result["entropy_whole"] = entropy(d)
    for off in range(0, max(0, len(d) - 256), 256):
        e = entropy(d[off:off + 256])
        if e >= 7.0:
            result["high_entropy_windows"].append((off, round(e, 2)))
    strs = set(s.decode("latin1") for s in re.findall(rb"[\x20-\x7e]{4,}", d))
    result["strings"] = sorted(s for s in strs if STRING_KW.search(s) and len(s) <= 60)
    return result


def report(r: dict) -> int:
    print(f"=== {r['path']}")
    print(f"    size={r['size']}  sha256={r['sha256']}")
    if r["vectors"]:
        v = r["vectors"]
        print(f"    SP=0x{v['SP']:08X}  Reset=0x{v['Reset']:08X}  "
              f"NMI=0x{v['NMI']:08X}  HardFault=0x{v['HardFault']:08X}")
    print(f"\n[crypto primitive fingerprints: {len(r['primitive_hits'])}/{len(PRIMITIVES)}]")
    if r["primitive_hits"]:
        for lbl, off in r["primitive_hits"]:
            print(f"    HIT  {lbl}  @0x{off:X}")
    else:
        print("    none found  ->  bootloader cannot hash, sign, or verify")
    print(f"\n[NXP peripheral / flash MMIO references]")
    for lbl, addr, off in r["peripheral_refs"]:
        print(f"    {lbl:34s}  0x{addr:08X}  @0x{off:X}")
    if not r["peripheral_refs"]:
        print("    none")
    print(f"\n[Thumb-2 bignum-multiply density]")
    print(f"    UMULL/UMLAL-like halfwords: {r['umull_density']}  "
          f"(RSA/ECC needs hundreds; ~0 => no bignum)")
    print(f"\n[entropy]")
    print(f"    whole-file: {r['entropy_whole']:.3f}")
    if r["high_entropy_windows"]:
        print(f"    >=7.0 256B windows (possible key/cert/sig blob): "
              f"{r['high_entropy_windows']}")
    else:
        print("    no >=7.0 windows  ->  no embedded key, cert, or signature")
    print(f"\n[security/boot strings ({len(r['strings'])})]")
    for s in r["strings"]:
        print(f"    {s!r}")
    crypto_clean = (not r["primitive_hits"]) and (not r["high_entropy_windows"])
    no_hw_crypto = not any(addr in (0x400A4000, 0x400A5000, 0x4003A000, 0x4003B000)
                           for _, addr, _ in r["peripheral_refs"])
    verdict = "NO CRYPTO CAPABILITY DETECTED" if (crypto_clean and no_hw_crypto) \
              else "CRYPTO CAPABILITY FOUND -- review"
    print(f"\n=> {verdict}\n")
    return 0 if (crypto_clean and no_hw_crypto) else 1


def main(argv: list[str]) -> int:
    if argv:
        paths = [Path(p) for p in argv]
    else:
        here = Path(__file__).resolve().parent
        guesses = [
            here / "updater_bundle" / "A75_master" / "DualBankBoot.bin",
            here / "updater_bundle" / "A75_ultra"  / "DualBankBoot.bin",
        ]
        paths = [p for p in guesses if p.exists()]
        if not paths:
            sys.stderr.write(
                "usage: bootloader-crypto-scan.py <DualBankBoot.bin> [more.bin ...]\n"
                "  or place bundled copies under tools/updater_bundle/A75_{master,ultra}/.\n"
            )
            return 2
    rc = 0
    for p in paths:
        if not p.exists():
            sys.stderr.write(f"missing: {p}\n"); rc = 2; continue
        rc |= report(analyse(p))
    if len(paths) == 2:
        a, b = paths[0].read_bytes(), paths[1].read_bytes()
        print(f"[cross-check] {paths[0].name} == {paths[1].name} : {a == b}")
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
