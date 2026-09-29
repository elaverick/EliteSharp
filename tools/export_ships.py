#!/usr/bin/env python3
"""
Exports the ship blueprints from the BBC Master Elite source into EliteSharp's
ship assets:

  src/EliteSharp/Assets/Ships/NN-name.json      ship attributes (one per ship type)
  src/EliteSharp/Assets/Ships/Models/name.gltf  wireframe models (glTF 2.0)

The assets are the game's source of truth for ship data, so this is an import
tool: running it again overwrites any edits made to the assets.

Model format
------------
Each model is a glTF 2.0 file with one mesh, whose single primitive is a list of
lines (mode 1) - one per edge of the wireframe. The data needed for Elite's
hidden line removal is stored alongside the geometry:

  * POSITION     the vertex coordinates (whole numbers, in the original's units)
  * _FACES       for each vertex, the four faces it belongs to (VEC4 of bytes)
  * _VISIBILITY  for each vertex, the distance beyond which it isn't drawn
  * mesh.extras.elite.edges   for each line, the faces either side and the
                              edge's visibility distance
  * mesh.extras.elite.faces   the face normals (scaled by 2^normalScale) and
                              the distance beyond which the face always shows
  * mesh.extras.elite         normalScale, dotDistance (beyond which the ship is
                              drawn as a dot), maxVisibleEdges, gunVertex and
                              explosionVertices

The coordinates are exactly those in the original, which uses a left-handed
system (x right, y up, z forward). glTF is right-handed with +z forward, so the
node that holds the mesh mirrors the x-axis, which makes glTF viewers show the
ships the right way round without changing the data.

Usage: python tools/export_ships.py   (run from the repository root)
"""

import base64
import json
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_data  # noqa: E402

ROOT = gen_data.ROOT
OUT_DIR = os.path.join(ROOT, "src", "EliteSharp", "Assets", "Ships")
MODEL_DIR = os.path.join(OUT_DIR, "Models")

# Ship type number -> (asset id, display name)
SHIP_IDS = {
    "SHIP_MISSILE": ("missile", "Missile"),
    "SHIP_CORIOLIS": ("coriolis", "Coriolis space station"),
    "SHIP_ESCAPE_POD": ("escape-pod", "Escape pod"),
    "SHIP_PLATE": ("alloy-plate", "Alloy plate"),
    "SHIP_CANISTER": ("cargo-canister", "Cargo canister"),
    "SHIP_BOULDER": ("boulder", "Boulder"),
    "SHIP_ASTEROID": ("asteroid", "Asteroid"),
    "SHIP_SPLINTER": ("splinter", "Splinter"),
    "SHIP_SHUTTLE": ("shuttle", "Shuttle"),
    "SHIP_TRANSPORTER": ("transporter", "Transporter"),
    "SHIP_COBRA_MK_3": ("cobra-mk3", "Cobra Mk III"),
    "SHIP_PYTHON": ("python", "Python"),
    "SHIP_BOA": ("boa", "Boa"),
    "SHIP_ANACONDA": ("anaconda", "Anaconda"),
    "SHIP_ROCK_HERMIT": ("rock-hermit", "Rock hermit"),
    "SHIP_VIPER": ("viper", "Viper"),
    "SHIP_SIDEWINDER": ("sidewinder", "Sidewinder"),
    "SHIP_MAMBA": ("mamba", "Mamba"),
    "SHIP_KRAIT": ("krait", "Krait"),
    "SHIP_ADDER": ("adder", "Adder"),
    "SHIP_GECKO": ("gecko", "Gecko"),
    "SHIP_COBRA_MK_1": ("cobra-mk1", "Cobra Mk I"),
    "SHIP_WORM": ("worm", "Worm"),
    "SHIP_COBRA_MK_3_P": ("cobra-mk3-pirate", "Cobra Mk III (pirate)"),
    "SHIP_ASP_MK_2": ("asp-mk2", "Asp Mk II"),
    "SHIP_PYTHON_P": ("python-pirate", "Python (pirate)"),
    "SHIP_FER_DE_LANCE": ("fer-de-lance", "Fer-de-lance"),
    "SHIP_MORAY": ("moray", "Moray"),
    "SHIP_THARGOID": ("thargoid", "Thargoid"),
    "SHIP_THARGON": ("thargon", "Thargon"),
    "SHIP_CONSTRICTOR": ("constrictor", "Constrictor"),
    "SHIP_COUGAR": ("cougar", "Cougar"),
    "SHIP_DODO": ("dodo", "Dodecahedron (\"Dodo\") space station"),
}

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


