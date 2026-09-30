#!/usr/bin/env python3
"""
Generates the C# data files for EliteSharp from the BBC Master Elite source.

The data tables (text token tables, font, dashboard bitmap, market data and
so on) are lifted byte-for-byte from the assembled binaries, using the label
addresses in the assembler listing, so the port uses exactly the same data as
the game. The ships are exported separately, as assets, by
tools/export_ships.py.

The one exception is the mission text: the missions are defined in
src/EliteSharp/Assets/Missions, so the text that only the missions use (the
briefings and debriefings, and the Constrictor's trail of clues in the system
descriptions) is left out of the token tables (see strip_mission_text).

Usage: python tools/gen_data.py   (run from the repository root)
"""

import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "elite-source-code-bbc-master")

LISTING = os.path.join(SRC, "3-assembled-output", "compile.txt")
BCODE = os.path.join(SRC, "3-assembled-output", "BCODE.unprot.bin")
BDATA = os.path.join(SRC, "3-assembled-output", "BDATA.unprot.bin")
FONT = os.path.join(SRC, "1-source-files", "fonts", "P.FONT.bin")
DIALS = os.path.join(SRC, "1-source-files", "images", "P.DIALS2P.bin")
OUT_DIR = os.path.join(ROOT, "src", "EliteSharp", "Data")

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


# ---------------------------------------------------------------------------
# Mission text
# ---------------------------------------------------------------------------

VE = 0x57

# The extended tokens that only the missions use: the briefings and
# debriefings (10, 11, 15, 222 and 223), "Incoming Message" (216), the
# captains' names (217-219), where the Constrictor was last seen (220-221), the
# phrases that only those use (196, 203, 204, 207, 208, 209, 211, 212 and 213)
# and the random words in the Constrictor's clues (91-95 and 106-110). Their
# places in the table are kept, empty, so the other tokens keep their numbers.
MISSION_TOKENS = sorted({10, 11, 15, 91, 92, 93, 94, 95, 106, 107, 108, 109, 110, 196, 203, 204, 207, 208,
                         209, 211, 212, 213, 216, 217, 218, 219, 220, 221, 222, 223})

# The control codes that only the mission text uses: show the ship and wait
# (22), wait for a key (24), incoming message (25), the captain's name (27) and
# where the Constrictor was last seen (28)
MISSION_CODES = {22, 24, 25, 27, 28}


def split_tokens(data):
    """Split a VE-delimited token table into its tokens (token n is item n)."""
    parts = [[]]
    for byte in data:
        if byte == VE:
            parts.append([])
        else:
            parts[-1].append(byte)
    return parts


def join_tokens(parts):
    out = list(parts[0])
    for part in parts[1:]:
        out.append(VE)
        out.extend(part)
    return out


def token_references(part, random_bases):
    """The extended tokens and control codes that a token uses."""
    tokens, codes = set(), set()
    for byte in part:
        c = byte ^ VE
        if c < 32:
            codes.add(c)
        elif 91 <= c < 129:
            tokens.update(random_bases[c - 91] + i for i in range(5))
        elif 129 <= c < 215:
            tokens.add(c)
    return tokens, codes


def strip_mission_text(extended, systems, galaxies, descriptions, random_bases):
    """
    Remove the mission-only text: empty the mission tokens, and remove the
    system descriptions that are mission 1 clues (the ones with bit 7 of the
    galaxy clear, which the original only shows during mission 1). Check that
    nothing left refers to anything removed.
    """
    parts = split_tokens(extended)
    for token in MISSION_TOKENS:
        parts[token] = []

    count = len(systems)
    descriptions_parts = split_tokens(descriptions)
    kept = [i for i in range(count) if galaxies[i] & 0x80]
    new_systems = [systems[i] for i in kept]
    new_galaxies = [galaxies[i] for i in kept]
    new_descriptions = [descriptions_parts[0]] + [descriptions_parts[i + 1] for i in kept] + descriptions_parts[count + 1:]

    for number, part in list(enumerate(parts)) + [(f"description {i + 1}", p) for i, p in enumerate(new_descriptions[1:])]:
        tokens, codes = token_references(part, random_bases)
        if tokens & set(MISSION_TOKENS) or codes & MISSION_CODES:
            raise SystemExit(f"Token {number} still uses mission text: {sorted(tokens & set(MISSION_TOKENS))} {sorted(codes & MISSION_CODES)}")

    return join_tokens(parts), new_systems, new_galaxies, join_tokens(new_descriptions)


# ---------------------------------------------------------------------------
# Byte tables
# ---------------------------------------------------------------------------

def fmt_bytes(name, data, comment):
    lines = [f"    /// <summary>{comment}</summary>",
             f"    public static readonly byte[] {name} ="]
    lines.append("    [")
    for i in range(0, len(data), 16):
        chunk = ", ".join(f"0x{b:02X}" for b in data[i:i + 16])
        lines.append(f"        {chunk},")
    lines.append("    ];")
    lines.append("")
    return lines


