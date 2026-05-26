#!/usr/bin/env python3
"""Pull the per-model RGB layout-index table out of the Antler JS bundle.

The JS bundle is a single ~6 MB minified line — ripgrep silently mishandles
it (see antler-js-extraction-prep memory), so this script does straight
Python substring scans instead.

Wire format reminder (docs/rgb-protocol.md L80):
    Each per-key entry on the 0xAE/0x01 custom packet is
        [ 0x80 + layoutIndex, R, G, B ]
    where `layoutIndex` for each model comes from a `Q[s][k].value` lookup
    against a model-specific 2D table — that's what we're trying to find.

Strategy:
    1. Locate `sendTurboLedModeData` (JS line ~32325 per doc).
    2. Find the call site at JS ~20937 that passes modeIndex=19 and the
       per-model indices array. The variable name will be minified to
       something like `Sa` or `xC` — we walk neighbourhoods to recover it.
    3. Dump the table verbatim, then map it back to A75 Pro layout
       positions for human-readable verification.
"""
import re
import sys
from pathlib import Path

BUNDLE = Path(r"C:\Users\skdes\Downloads\antler-work\index.CJWCGjvj.live-2026-05-19.js")

def main():
    raw = BUNDLE.read_text(encoding="utf-8")
    print(f"loaded {len(raw):,} chars")

    # Anchor 1: sendTurboLedModeData. Builder for the custom-light packet.
    for needle in ("sendTurboLedModeData", "buildPkt_custom_led_mode_select",
                   "transmit_color_report_packet"):
        i = raw.find(needle)
        print(f"  needle {needle!r:50s} -> {'NOT FOUND' if i < 0 else f'offset {i:,}'}")

    # Anchor 2: literal `128+` patterns — uncommon enough to be RGB-specific.
    # Each push site looks like `w.push(128+<expr>, t, a, r)` per doc L77.
    hits = [m.start() for m in re.finditer(r"128\+", raw)]
    print(f"\n'128+' literal hits: {len(hits)}")
    for h in hits[:20]:
        print(f"  @ {h:,}: ...{raw[max(0, h-30):h+60]}...")


if __name__ == "__main__":
    main()