# The names of the ships' colours, from the original's colour bytes: shpcol
# holds mode 1 bytes for the space view, and scacol mode 2 bytes for the
# scanner (see Rendering/Ink.cs)
SPACE_COLOURS = {0x0F: "yellow", 0xF0: "red", 0xFF: "cyan", 0xAF: "green", 0xFA: "white", 0xC9: "moray"}
SCANNER_COLOURS = {0x00: "none", 0x03: "red", 0x0C: "green", 0x0F: "yellow", 0x30: "blue", 0x33: "magenta",
                   0x3C: "cyan", 0x3F: "white"}


def mem(addr):
    """Read a byte from the assembled game data at a BBC memory address."""
    return gen_data.BDATA_BYTES[addr - gen_data.BDATA_BASE]


def signed_coord(sign_bit, magnitude):
    return -magnitude if sign_bit else magnitude


def parse_ships():
    """
    Decode every blueprint from the assembled data, following the same pointers
    the game does (XX21 table, then the edge/face offsets in each header). This
    reproduces the original's quirks exactly, such as the splinter's face data
    pointing 24 bytes past its own faces, into the Shuttle's blueprint.
    """
    xx21 = gen_data.LABELS["XX21"]
    ships = {}
    for index, (label, _, _) in enumerate(SHIP_ORDER):
        base = mem(xx21 + index * 2) | (mem(xx21 + index * 2 + 1) << 8)
        assert base == gen_data.LABELS[label], (label, hex(base), hex(gen_data.LABELS[label]))
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
                    if gen_data.LABELS.get(key) == target:
                        return other
            return f"&{target:04X}"

        ships[label] = dict(byte0=h[0], area=h[1] | (h[2] << 8), maxedge=h[5], gun=h[6],
                            expl=h[7], nverts=nverts, nedges=nedges,
                            bounty=h[10] | (h[11] << 8), nfaces=nfaces, vis=h[13],
                            energy=h[14], speed=h[15], normscale=h[18], lasmis=h[19],
                            edges_src=owner(edges_off), faces_src=owner(faces_off),
                            vertices=vertices, edges=edges, faces=faces)
    return ships


MARKET_ITEMS = [
    "Food", "Textiles", "Radioactives", "Slaves", "Liquor/Wines", "Luxuries",
    "Narcotics", "Computers", "Machinery", "Alloys", "Firearms", "Furs",
    "Minerals", "Gold", "Platinum", "Gem-Stones", "Alien Items",
]

# The bits in the NEWB flags (E% holds the defaults for each ship type)
NEWB_FLAGS = ["trader", "bountyHunter", "hostile", "pirate", "docking",
              "innocent", "cop", "escapePod"]


def geometry_key(s):
    return json.dumps([s["vertices"], s["edges"][:s["nedges"]], s["faces"][:s["nfaces"]],
                       s["normscale"], s["vis"], s["maxedge"], s["gun"], s["expl"]])


def pad4(data):
    return data + b"\0" * (-len(data) % 4)


def build_gltf(name, s):
    vertices = s["vertices"]
    edges = s["edges"][:s["nedges"]]
    faces = s["faces"][:s["nfaces"]]

    positions = b"".join(struct.pack("<3f", v[0], v[1], v[2]) for v in vertices)
    face_ids = b"".join(struct.pack("<4B", v[3], v[4], v[5], v[6]) for v in vertices)
    visibility = b"".join(struct.pack("<f", v[7]) for v in vertices)
    indices = b"".join(struct.pack("<2H", e[0], e[1]) for e in edges)

    views = []
    blob = b""
    for data, target in ((positions, 34962), (face_ids, 34962), (visibility, 34962), (indices, 34963)):
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), "target": target})
        blob += pad4(data)

    n = len(vertices)
    xs = [v[0] for v in vertices]
    ys = [v[1] for v in vertices]
    zs = [v[2] for v in vertices]
    accessors = [
        {"bufferView": 0, "componentType": 5126, "count": n, "type": "VEC3",
         "min": [min(xs), min(ys), min(zs)], "max": [max(xs), max(ys), max(zs)]},
        {"bufferView": 1, "componentType": 5121, "count": n, "type": "VEC4"},
        {"bufferView": 2, "componentType": 5126, "count": n, "type": "SCALAR"},
        {"bufferView": 3, "componentType": 5123, "count": len(edges) * 2, "type": "SCALAR"},
    ]

    return {
        "asset": {
            "version": "2.0",
            "generator": "EliteSharp tools/export_ships.py",
            "extras": {
                "description": "Elite ship wireframe. Coordinates are in the original's "
                               "left-handed space (x right, y up, z forward); the node "
                               "mirrors x to convert to glTF's right-handed space.",
            },
        },
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"name": name, "mesh": 0, "scale": [-1, 1, 1]}],
        "meshes": [{
            "name": name,
            "primitives": [{
                "mode": 1,
                "attributes": {"POSITION": 0, "_FACES": 1, "_VISIBILITY": 2},
                "indices": 3,
            }],
            "extras": {
                "elite": {
                    "normalScale": s["normscale"],
                    "dotDistance": s["vis"],
                    "maxVisibleEdges": (s["maxedge"] - 1) // 4,
                    "gunVertex": s["gun"] // 4,
                    "explosionVertices": (s["expl"] - 6) // 4,
                    "edges": [{"faces": [e[2], e[3]], "visibility": e[4]} for e in edges],
                    "faces": [{"normal": [f[0], f[1], f[2]], "visibility": f[3]} for f in faces],
                },
            },
        }],
        "accessors": accessors,
        "bufferViews": views,
        "buffers": [{
            "byteLength": len(blob),
            "uri": "data:application/octet-stream;base64," + base64.b64encode(blob).decode("ascii"),
        }],
    }