def gen_tables():
    tables = []

    # Text token tables (still obfuscated with RE / VE, exactly as in memory)
    tables.append(("RecursiveTokens", data_bytes("QQ18", end_label="SNE"),
                   "QQ18: recursive text tokens 0-148, each byte EOR'd with RE (&23), null-terminated."))
    extended, systems, galaxies, descriptions = strip_mission_text(
        data_bytes("TKN1", end_label="RUPLA"),
        data_bytes("RUPLA", end_label="RUGAL"),
        data_bytes("RUGAL", end_label="RUTOK"),
        data_bytes("RUTOK"),
        code_bytes("MTIN", 38))
    tables.append(("ExtendedTokens", extended,
                   "TKN1: extended text tokens, each byte EOR'd with VE (&57), tokens delimited by VE (without the mission text, which is in Assets/Missions)."))
    tables.append(("ExtendedDescriptionSystems", systems,
                   "RUPLA: system numbers that have extended description overrides (without the mission 1 clues, which are in Assets/Missions)."))
    tables.append(("ExtendedDescriptionGalaxies", galaxies,
                   "RUGAL: galaxy numbers (bit 7 set) for the RUPLA overrides."))
    tables.append(("ExtendedDescriptionTokens", descriptions,
                   "RUTOK: extended description override tokens, EOR'd with VE (&57)."))

    # Tables in the main code block (the maths tables, such as the sine,
    # arctan and logarithm tables, are left out, as the game uses real maths)
    tables.append(("ExtendedTwoLetterTokens", code_bytes("TKN2", 26),
                   "TKN2: two-letter tokens 215-227 for extended text."))
    tables.append(("TwoLetterTokens", code_bytes("QQ16", 64),
                   "QQ16: two-letter tokens 128-159."))
    tables.append(("RandomTokenBases", code_bytes("MTIN", 38),
                   "MTIN: base token numbers for the random extended tokens [91-128]."))
    tables.append(("MarketPrices", code_bytes("QQ23", 17 * 4),
                   "QQ23: market table (base price, factor/units, base quantity, mask) for 17 items."))
    tables.append(("EquipmentPrices", code_bytes("PRXS", 28),
                   "PRXS: equipment prices * 10 as 16-bit little-endian words."))
    tables.append(("HangarGroups", code_bytes("HATB", 36),
                   "HATB: ship hangar groups (type, x_hi/z_hi, z_lo/x_sign) * 3 * 4."))
    tables.append(("BombBaseX", code_bytes("BOMBPOS", 10),
                   "BOMBPOS: base x-coordinates for the energy bomb lightning bolt."))
    tables.append(("DefaultCommander", code_bytes("NA2%", LABELS["NAEND%"] - LABELS["NA2%"]),
                   "NA2%: the default JAMESON commander (name + data block + checksums)."))
    tables.append(("SoundPriority", code_bytes("SFXPR", 12), "SFXPR: sound data block 1."))
    tables.append(("SoundBits", code_bytes("SFXBT", 12), "SFXBT: sound data block 2."))
    tables.append(("SoundFrequency", code_bytes("SFXFQ", 12), "SFXFQ: sound data block 3."))
    tables.append(("SoundVolumeChange", code_bytes("SFXVC", 12), "SFXVC: sound data block 4."))
    # The colours (the palettes TVT1 and TVT3, and the colour tables coltabl
    # and sightcol) are defined as inks and palettes in the game's code (see
    # Rendering/Ink.cs and Rendering/Palette.cs), rather than as screen bytes
    tables.append(("EcmBulb", code_bytes("ECBT", 16), "ECBT: E.C.M. bulb bitmap (mode 2)."))
    tables.append(("StationBulb", code_bytes("SPBT", 16), "SPBT: space station bulb bitmap (mode 2)."))
    tables.append(("PauseToggleKeys", code_bytes("TGINT", 9),
                   "TGINT: configuration keys toggled while paused (CAPS LOCK, then AXFYJKUT)."))

    tables.append(("Font", list(open(FONT, "rb").read()),
                   "FONT%: the MOS character bitmaps for ASCII 32-127."))
    tables.append(("Dashboard", list(open(DIALS, "rb").read()),
                   "P.DIALS2P: the mode 2 dashboard bitmap (7 character rows)."))

    out = ["// <auto-generated>",
           "// Generated by tools/gen_data.py from the assembled BBC Master Elite binaries.",
           "// Do not edit by hand.",
           "// </auto-generated>",
           "",
           "namespace EliteSharp.Data;",
           "",
           "public static class GameData",
           "{"]
    for name, data, comment in tables:
        out.extend(fmt_bytes(name, data, comment))
    out.append("}")
    return "\n".join(out)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    with open(os.path.join(OUT_DIR, "GameData.g.cs"), "w", newline="\n") as f:
        f.write(gen_tables() + "\n")
    print("Generated the data tables into", OUT_DIR)


if __name__ == "__main__":
    main()
