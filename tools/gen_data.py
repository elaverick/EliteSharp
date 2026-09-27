#!/usr/bin/env python3
"""
Generates the C# data files for EliteSharp from the BBC Master Elite source.

Ship blueprints are parsed from the annotated source (elite-data.asm) so the
generated classes keep the vertex/edge/face layout readable. Everything else
(text token tables, maths tables, font, dashboard bitmap, market data and so on)
is lifted byte-for-byte from the assembled binaries, using the label addresses
in the assembler listing, so the port uses exactly the same data as the game.

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
# Ship blueprints
# ---------------------------------------------------------------------------

SHIP_ORDER = [
    ("SHIP_MISSILE", "Missile", "Missile"),
    ("SHIP_CORIOLIS", "CoriolisStation", "Coriolis space station"),
    ("SHIP_ESCAPE_POD", "EscapePod", "Escape pod"),
    ("SHIP_PLATE", "AlloyPlate", "Alloy plate"),
    ("SHIP_CANISTER", "CargoCanister", "Cargo canister"),
    ("SHIP_BOULDER", "Boulder", "Boulder"),
    ("SHIP_ASTEROID", "Asteroid", "Asteroid"),
    ("SHIP_SPLINTER", "Splinter", "Splinter"),
    ("SHIP_SHUTTLE", "Shuttle", "Shuttle"),
    ("SHIP_TRANSPORTER", "Transporter", "Transporter"),
    ("SHIP_COBRA_MK_3", "CobraMkIII", "Cobra Mk III"),
    ("SHIP_PYTHON", "Python", "Python"),
    ("SHIP_BOA", "Boa", "Boa"),
    ("SHIP_ANACONDA", "Anaconda", "Anaconda"),
    ("SHIP_ROCK_HERMIT", "RockHermit", "Rock hermit (asteroid)"),
    ("SHIP_VIPER", "Viper", "Viper"),
    ("SHIP_SIDEWINDER", "Sidewinder", "Sidewinder"),
    ("SHIP_MAMBA", "Mamba", "Mamba"),
    ("SHIP_KRAIT", "Krait", "Krait"),
    ("SHIP_ADDER", "Adder", "Adder"),
    ("SHIP_GECKO", "Gecko", "Gecko"),
    ("SHIP_COBRA_MK_1", "CobraMkI", "Cobra Mk I"),
    ("SHIP_WORM", "Worm", "Worm"),
    ("SHIP_COBRA_MK_3_P", "CobraMkIIIPirate", "Cobra Mk III (pirate)"),
    ("SHIP_ASP_MK_2", "AspMkII", "Asp Mk II"),
    ("SHIP_PYTHON_P", "PythonPirate", "Python (pirate)"),
    ("SHIP_FER_DE_LANCE", "FerDeLance", "Fer-de-lance"),
    ("SHIP_MORAY", "Moray", "Moray"),
    ("SHIP_THARGOID", "Thargoid", "Thargoid"),
    ("SHIP_THARGON", "Thargon", "Thargon"),
    ("SHIP_CONSTRICTOR", "Constrictor", "Constrictor"),
    ("SHIP_COUGAR", "Cougar", "Cougar"),
    ("SHIP_DODO", "DodoStation", "Dodecahedron (\"Dodo\") space station"),
]


def mem(addr):
    """Read a byte from the assembled game data at a BBC memory address."""
    return BDATA_BYTES[addr - BDATA_BASE]


def signed_coord(sign_bit, magnitude):
    return -magnitude if sign_bit else magnitude


def parse_ships():
    """
    Decode every blueprint from the assembled data, following the same pointers
    the game does (XX21 table, then the edge/face offsets in each header). This
    reproduces the original's quirks exactly, such as the splinter's face data
    pointing 24 bytes past its own faces, into the Shuttle's blueprint.
    """
    xx21 = LABELS["XX21"]
    ships = {}
    for index, (label, _, _) in enumerate(SHIP_ORDER):
        base = mem(xx21 + index * 2) | (mem(xx21 + index * 2 + 1) << 8)
        assert base == LABELS[label], (label, hex(base), hex(LABELS[label]))
        h = [mem(base + i) for i in range(20)]
        edges_off = (h[16] << 8) | h[3]
        faces_off = (h[17] << 8) | h[4]
        if edges_off >= 0x8000:
            edges_off -= 0x10000
        if faces_off >= 0x8000:
            faces_off -= 0x10000
        nverts = h[8] // 6
        nedges = h[9]
        nfaces = h[12] // 4

        vertices = []
        for v in range(nverts):
            b = [mem(base + 20 + v * 6 + i) for i in range(6)]
            s = b[3]
            vertices.append([signed_coord(s & 0x80, b[0]), signed_coord(s & 0x40, b[1]),
                             signed_coord(s & 0x20, b[2]), b[4] & 15, b[4] >> 4,
                             b[5] & 15, b[5] >> 4, s & 31])
        edges = []
        for e in range(nedges):
            b = [mem(base + edges_off + e * 4 + i) for i in range(4)]
            edges.append([b[2] // 4, b[3] // 4, b[1] & 15, b[1] >> 4, b[0]])
        faces = []
        for f in range(nfaces):
            b = [mem(base + faces_off + f * 4 + i) for i in range(4)]
            s = b[0]
            faces.append([signed_coord(s & 0x80, b[1]), signed_coord(s & 0x40, b[2]),
                          signed_coord(s & 0x20, b[3]), s & 31])

        def owner(offset):
            target = base + offset
            for other, _, _ in SHIP_ORDER:
                for part in ("EDGES", "FACES"):
                    key = f"{other}_{part}"
                    if LABELS.get(key) == target:
                        return other
            return f"&{target:04X}"

        ships[label] = dict(byte0=h[0], area=h[1] | (h[2] << 8), maxedge=h[5], gun=h[6],
                            expl=h[7], nverts=nverts, nedges=nedges,
                            bounty=h[10] | (h[11] << 8), nfaces=nfaces, vis=h[13],
                            energy=h[14], speed=h[15], normscale=h[18], lasmis=h[19],
                            edges_src=owner(edges_off), faces_src=owner(faces_off),
                            vertices=vertices, edges=edges, faces=faces)
    return ships


def gen_ships(ships):
    out = []
    out.append("// <auto-generated>")
    out.append("// Generated by tools/gen_data.py from elite-data.asm (BBC Master Elite).")
    out.append("// Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("")
    out.append("namespace EliteSharp.Game.Ships;")
    out.append("")
    for index, (label, cls, desc) in enumerate(SHIP_ORDER):
        s = ships[label]
        shared = []
        if s["edges_src"] != label:
            shared.append(f"edges from {s['edges_src']}")
        if s["faces_src"] != label:
            shared.append(f"faces from {s['faces_src']}")
        note = f" (shares {', '.join(shared)})" if shared else ""
        out.append(f"/// <summary>{desc} ({label}){note}.</summary>")
        out.append(f"public sealed partial class {cls} : Ship")
        out.append("{")
        out.append(f"    public static readonly ShipBlueprint Data = new(")
        out.append(f"        blueprintNumber: {index + 1},")
        out.append(f"        name: \"{desc.replace(chr(34), chr(92) + chr(34))}\",")
        out.append(f"        byte0: {s['byte0']},")
        out.append(f"        targetableArea: {s['area']},")
        out.append(f"        lineHeapSize: {s['maxedge']},")
        out.append(f"        gunVertex: {s['gun'] // 4},")
        out.append(f"        explosionCountByte: {s['expl']},")
        out.append(f"        bounty: {s['bounty']},")
        out.append(f"        visibilityDistance: {s['vis']},")
        out.append(f"        maxEnergy: {s['energy']},")
        out.append(f"        maxSpeed: {s['speed']},")
        out.append(f"        normalScale: {s['normscale']},")
        out.append(f"        laserAndMissiles: 0b{s['lasmis']:08b},")
        out.append("        vertices:")
        out.append("        [")
        for v in s["vertices"]:
            x, y, z, f1, f2, f3, f4, vis = v
            out.append(f"            new({x}, {y}, {z}, {f1}, {f2}, {f3}, {f4}, {vis}),")
        out.append("        ],")
        out.append("        edges:")
        out.append("        [")
        for e in s["edges"][:s["nedges"]]:
            v1, v2, f1, f2, vis = e
            out.append(f"            new({v1}, {v2}, {f1}, {f2}, {vis}),")
        out.append("        ],")
        out.append("        faces:")
        out.append("        [")
        for f in s["faces"][:s["nfaces"]]:
            nx, ny, nz, vis = f
            out.append(f"            new({nx}, {ny}, {nz}, {vis}),")
        out.append("        ]);")
        out.append("")
        out.append(f"    public {cls}() : base({index + 1}, Data) {{ }}")
        out.append("}")
        out.append("")
    return "\n".join(out)


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
    tables.append(("ExtendedTokens", data_bytes("TKN1", end_label="RUPLA"),
                   "TKN1: extended text tokens, each byte EOR'd with VE (&57), tokens delimited by VE."))
    tables.append(("ExtendedDescriptionSystems", data_bytes("RUPLA", end_label="RUGAL"),
                   "RUPLA: system numbers that have extended description overrides."))
    tables.append(("ExtendedDescriptionGalaxies", data_bytes("RUGAL", end_label="RUTOK"),
                   "RUGAL: galaxy numbers (bit 7 = second table) for the RUPLA overrides."))
    tables.append(("ExtendedDescriptionTokens", data_bytes("RUTOK"),
                   "RUTOK: extended description override tokens, EOR'd with VE (&57)."))
    tables.append(("Sine", data_bytes("SNE", 32),
                   "SNE: sine table, 32 segments of a half circle, scaled so 1.0 = 256."))
    tables.append(("Arctan", data_bytes("ACT", 32),
                   "ACT: arctan table for 0 to 45 degrees, 256 = full circle."))
    tables.append(("DefaultNewbFlags", data_bytes("E%", 33),
                   "E%: default NEWB flags for each ship type (index = type - 1)."))
    tables.append(("KillFraction", data_bytes("KWL%", 33),
                   "KWL%: fractional kill points for each ship type (index = type - 1)."))
    tables.append(("KillInteger", data_bytes("KWH%", 33),
                   "KWH%: integer kill points for each ship type (index = type - 1)."))

    # Tables in the main code block
    tables.append(("LogHigh", code_bytes("log", 256), "log: high byte of 32 * log2(n) * 256."))
    tables.append(("LogLow", code_bytes("logL", 256), "logL: low byte of 32 * log2(n) * 256."))
    tables.append(("AntiLog", code_bytes("alogh", 256), "alogh: 2^((n / 2 + 128) / 16) / 256."))
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
    tables.append(("ShipColours", code_bytes("shpcol", 34),
                   "shpcol: mode 1 colour byte for each ship type (index = type)."))
    tables.append(("ScannerColours", code_bytes("scacol", 34),
                   "scacol: mode 2 scanner colour byte for each ship type (index = type)."))
    tables.append(("ExplosionColours", code_bytes("coltabl", 4),
                   "coltabl: mode 1 colours for explosion particles."))
    tables.append(("SightColours", code_bytes("sightcol", 4),
                   "sightcol: laser crosshair colours for pulse, beam, military, mining."))
    tables.append(("HangarGroups", code_bytes("HATB", 36),
                   "HATB: ship hangar groups (type, x_hi/z_hi, z_lo/x_sign) * 3 * 4."))
    tables.append(("BombBaseX", code_bytes("BOMBPOS", 10),
                   "BOMBPOS: base x-coordinates for the energy bomb lightning bolt."))
    tables.append(("DefaultCommander", code_bytes("NA2%", LABELS["NAEND%"] - LABELS["NA2%"]),
                   "NA2%: the default JAMESON commander (name + data block + checksums)."))
    tables.append(("SpaceViewPalettes", code_bytes("TVT3", 64),
                   "TVT3: the four mode 1 palettes (space, chart, title, trade)."))
    tables.append(("DashboardPalette", code_bytes("TVT1", 16),
                   "TVT1: the mode 2 palette for the dashboard."))
    tables.append(("SoundPriority", code_bytes("SFXPR", 12), "SFXPR: sound data block 1."))
    tables.append(("SoundBits", code_bytes("SFXBT", 12), "SFXBT: sound data block 2."))
    tables.append(("SoundFrequency", code_bytes("SFXFQ", 12), "SFXFQ: sound data block 3."))
    tables.append(("SoundVolumeChange", code_bytes("SFXVC", 12), "SFXVC: sound data block 4."))
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
    ships = parse_ships()
    with open(os.path.join(OUT_DIR, "ShipBlueprints.g.cs"), "w", newline="\n") as f:
        f.write(gen_ships(ships) + "\n")
    with open(os.path.join(OUT_DIR, "GameData.g.cs"), "w", newline="\n") as f:
        f.write(gen_tables() + "\n")
    print("Generated", len(ships), "ship blueprints and data tables into", OUT_DIR)


if __name__ == "__main__":
    main()
