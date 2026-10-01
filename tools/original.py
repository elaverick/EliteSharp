#!/usr/bin/env python3
"""
Reads the assembled BBC Master Elite binaries, for the tools that export the
original's data as EliteSharp's assets (tools/export_ships.py and
tools/render_sounds.py), and for checking them against the original.

It finds each label's address in the assembler listing, so the tools can lift
the original's data byte-for-byte with code_bytes (the main code, BCODE) and
data_bytes (the ship blueprints, BDATA).

The rest of the original's data is in the game's assets or its code, and the
tests check it against the original bytes: the dashboard, its bulbs and the
font are PNG images in src/EliteSharp/Assets/Images, the sound effects are
OGG files in src/EliteSharp/Assets/Sounds, the markets and equipment prices
are in src/EliteSharp/Assets/trading.yml, the text is in
src/EliteSharp/Assets/Strings and src/EliteSharp/Assets/Missions, and the
default commander, the energy bomb's bolt, the pause keys, the hangar's
groups of ships and the letters of the systems' names are in the game's code.
"""

import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "elite-source-code-bbc-master")

LISTING = os.path.join(SRC, "3-assembled-output", "compile.txt")
BCODE = os.path.join(SRC, "3-assembled-output", "BCODE.unprot.bin")
BDATA = os.path.join(SRC, "3-assembled-output", "BDATA.unprot.bin")

BCODE_BASE = 0x1300
BDATA_BASE = 0x7000

# ---------------------------------------------------------------------------
# Assembler listing: label -> address
# ---------------------------------------------------------------------------

def read_labels():
    labels = {}
    lines = open(LISTING, encoding="latin-1").read().splitlines()
    for i, line in enumerate(lines):
        m = re.match(r"^\.(\S+)$", line)
        if not m:
            continue
        name = m.group(1)
        if name in labels:
            continue
        for j in range(i + 1, min(i + 40, len(lines))):
            a = re.match(r"^\s+([0-9A-F]{4})\b", lines[j])
            if a:
                labels[name] = int(a.group(1), 16)
                break
    return labels


LABELS = read_labels()
BCODE_BYTES = open(BCODE, "rb").read()
BDATA_BYTES = open(BDATA, "rb").read()


def code_bytes(label, count, offset=0):
    addr = LABELS[label] + offset - BCODE_BASE
    return list(BCODE_BYTES[addr:addr + count])


def data_bytes(label, count=None, end_label=None):
    start = LABELS[label] - BDATA_BASE
    if end_label is not None:
        end = LABELS[end_label] - BDATA_BASE
    elif count is not None:
        end = start + count
    else:
        end = len(BDATA_BYTES)
    return list(BDATA_BYTES[start:end])
