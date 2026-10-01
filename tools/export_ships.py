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
Each model is a glTF 2.0 file with one mesh, whose primitives share one set of
vertex positions (POSITION, in the original's units):

  * a list of triangles (mode 4): the ship's surface, closed and wound so that
    each triangle faces outwards, which the renderer uses to hide whatever is
    behind the ship (see "Ship surfaces" below for how it is built)
  * a list of lines (mode 1): the ship's structure, the edges where its faces
    meet (and the parts that stick out, such as gun barrels)
  * a list of lines (mode 1), if the ship has any: the details drawn on its
    faces, such as vents and windows

Each primitive's extras name its role ("surface", "structure" or "details").
The mesh's extras.elite holds the original data that the game itself uses:
gunVertex (the vertex the ship fires from) and explosionVertices (how many
vertices its explosion cloud starts from). The original's other drawing data
(such as the distance beyond which it draws a ship as a dot) is left out, as
the renderer decides how to draw the ships from their geometry.

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
import original  # noqa: E402

ROOT = original.ROOT
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
    return original.BDATA_BYTES[addr - original.BDATA_BASE]


def signed_coord(sign_bit, magnitude):
    return -magnitude if sign_bit else magnitude


def parse_ships():
    """
    Decode every blueprint from the assembled data, following the same pointers
    the game does (XX21 table, then the edge/face offsets in each header). This
    reproduces the original's quirks exactly, such as the splinter's face data
    pointing 24 bytes past its own faces, into the Shuttle's blueprint.
    """
    xx21 = original.LABELS["XX21"]
    ships = {}
    for index, (label, _, _) in enumerate(SHIP_ORDER):
        base = mem(xx21 + index * 2) | (mem(xx21 + index * 2 + 1) << 8)
        assert base == original.LABELS[label], (label, hex(base), hex(original.LABELS[label]))
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
                    if original.LABELS.get(key) == target:
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


# ---------------------------------------------------------------------------
# Ship surfaces
# ---------------------------------------------------------------------------
#
# The original stores each ship as vertices, edges and face normals, and each
# edge records the two faces it runs between (or the same face twice, for a
# detail drawn on a face, such as a vent or a window). That is enough to rebuild
# each face as a polygon: a face is bordered by the edges that name it. The
# polygons make a closed surface for each ship, which the renderer uses to
# hide what is behind it.
#
# A few ships need some care:
#   * Some edges stick out of the ship (gun barrels, the Krait's prongs, the
#     missile's fins). They name the faces they run between, but they don't
#     border them, so they are pruned from the faces' borders (and stay as
#     lines).
#   * The Cougar's wings are flat plates, whose top and bottom are part of the
#     hull's top and bottom faces, and the edge along each wing root runs across
#     those faces; it is left out of their borders.
#   * The Adder's back is one flat face that is split into three faces with
#     the same normal, whose edges only make a border when put together.
#   * The alloy plate is a flat plate drawn with detail lines, so it becomes a
#     polygon with a face on each side.
# Any holes left over (edges bordering only one face) are filled.


def vsub(a, b):
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def vdot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def vcross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def vscale(a, s):
    return [a[0] * s, a[1] * s, a[2] * s]


def vlength(a):
    return vdot(a, a) ** 0.5


def newell_normal(points):
    """The normal of a polygon (not necessarily flat), by Newell's method."""
    n = [0.0, 0.0, 0.0]
    for i, p in enumerate(points):
        q = points[(i + 1) % len(points)]
        n[0] += (p[1] - q[1]) * (p[2] + q[2])
        n[1] += (p[2] - q[2]) * (p[0] + q[0])
        n[2] += (p[0] - q[0]) * (p[1] + q[1])
    return n


def edge_key(a, b):
    return (min(a, b), max(a, b))


def degrees(edges):
    degree = {}
    for a, b in edges:
        degree[a] = degree.get(a, 0) + 1
        degree[b] = degree.get(b, 0) + 1
    return degree


def single_loop(edges):
    """The vertices of the one loop that the edges make, in order, or None."""
    if len(edges) < 3 or any(d != 2 for d in degrees(edges).values()):
        return None
    neighbours = {}
    for a, b in edges:
        neighbours.setdefault(a, []).append(b)
        neighbours.setdefault(b, []).append(a)
    start = next(iter(neighbours))
    loop, previous, current = [start], None, start
    while True:
        nxt = [v for v in neighbours[current] if v != previous][0]
        if nxt == start:
            break
        loop.append(nxt)
        previous, current = current, nxt
        if len(loop) > len(neighbours):
            return None
    return loop if len(loop) == len(neighbours) else None


def face_border(edges):
    """
    The border of a face as a loop of vertices, from the edges that name it:
    edges that stick out (with a free end) are pruned, and if the rest don't
    make a single loop, edges that cut across the face (joining two vertices
    that have more than two edges) are left out until they do.
    """
    edges = set(edges)
    while True:
        degree = degrees(edges)
        loose = {e for e in edges if degree[e[0]] == 1 or degree[e[1]] == 1}
        if not loose:
            break
        edges -= loose
    loop = single_loop(edges)
    if loop is not None:
        return loop
    degree = degrees(edges)
    chords = [e for e in edges if degree[e[0]] > 2 and degree[e[1]] > 2]
    for count in range(1, len(chords) + 1):
        for i in range(len(chords) - count + 1):
            loop = single_loop(edges - set(chords[i:i + count]))
            if loop is not None:
                return loop
    return None


def boundary_loops(edges):
    """Split a set of edges (each vertex on two of them) into loops."""
    edges = set(edges)
    loops = []
    while edges:
        a, b = next(iter(edges))
        loop = [a]
        current, previous = b, a
        edges.discard((a, b))
        while current != a:
            loop.append(current)
            nxt = next(e for e in edges if current in e)
            edges.discard(nxt)
            previous, current = current, nxt[1] if nxt[0] == current else nxt[0]
        loops.append(loop)
    return loops


def orient(polygons):
    """
    Wind the polygons consistently (neighbours run along their shared edge in
    opposite directions), and outwards (so the enclosed volume is positive).
    """
    def directed(poly):
        return {(poly[i], poly[(i + 1) % len(poly)]) for i in range(len(poly))}

    done = [False] * len(polygons)
    for start in range(len(polygons)):
        if done[start]:
            continue
        done[start] = True
        queue = [start]
        while queue:
            p = queue.pop()
            edges_p = directed(polygons[p])
            for q in range(len(polygons)):
                if done[q]:
                    continue
                edges_q = directed(polygons[q])
                if any((b, a) in edges_p for a, b in edges_q):
                    done[q] = True
                    queue.append(q)
                elif any((a, b) in edges_p for a, b in edges_q):
                    polygons[q] = polygons[q][::-1]
                    done[q] = True
                    queue.append(q)
    return polygons


def triangulate(points, poly):
    """Ear-clip a polygon (a loop of vertex numbers), keeping its winding."""
    normal = newell_normal([points[v] for v in poly])
    remaining = list(poly)
    triangles = []

    def is_ear(i):
        a, b, c = remaining[i - 1], remaining[i], remaining[(i + 1) % len(remaining)]
        pa, pb, pc = points[a], points[b], points[c]
        if vdot(vcross(vsub(pb, pa), vsub(pc, pa)), normal) <= 0:
            return False
        for v in remaining:
            if v in (a, b, c):
                continue
            p = points[v]
            if (vdot(vcross(vsub(pb, pa), vsub(p, pa)), normal) > 0 and
                    vdot(vcross(vsub(pc, pb), vsub(p, pb)), normal) > 0 and
                    vdot(vcross(vsub(pa, pc), vsub(p, pc)), normal) > 0):
                return False
        return True

    while len(remaining) > 3:
        for i in range(len(remaining)):
            if is_ear(i):
                triangles.append((remaining[i - 1], remaining[i], remaining[(i + 1) % len(remaining)]))
                del remaining[i]
                break
        else:
            # Not simple (or degenerate): fall back to a fan
            triangles += [(remaining[0], remaining[i], remaining[i + 1]) for i in range(1, len(remaining) - 1)]
            remaining = []
    if len(remaining) == 3:
        triangles.append(tuple(remaining))
    return triangles


def split_along(poly, chords):
    """
    Split a polygon (a loop of vertex numbers) along any of the given lines
    that join two of its corners that aren't next to each other, so the
    triangles include those lines.
    """
    n = len(poly)
    for i in range(n):
        for j in range(i + 2, n):
            if (i, j) != (0, n - 1) and edge_key(poly[i], poly[j]) in chords:
                return split_along(poly[i:j + 1], chords) + split_along(poly[j:] + poly[:i + 1], chords)
    return [poly]


def signed_volume(points, triangles):
    return sum(vdot(points[a], vcross(points[b], points[c])) for a, b, c in triangles) / 6


def build_surface(name, vertices, edges, faces):
    """
    The ship's surface as triangles (outward-facing and closed), its lines
    split into the structure and the details drawn on its faces, and its
    vertex positions with the details moved onto the faces they are drawn on.
    """
    points = [[float(v[0]), float(v[1]), float(v[2])] for v in vertices]
    nfaces = len(faces)
    structure = [edge_key(e[0], e[1]) for e in edges if e[2] != e[3]]
    details = [(edge_key(e[0], e[1]), e[2]) for e in edges if e[2] == e[3]]

    # Each face's border
    named = {}
    for e in edges:
        if e[2] != e[3]:
            for f in (e[2], e[3]):
                if f < nfaces:
                    named.setdefault(f, set()).add(edge_key(e[0], e[1]))
    borders = {}
    for f in range(nfaces):
        loop = face_border(named.get(f, ()))
        if loop is not None:
            borders[f] = loop

    # Faces that are split into parts with the same normal
    def direction(f):
        n = faces[f][:3]
        length = vlength(n) or 1
        return tuple(round(c / length, 3) for c in n)

    unmatched = [f for f in named if f not in borders]
    groups = {}
    for f in unmatched:
        groups.setdefault(direction(f), []).append(f)
    face_of = {f: f for f in borders}
    for group in groups.values():
        merged = set().union(*(named[f] for f in group))
        loop = face_border(merged)
        if loop is not None:
            borders[group[0]] = loop
            for f in group:
                face_of[f] = group[0]

    polygons = list(borders.values())
    polygon_faces = list(borders.keys())

    # Fill any holes
    uses = {}
    for poly in polygons:
        for i in range(len(poly)):
            key = edge_key(poly[i], poly[(i + 1) % len(poly)])
            uses[key] = uses.get(key, 0) + 1
    open_edges = [k for k, n in uses.items() if n == 1]
    for loop in boundary_loops(open_edges) if open_edges else []:
        polygons.append(loop)
        polygon_faces.append(None)

    two_sided = False
    if not polygons:
        # A flat plate drawn with detail lines: a polygon with a face on each side
        loop = single_loop({key for key, _ in details})
        assert loop is not None, f"{name}: no surface"
        polygons = [loop]
        polygon_faces = [0]
        two_sided = True

    # Triangulate each face, first splitting it along any detail line drawn
    # from one of its corners to another, so the line lies on the surface
    # (the Python's rear faces aren't flat, and have their diagonals drawn)
    polygons = orient(polygons)
    detail_lines = {key for key, _ in details}
    triangles_by_face = []
    for poly in polygons:
        triangles_by_face.append([t for part in split_along(poly, detail_lines) for t in triangulate(points, part)])
    triangles = [t for ts in triangles_by_face for t in ts]
    if two_sided:
        triangles += [(a, c, b) for a, b, c in triangles]
    elif signed_volume(points, triangles) < 0:
        triangles = [(a, c, b) for a, b, c in triangles]
        triangles_by_face = [[(a, c, b) for a, b, c in ts] for ts in triangles_by_face]

    # Check that the surface is closed and consistently wound (a flat plate,
    # with a face on each side, is closed by definition)
    if not two_sided:
        directed = {}
        for a, b, c in triangles:
            for key in ((a, b), (b, c), (c, a)):
                directed[key] = directed.get(key, 0) + 1
        for (a, b), n in directed.items():
            assert n == 1 and directed.get((b, a)) == 1, f"{name}: surface isn't closed at edge {a}-{b}"

    # Move the details onto the faces they are drawn on (the original's
    # whole-number coordinates put some of them a little inside or outside)
    structural_vertices = {v for key in structure for v in key}
    for key, f in details:
        f = face_of.get(f, f)
        if f not in polygon_faces:
            continue
        face_triangles = triangles_by_face[polygon_faces.index(f)]
        for v in key:
            if v in structural_vertices:
                continue
            points[v] = project_onto(points[v], [[points[i] for i in t] for t in face_triangles])

    return points, triangles, structure, [key for key, _ in details]


def project_onto(p, triangles):
    """Move a point onto the plane of the triangle it is over (or the nearest plane)."""
    best, best_distance = None, None
    for a, b, c in triangles:
        n = vcross(vsub(b, a), vsub(c, a))
        length = vlength(n)
        if length == 0:
            continue
        n = vscale(n, 1 / length)
        distance = vdot(vsub(p, a), n)
        q = vsub(p, vscale(n, distance))
        inside = (vdot(vcross(vsub(b, a), vsub(q, a)), n) >= -1e-6 and
                  vdot(vcross(vsub(c, b), vsub(q, b)), n) >= -1e-6 and
                  vdot(vcross(vsub(a, c), vsub(q, c)), n) >= -1e-6)
        rank = (0 if inside else 1, abs(distance))
        if best is None or rank < best_distance:
            best, best_distance = q, rank
    return best if best is not None else p


def geometry_key(s):
    return json.dumps([s["vertices"], s["edges"][:s["nedges"]], s["faces"][:s["nfaces"]],
                       s["normscale"], s["vis"], s["maxedge"], s["gun"], s["expl"]])


def pad4(data):
    return data + b"\0" * (-len(data) % 4)


def build_gltf(name, s):
    edges = s["edges"][:s["nedges"]]
    faces = s["faces"][:s["nfaces"]]
    points, triangles, structure, details = build_surface(name, s["vertices"], edges, faces)

    positions = b"".join(struct.pack("<3f", *p) for p in points)
    surface = b"".join(struct.pack("<3H", *t) for t in triangles)
    lines = b"".join(struct.pack("<2H", a, b) for a, b in structure)
    detail_lines = b"".join(struct.pack("<2H", a, b) for a, b in details)

    views = []
    accessors = []
    blob = b""

    def add(data, target, accessor):
        nonlocal blob
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), "target": target})
        blob += pad4(data)
        accessors.append(dict(accessor, bufferView=len(views) - 1))
        return len(accessors) - 1

    xs, ys, zs = ([p[i] for p in points] for i in range(3))
    position = add(positions, 34962, {"componentType": 5126, "count": len(points), "type": "VEC3",
                                      "min": [min(xs), min(ys), min(zs)], "max": [max(xs), max(ys), max(zs)]})
    primitives = [
        {"mode": 4, "attributes": {"POSITION": position},
         "indices": add(surface, 34963, {"componentType": 5123, "count": len(triangles) * 3, "type": "SCALAR"}),
         "extras": {"role": "surface"}},
        {"mode": 1, "attributes": {"POSITION": position},
         "indices": add(lines, 34963, {"componentType": 5123, "count": len(structure) * 2, "type": "SCALAR"}),
         "extras": {"role": "structure"}},
    ]
    if details:
        primitives.append(
            {"mode": 1, "attributes": {"POSITION": position},
             "indices": add(detail_lines, 34963, {"componentType": 5123, "count": len(details) * 2, "type": "SCALAR"}),
             "extras": {"role": "details"}})

    return {
        "asset": {
            "version": "2.0",
            "generator": "EliteSharp tools/export_ships.py",
            "extras": {
                "description": "Elite ship model. Coordinates are in the original's "
                               "left-handed space (x right, y up, z forward); the node "
                               "mirrors x to convert to glTF's right-handed space.",
            },
        },
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"name": name, "mesh": 0, "scale": [-1, 1, 1]}],
        "meshes": [{
            "name": name,
            "primitives": primitives,
            "extras": {
                "elite": {
                    "gunVertex": s["gun"] // 4,
                    "explosionVertices": (s["expl"] - 6) // 4,
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

    # Put short lists (numbers and strings) on one line each, to keep the
    # files readable
    text = re.sub(r"\[\s+([^\[\]{}]*?)\s+\]",
                  lambda m: "[" + re.sub(r",\s+", ", ", m.group(1)) + "]"
                  if len(m.group(1)) < 120 else m.group(0), text)
    with open(path, "w", newline="\n", encoding="utf-8") as f:
        f.write(text + "\n")


def main():
    ships = parse_ships()
    shpcol = original.code_bytes("shpcol", 34)
    scacol = original.code_bytes("scacol", 34)
    newb = original.data_bytes("E%", 33)
    kwl = original.data_bytes("KWL%", 33)
    kwh = original.data_bytes("KWH%", 33)

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
        assert (s["expl"] - 6) % 4 == 0 and s["gun"] % 4 == 0, label

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