def write_json(path, data):
    text = json.dumps(data, indent=2)

    # Put short lists (numbers and strings) and the per-edge and per-face
    # objects on one line each, to keep the files readable
    text = re.sub(r"\[\s+([^\[\]{}]*?)\s+\]",
                  lambda m: "[" + re.sub(r",\s+", ", ", m.group(1)) + "]"
                  if len(m.group(1)) < 120 else m.group(0), text)
    text = re.sub(r"\{\s+(\"(?:faces|normal)\": \[[^\]]*\]),\s+(\"visibility\": \d+)\s+\}",
                  r"{\1, \2}", text)
    with open(path, "w", newline="\n", encoding="utf-8") as f:
        f.write(text + "\n")


def main():
    ships = parse_ships()
    shpcol = gen_data.code_bytes("shpcol", 34)
    scacol = gen_data.code_bytes("scacol", 34)
    newb = gen_data.data_bytes("E%", 33)
    kwl = gen_data.data_bytes("KWL%", 33)
    kwh = gen_data.data_bytes("KWH%", 33)

    os.makedirs(MODEL_DIR, exist_ok=True)
    for old in os.listdir(OUT_DIR):
        if old.endswith(".json") and old != "ship.schema.json":
            os.remove(os.path.join(OUT_DIR, old))
    for old in os.listdir(MODEL_DIR):
        os.remove(os.path.join(MODEL_DIR, old))

    models = {}
    for index, (label, _, _) in enumerate(SHIP_ORDER):
        ship_type = index + 1
        s = ships[label]
        asset_id, name = SHIP_IDS[label]

        # Ships with identical geometry share a model
        key = geometry_key(s)
        if key not in models:
            models[key] = asset_id
            write_json(os.path.join(MODEL_DIR, asset_id + ".gltf"), build_gltf(name, s))
        model = models[key]

        byte0 = s["byte0"]
        scooped = byte0 >> 4
        flags = [flag for bit, flag in enumerate(NEWB_FLAGS) if newb[index] & (1 << bit)]
        area = s["area"]
        radius = int(round(area ** 0.5))
        assert radius * radius == area, label
        assert (s["expl"] - 6) % 4 == 0 and (s["maxedge"] - 1) % 4 == 0 and s["gun"] % 4 == 0, label

        attributes = {
            "$schema": "ship.schema.json",
            "type": ship_type,
            "name": name,
            "model": "Models/" + model + ".gltf",
            "maxEnergy": s["energy"],
            "maxSpeed": s["speed"],
            "laserPower": s["lasmis"] >> 3,
            "missiles": s["lasmis"] & 7,
            "bounty": s["bounty"] / 10,
            "targetRadius": radius,
            "canisters": byte0 & 15,
            "scoopedAs": MARKET_ITEMS[scooped + 1] if scooped else None,
            "killPoints": kwh[index] + kwl[index] / 256,
            "colour": SPACE_COLOURS[shpcol[ship_type]],
            "scannerColour": SCANNER_COLOURS[scacol[ship_type]],
            "flags": flags,
        }
        if attributes["scoopedAs"] is None:
            del attributes["scoopedAs"]
        write_json(os.path.join(OUT_DIR, f"{ship_type:02d}-{asset_id}.json"), attributes)

    print(f"Exported {len(SHIP_ORDER)} ships and {len(models)} models into {OUT_DIR}")


if __name__ == "__main__":
    main()
