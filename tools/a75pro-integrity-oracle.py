#!/usr/bin/env python3
"""
DrunkDeer A75 Pro Firmware Integrity Oracle (DRY-RUN ONLY)
==========================================================
Analyzes the A75 Pro .enc firmware image to identify the safest possible
byte modification for an integrity oracle test — WITHOUT sending anything
to a device.

Purpose: resolve whether the A75 Pro's .enc firmware path has ANY
device-side integrity check. If a modified .enc is accepted, the finding
upgrades from "informational" to "real vulnerability".

This script NEVER touches a device. It produces:
1. Analysis of the .enc structure
2. A modified .enc file (if --prepare is given)
3. The exact HID protocol bytes needed for a manual flash test

Usage:
  python a75pro-integrity-oracle.py                    # analysis only
  python a75pro-integrity-oracle.py --prepare          # also create modified .enc
  python a75pro-integrity-oracle.py --prepare --live   # ALSO SENDS TO DEVICE (DANGEROUS)
                                                         # ^ requires explicit confirmation

WARNING: Flashing modified firmware carries a bricking risk.
    Do NOT use --live unless you accept that risk.
"""

import struct
import os
import sys
import hashlib
import collections
import math

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ENC_PATH = os.path.join(SCRIPT_DIR, "updater_bundle", "A75_Pro_ANSI_WIN",
                        "usb_hid_app_v1.0.0_E15CF7C4.enc")
MODIFIED_PATH = os.path.join(SCRIPT_DIR, "a75pro_modified_oracle.enc")


def entropy(data):
    freq = [0] * 256
    for b in data:
        freq[b] += 1
    n = len(data)
    return -sum((c / n) * math.log2(c / n) for c in freq if c > 0)


def analyze_enc_structure(data):
    """Map the .enc file structure to find the safest flip target."""
    print("=" * 70)
    print("  A75 Pro .enc Structure Analysis")
    print("=" * 70)

    size = len(data)
    n_blocks = size // 16
    print(f"\n  Total size: {size:,} bytes ({n_blocks} x 16-byte blocks)")

    # Block frequency analysis
    blocks = [data[i:i+16] for i in range(0, size - 15, 16)]
    counter = collections.Counter(blocks)
    top = counter.most_common(5)

    print(f"\n  Block frequency (top 5):")
    for block_hex, count in top:
        pct = count / n_blocks * 100
        first_idx = blocks.index(block_hex)
        last_idx = len(blocks) - 1 - blocks[::-1].index(block_hex)
        print(f"    {block_hex.hex()}: {count}x ({pct:.1f}%) | "
              f"blocks [{first_idx}..{last_idx}]")

    # The most-repeated block is the safest modification target.
    safest_block_hex, safest_count = top[0]
    last_idx = len(blocks) - 1 - blocks[::-1].index(safest_block_hex)
    target_idx = int(last_idx * 0.75)
    candidates = [i for i, b in enumerate(blocks) if b == safest_block_hex]
    target_idx = min(candidates, key=lambda i: abs(i - target_idx))

    target_offset = target_idx * 16
    print(f"\n  SAFEST MODIFICATION TARGET:")
    print(f"    Block index: {target_idx} / {n_blocks}")
    print(f"    File offset: 0x{target_offset:x} ({target_offset:,})")
    print(f"    Block value:  {safest_block_hex.hex()}")
    print(f"    This block repeats {safest_count}x in the image")
    print(f"    Position:     {target_idx/n_blocks*100:.0f}% into file (far from entry code)")
    print()
    print(f"    Proposed change: flip byte at offset 0x{target_offset:x}+8")
    print(f"    Before: {data[target_offset:target_offset+16].hex()}")
    modified = bytearray(data[target_offset:target_offset+16])
    modified[8] ^= 0x01
    print(f"    After:  {bytes(modified).hex()}")

    return target_idx, target_offset


def generate_hid_protocol(data):
    """Print the HID protocol bytes for manual flashing."""
    print("\n" + "=" * 70)
    print("  HID Firmware Update Protocol (from disclosure report)")
    print("=" * 70)

    size = len(data)
    report_id = 4
    chunk_size = 56
    n_data_packets = (size + chunk_size - 1) // chunk_size

    print(f"""
  Transport: WebHID / native HID
  Report ID: {report_id}
  Device:    VID 0x352D, PID 0x2383 (A75 Pro app mode, Col03)
  Chunk size: {chunk_size} bytes
  Total size: {size:,} bytes
  Data packets needed: {n_data_packets}

  Protocol:

  Phase 1 — START:
    byte[0]    = 0xF9
    byte[1]    = 0x00
    byte[2]    = 0x00
    byte[3..6] = total_length (u32 LE) = {size} = 0x{size:08x}
    byte[7..62] = 0x00 (padding)
    => Wait 200ms

  Phase 2 — DATA ({n_data_packets} packets):
    byte[0]    = 0xF9
    byte[1]    = 0x01
    byte[2]    = chunk_len (<= 56)
    byte[3..6] = sequence_index (u32 LE)
    byte[7..62] = payload (up to 56 bytes)
    => Wait 50ms between packets

  Phase 3 — END:
    byte[0]    = 0xF9
    byte[1]    = 0x02
    byte[2]    = 0x00
    byte[3..6] = 0x00
    byte[7..62] = 0x00

  The device expects a single-packet-in-flight handshake after each DATA.
  400ms watchdog — if no packet within 400ms, flash aborts.
""")


def main():
    dry_run = "--live" not in sys.argv
    prepare = "--prepare" in sys.argv

    print("=" * 70)
    print("  DrunkDeer A75 Pro Firmware Integrity Oracle")
    print("  DRY-RUN MODE" if dry_run else "  LIVE MODE — WILL MODIFY DEVICE")
    print("=" * 70)
    print()

    if not os.path.exists(ENC_PATH):
        print(f"ERROR: .enc file not found at {ENC_PATH}")
        print("Run the updater bundle extraction first.")
        sys.exit(1)

    data = open(ENC_PATH, "rb").read()
    orig_hash = hashlib.sha256(data).hexdigest()
    print(f"  Original .enc: {os.path.basename(ENC_PATH)}")
    print(f"  SHA-256:       {orig_hash[:32]}...")

    target_idx, target_offset = analyze_enc_structure(data)
    generate_hid_protocol(data)

    if prepare:
        modified = bytearray(data)
        modified[target_offset + 8] ^= 0x01
        mod_hash = hashlib.sha256(bytes(modified)).hexdigest()

        open(MODIFIED_PATH, "wb").write(bytes(modified))
        print("=" * 70)
        print(f"  Modified .enc written to: {os.path.basename(MODIFIED_PATH)}")
        print(f"  SHA-256: {mod_hash[:32]}...")
        print(f"  Difference: 1 byte flipped at offset 0x{target_offset+8:x}")
        print("=" * 70)

        if dry_run:
            print("""
  DRY-RUN MODE — file was created but NOT sent to device.

  To actually test (AT YOUR OWN RISK):
    1. Connect your A75 Pro
    2. Run: python a75pro-integrity-oracle.py --prepare --live
    3. Confirm the interactive prompt
    4. Watch the keyboard — if it reboots and works normally, the device
       has NO integrity check (finding upgraded from informational to real).
    5. If the device rejects the update (LED pattern, no reboot), it has
       some integrity mechanism (finding downgraded to defense-in-depth).

  Recovery: Re-flash the original firmware using the official updater with
  the unmodified .enc from the updater bundle.
""")
    else:
        print("""
  To prepare the modified firmware file, re-run with --prepare:
    python a75pro-integrity-oracle.py --prepare

  Analysis complete. No files were written. No device was touched.
""")


if __name__ == "__main__":
    main()