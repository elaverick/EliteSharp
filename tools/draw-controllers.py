"""Draw the controller pictures for the Controls screen.

Each controller is a low-polygon 3D model, drawn the way Elite draws its
ships: in perspective, as the edges of its faces, with the edges behind the
model hidden, in thin lines on a transparent background (the screen behind is
black). The body is white and the controls are yellow (the fire button red).

Edges are only drawn where the faces either side of them meet at an angle (so
the gently curved parts look smooth), and around the model's outline. The
faces are drawn from the back to the front, each filled with black, which
hides whatever is behind it (the painter's algorithm).

Each picture is 1984 x 576 pixels (eight pixels for each of the original's
pixels in the screen's picture area), drawn at twice that size and scaled
down, to smooth the lines. The script also works out where each control is in
the picture, and writes it into the controller's profile (or profiles) in
src/EliteSharp/Assets/Controllers, so the markers the game draws line up.

Run it from anywhere, with Pillow and NumPy installed:

    python tools/draw-controllers.py
"""

import math
import pathlib
import re

import numpy as np
from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "EliteSharp" / "Assets"
CONTROLLERS = ASSETS / "Controllers"

WIDTH, HEIGHT = 1984, 576
SS = 2          # supersampling
MARGIN = 24     # around the model, in the picture's pixels
LINE = 4        # the width of the lines, in the picture's pixels
CREASE = 24     # the smallest angle between faces (in degrees) that has an edge drawn

WHITE = (255, 255, 255, 255)
YELLOW = (255, 255, 0, 255)
RED = (255, 0, 0, 255)
CYAN = (0, 255, 255, 255)
GREEN = (0, 255, 0, 255)
MAGENTA = (255, 0, 255, 255)
BLACK = (0, 0, 0, 255)

FONT = Image.open(ASSETS / "Images" / "font.png").convert("RGBA")


# ---------------------------------------------------------------------------
# Models
#
# Model space has x to the right, y away from the player and z up, in
# millimetres. A model is a list of vertices and a list of faces, each face
# being a list of vertex indices, anticlockwise when seen from outside, with
# a colour.
# ---------------------------------------------------------------------------

class Model:
    def __init__(self):
        self.vertices = []
        self.faces = []      # (indices, colour)
        self.points = {}     # named points (the controls), in model space

    def add_vertices(self, points):
        start = len(self.vertices)
        self.vertices.extend(np.array(p, dtype=float) for p in points)
        return list(range(start, start + len(points)))

    def add_face(self, indices, colour):
        self.faces.append((list(indices), colour))

    def point(self, name, p):
        self.points[name] = np.array(p, dtype=float)


def offset_outline(outline, distance):
    """Move each point of a closed outline (anticlockwise) inwards by a distance."""
    result = []
    n = len(outline)
    for i in range(n):
        p0, p1, p2 = np.array(outline[i - 1]), np.array(outline[i]), np.array(outline[(i + 1) % n])
        d0 = (p1 - p0) / np.linalg.norm(p1 - p0)
        d1 = (p2 - p1) / np.linalg.norm(p2 - p1)
        n0 = np.array([-d0[1], d0[0]])
        n1 = np.array([-d1[1], d1[0]])
        normal = n0 + n1
        normal /= np.linalg.norm(normal)
        scale = distance / max(0.3, np.dot(normal, n0))
        result.append(tuple(p1 + normal * scale))
    return result


def is_anticlockwise(outline):
    area = sum(outline[i - 1][0] * outline[i][1] - outline[i][0] * outline[i - 1][1] for i in range(len(outline)))
    return area > 0


def prism(model, outline, z0, z1, colour, bevel=0.0, axes=(0, 1, 2), origin=(0, 0, 0)):
    """
    An outline (in the plane of the first two axes) extruded along the third
    from z0 to z1, with its top edge chamfered by the bevel. The axes say
    which of x, y and z the outline's x and y and the extrusion go along.
    """
    if not is_anticlockwise(outline):
        outline = outline[::-1]

    def place(x, y, z):
        p = [0.0, 0.0, 0.0]
        p[axes[0]], p[axes[1]], p[axes[2]] = x, y, z
        return tuple(np.array(p) + np.array(origin))

    rings = [[place(x, y, z0) for x, y in outline]]
    if bevel > 0:
        rings.append([place(x, y, z1 - bevel) for x, y in outline])
        rings.append([place(x, y, z1) for x, y in offset_outline(outline, bevel)])
    else:
        rings.append([place(x, y, z1) for x, y in outline])
    loft(model, rings, colour, flip=_flips(axes))


def _flips(axes):
    """Whether a choice of axes is a reflection (which turns the faces inside out)."""
    m = np.zeros((3, 3))
    for i, a in enumerate(axes):
        m[a][i] = 1
    return np.linalg.det(m) < 0


def loft(model, rings, colour, cap_bottom=True, cap_top=True, flip=False):
    """A surface through rings of points (each anticlockwise seen from above), capped at both ends."""
    indices = [model.add_vertices(ring) for ring in rings]
    n = len(rings[0])

    def face(f):
        model.add_face(f[::-1] if flip else f, colour)

    for lower, upper in zip(indices, indices[1:]):
        for i in range(n):
            j = (i + 1) % n
            face([lower[i], lower[j], upper[j], upper[i]])
    if cap_bottom:
        face(indices[0][::-1])
    if cap_top:
        face(indices[-1])


def polygon(sides, rx, ry=None, cx=0.0, cy=0.0, start=None):
    """A regular polygon (or one stretched into an ellipse)."""
    ry = rx if ry is None else ry
    start = math.pi / sides if start is None else start
    return [(cx + rx * math.cos(start + 2 * math.pi * i / sides), cy + ry * math.sin(start + 2 * math.pi * i / sides))
            for i in range(sides)]


def box(model, x0, y0, z0, x1, y1, z1, colour, bevel=0.0):
    prism(model, [(x0, y0), (x1, y0), (x1, y1), (x0, y1)], z0, z1, colour, bevel)


def cylinder(model, cx, cy, z0, z1, r, colour, sides=8, bevel=0.0):
    prism(model, polygon(sides, r, cx=cx, cy=cy), z0, z1, colour, bevel)


def ring(sides, rx, ry, cx, cy, z):
    return [(x, y, z) for x, y in polygon(sides, rx, ry, cx, cy)]


def smooth(points, steps=4):
    """Round off a closed outline (Chaikin's corner cutting)."""
    for _ in range(steps):
        result = []
        for i in range(len(points)):
            p, q = np.array(points[i]), np.array(points[(i + 1) % len(points)])
            result += [tuple(p * 0.75 + q * 0.25), tuple(p * 0.25 + q * 0.75)]
        points = result
    return points


def rounded_rectangle(x0, y0, x1, y1, radius, steps=3):
    """A rectangle with rounded corners (each corner an arc of a few straight sides), anticlockwise."""
    points = []
    for cx, cy, start in ((x1 - radius, y0 + radius, -90), (x1 - radius, y1 - radius, 0), (x0 + radius, y1 - radius, 90),
                          (x0 + radius, y0 + radius, 180)):
        for k in range(steps + 1):
            a = math.radians(start + 90 * k / steps)
            points.append((cx + radius * math.cos(a), cy + radius * math.sin(a)))
    return points


def simplify(points, count):
    """Keep evenly spaced points of a closed outline."""
    step = len(points) / count
    return [points[int(i * step)] for i in range(count)]


# ---------------------------------------------------------------------------
# Drawing
# ---------------------------------------------------------------------------

class Camera:
    """Looks at the model from a direction (a yaw and an elevation, in degrees), in perspective."""

    def __init__(self, yaw, elevation, distance):
        y, e = math.radians(yaw), math.radians(elevation)
        # Turn the model about z by the yaw, then tip it towards us by the elevation
        rz = np.array([[math.cos(y), -math.sin(y), 0], [math.sin(y), math.cos(y), 0], [0, 0, 1]])
        rx = np.array([[1, 0, 0], [0, math.cos(e), -math.sin(e)], [0, math.sin(e), math.cos(e)]])
        self.rotation = rx @ rz
        self.distance = distance

    def view(self, p):
        """A point in view space: x right, y away from the camera, z up."""
        return self.rotation @ p + np.array([0, self.distance, 0])

    def project(self, p):
        v = self.view(p)
        return np.array([v[0] / v[1], -v[2] / v[1]]), v[1]


def render(panels, name, labels=(), extra=None):
    """
    Draw models into a picture, each seen from its own camera and fitted into
    its own part of the picture (given by its left and right edges), and
    return where their named points are in it.
    """
    image = Image.new("RGBA", (WIDTH * SS, HEIGHT * SS), (0, 0, 0, 0))
    depth = np.zeros((HEIGHT * SS, WIDTH * SS))
    places = {}
    for model, camera, left, right in panels:
        places.update(draw_model(image, depth, model, camera, left, right))

    for text, point_name, dx, dy, colour in labels:
        x, y = places[point_name]
        draw_text(image, x + dx, y + dy, text, colour)

    if extra:
        extra(image, places)

    final = image.resize((WIDTH, HEIGHT), Image.LANCZOS)
    final.save(CONTROLLERS / name, optimize=True)
    return {k: (int(round(v[0])), int(round(v[1]))) for k, v in places.items()}


def draw_model(image, depth, model, camera, left, right):
    """Draw a model into part of a picture (using and adding to its depth buffer), returning where its named points are."""
    views = [camera.view(v) for v in model.vertices]
    projected = [np.array([v[0] / v[1], -v[2] / v[1]]) for v in views]

    # Fit the model into its part of the picture
    lo = np.min(projected, axis=0)
    hi = np.max(projected, axis=0)
    scale = min((right - left - 2 * MARGIN) / (hi[0] - lo[0]), (HEIGHT - 2 * MARGIN) / (hi[1] - lo[1]))
    offset = np.array([(left + right) / 2, HEIGHT / 2]) - (lo + hi) / 2 * scale

    def to_picture(p):
        return p * scale + offset

    # Everything is drawn at the supersampled size
    screen = [to_picture(p) * SS for p in projected]
    inverse_depth = [1 / v[1] for v in views]

    # Which faces face us, and their normals (for the creases), by Newell's
    # method, which copes with faces whose first corners are in a line
    normals, facing = [], []
    for indices, _ in model.faces:
        normal = np.zeros(3)
        for i in range(len(indices)):
            p, q = views[indices[i]], views[indices[(i + 1) % len(indices)]]
            normal += np.array([(p[1] - q[1]) * (p[2] + q[2]), (p[2] - q[2]) * (p[0] + q[0]), (p[0] - q[0]) * (p[1] + q[1])])
        length = np.linalg.norm(normal)
        normal = normal / length if length > 0 else normal
        centre = np.mean([views[i] for i in indices], axis=0)
        normals.append(normal)
        facing.append(np.dot(normal, centre) < 0)

    # The depth buffer (of inverse depths, so nearer is bigger), from the
    # faces that face us, split into triangles
    for f, (indices, _) in enumerate(model.faces):
        if facing[f]:
            for a, b, c in triangulate([screen[i] for i in indices]):
                rasterise(depth, [screen[indices[i]] for i in (a, b, c)], [inverse_depth[indices[i]] for i in (a, b, c)])

    # The edges to draw: those of faces that face us, around the outline or at a crease
    edge_faces = {}
    for f, (indices, _) in enumerate(model.faces):
        for i in range(len(indices)):
            key = tuple(sorted((indices[i], indices[(i + 1) % len(indices)])))
            edge_faces.setdefault(key, []).append(f)

    def edge_colour(faces):
        front = [f for f in faces if facing[f]]
        if not front:
            return None
        colours = {model.faces[f][1] for f in faces}
        if len(faces) == 1 or len(colours) > 1 or not all(facing[f] for f in faces):
            # The controls' colour wins over the body's
            return next((c for c in (model.faces[f][1] for f in faces) if c != WHITE), WHITE)
        if any(np.dot(normals[a], normals[b]) < math.cos(math.radians(CREASE)) for a in faces for b in faces if a < b):
            return model.faces[front[0]][1]
        return None

    draw = ImageDraw.Draw(image)
    width = LINE * SS
    for (a, b), faces in edge_faces.items():
        colour = edge_colour(faces)
        if colour is None:
            continue
        for run in visible_runs(depth, screen[a], screen[b], inverse_depth[a], inverse_depth[b]):
            draw.line(run, fill=colour, width=width)
            for x, y in (run[0], run[-1]):
                draw.ellipse([x - width / 2 + 0.5, y - width / 2 + 0.5, x + width / 2 - 0.5, y + width / 2 - 0.5], fill=colour)

    places = {}
    for point_name, p in model.points.items():
        v = camera.view(p)
        places[point_name] = to_picture(np.array([v[0] / v[1], -v[2] / v[1]]))
    return places


def triangulate(points):
    """Split a simple polygon (in either winding) into triangles, by clipping off its ears."""
    pts = [np.array(p) for p in points]
    area = sum(pts[i - 1][0] * pts[i][1] - pts[i][0] * pts[i - 1][1] for i in range(len(pts)))
    sign = 1 if area > 0 else -1
    remaining = list(range(len(pts)))
    triangles = []

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    guard = 0
    while len(remaining) > 3 and guard < 10000:
        guard += 1
        for k in range(len(remaining)):
            i, j, l = remaining[k - 1], remaining[k], remaining[(k + 1) % len(remaining)]
            if sign * cross(pts[i], pts[j], pts[l]) <= 1e-9:
                continue
            inside = False
            for m in remaining:
                if m in (i, j, l):
                    continue
                p = pts[m]
                if (sign * cross(pts[i], pts[j], p) >= 0 and sign * cross(pts[j], pts[l], p) >= 0
                        and sign * cross(pts[l], pts[i], p) >= 0):
                    inside = True
                    break
            if not inside:
                triangles.append((i, j, l))
                remaining.pop(k)
                break
        else:
            break
    if len(remaining) == 3:
        triangles.append(tuple(remaining))
    elif len(remaining) > 3:
        # A polygon that can't be clipped (such as one seen edge on) is fanned
        triangles += [(remaining[0], remaining[k], remaining[k + 1]) for k in range(1, len(remaining) - 1)]
    return triangles


def rasterise(depth, corners, inverse):
    """Draw a triangle's inverse depths into the depth buffer, keeping the nearest."""
    (x0, y0), (x1, y1), (x2, y2) = corners
    area = (x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0)
    if abs(area) < 1e-6:
        return
    left = max(0, int(math.floor(min(x0, x1, x2))))
    right = min(depth.shape[1], int(math.ceil(max(x0, x1, x2))) + 1)
    top = max(0, int(math.floor(min(y0, y1, y2))))
    bottom = min(depth.shape[0], int(math.ceil(max(y0, y1, y2))) + 1)
    if left >= right or top >= bottom:
        return
    xs, ys = np.meshgrid(np.arange(left, right) + 0.5, np.arange(top, bottom) + 0.5)
    w0 = ((x1 - xs) * (y2 - ys) - (y1 - ys) * (x2 - xs)) / area
    w1 = ((x2 - xs) * (y0 - ys) - (y2 - ys) * (x0 - xs)) / area
    w2 = 1 - w0 - w1
    tolerance = -0.02
    inside = (w0 >= tolerance) & (w1 >= tolerance) & (w2 >= tolerance)
    z = w0 * inverse[0] + w1 * inverse[1] + w2 * inverse[2]
    region = depth[top:bottom, left:right]
    np.maximum(region, np.where(inside, z, 0), out=region)


def visible_runs(depth, a, b, inverse_a, inverse_b, step=1.5, tolerance=0.004):
    """The parts of a line that aren't hidden, as lists of points."""
    length = np.linalg.norm(b - a)
    count = max(2, int(length / step) + 1)
    runs, run = [], []
    h, w = depth.shape
    for i in range(count):
        t = i / (count - 1)
        x, y = a + (b - a) * t
        inverse = inverse_a + (inverse_b - inverse_a) * t
        ix, iy = int(x), int(y)
        nearest = depth[max(0, iy - 1):min(h, iy + 2), max(0, ix - 1):min(w, ix + 2)]
        hidden = nearest.size > 0 and inverse < nearest.min() * (1 - tolerance)
        if hidden:
            if len(run) > 1:
                runs.append(run)
            run = []
        else:
            run.append((x, y))
    if len(run) > 1:
        runs.append(run)
    return runs


def draw_text(image, x, y, text, colour, scale=2):
    """Text in the game's font, centred on a point, each of its pixels a block of scale x scale."""
    size = 8 * scale * SS
    left = int(x * SS - size * len(text) / 2)
    top = int(y * SS - size / 2)
    for i, char in enumerate(text):
        index = ord(char) - 32
        glyph = FONT.crop(((index % 32) * 8, (index // 32) * 8, (index % 32) * 8 + 8, (index // 32) * 8 + 8))
        glyph = glyph.resize((size, size), Image.NEAREST)
        image.paste(Image.new("RGBA", glyph.size, colour), (left + i * size, top), glyph)


def write_positions(profile, places):
    """Put the controls' places into a profile, replacing any that are there."""
    path = CONTROLLERS / profile
    lines = path.read_text(encoding="utf-8").split("\n")
    for i, line in enumerate(lines):
        match = re.match(r"^(\s*- )([^@\"]+?)(\s*@\s*\d+,\d+)?\s*$", line)
        if match and match.group(2).strip() in places:
            x, y = places[match.group(2).strip()]
            lines[i] = f"{match.group(1)}{match.group(2).strip()} @ {x},{y}"
    path.write_text("\n".join(lines), encoding="utf-8")


def hat_places(places, hat, centre_name, picture_offset=16):
    """The places of a hat's four directions, around its centre (for hats whose directions are buttons)."""
    x, y = places[centre_name]
    for direction, (dx, dy) in (("Up", (0, -1)), ("Right", (1, 0)), ("Down", (0, 1)), ("Left", (-1, 0))):
        places[f"{hat} {direction}"] = (x + dx * picture_offset, y + dy * picture_offset)


# ---------------------------------------------------------------------------
# Gamepads
# ---------------------------------------------------------------------------

def gamepad_body(model, outline, thickness=16, bevel=5):
    outline = simplify(smooth(outline, 3), 40)
    prism(model, outline, 0, thickness, WHITE, bevel)


def stick(model, name, x, y, top, colour=YELLOW):
    """A thumbstick: a collar, a stem and a dished cap."""
    cylinder(model, x, y, top, top + 2, 12, WHITE, sides=10)
    cylinder(model, x, y, top + 2, top + 7, 4, colour, sides=6)
    cylinder(model, x, y, top + 7, top + 11, 10, colour, sides=10, bevel=2)
    model.point(name, (x, y, top + 11))


def face_button(model, name, x, y, top, colour=YELLOW, r=4.5, height=3):
    cylinder(model, x, y, top, top + height, r, colour, sides=8, bevel=1.2)
    model.point(name, (x, y, top + height))


def dpad(model, names, x, y, top):
    w, l = 4.5, 13
    cross = [(x - w, y - l), (x + w, y - l), (x + w, y - w), (x + l, y - w), (x + l, y + w), (x + w, y + w),
             (x + w, y + l), (x - w, y + l), (x - w, y + w), (x - l, y + w), (x - l, y - w), (x - w, y - w)]
    prism(model, cross, top, top + 3, YELLOW, bevel=1)
    up, down, left, right = names
    model.point(up, (x, y + l - 4, top + 3))
    model.point(down, (x, y - l + 4, top + 3))
    model.point(left, (x - l + 4, y, top + 3))
    model.point(right, (x + l - 4, y, top + 3))


def shoulders(model, left_names, right_names, front, top):
    """The bumpers along the far edge, and the triggers behind them."""
    for side, (bumper, trigger) in ((-1, left_names), (1, right_names)):
        x0, x1 = sorted((side * 30, side * 64))
        box(model, x0, front - 6, top - 9, x1, front + 2, top - 2, YELLOW, bevel=1.5)
        model.point(bumper, ((x0 + x1) / 2, front - 6, top - 2))
        tx0, tx1 = sorted((side * 40, side * 58))
        prism(model, [(front + 2, top - 10), (front + 12, top - 8), (front + 14, top + 2), (front + 4, top)],
              tx0, tx1, YELLOW, axes=(1, 2, 0))
        model.point(trigger, ((tx0 + tx1) / 2, front + 9, top + 1))


def shoulder_labels(bumper, trigger, side):
    return [(bumper, bumper, side * 150, 0, WHITE), (trigger, trigger, side * 110, 0, WHITE)]


XBOX_OUTLINE = [(-62, 30), (-30, 33), (0, 30), (30, 33), (62, 30), (74, 20), (80, 0), (86, -24), (88, -46),
                (82, -60), (68, -64), (54, -56), (40, -40), (24, -32), (0, -30), (-24, -32), (-40, -40), (-54, -56),
                (-68, -64), (-82, -60), (-88, -46), (-86, -24), (-80, 0), (-74, 20)]


def xbox():
    m = Model()
    top = 16
    gamepad_body(m, XBOX_OUTLINE)
    stick(m, "L3", -44, 8, top)
    stick(m, "R3", 24, -16, top)
    dpad(m, ("D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right"), -24, -16, top)
    for name, x, y in (("Y", 46, 20), ("A", 46, -4), ("X", 34, 8), ("B", 58, 8)):
        face_button(m, name, x, y, top)
    face_button(m, "Back", -12, 10, top, r=3, height=2)
    face_button(m, "Start", 12, 10, top, r=3, height=2)
    face_button(m, "Share", 0, 0, top, r=2.5, height=2)
    face_button(m, "Xbox", 0, 21, top, colour=WHITE, r=7, height=2)
    shoulders(m, ("LB", "LT"), ("RB", "RT"), 32, top)
    m.points["Left X"] = m.points["Left Y"] = m.points["L3"]
    m.points["Right X"] = m.points["Right Y"] = m.points["R3"]

    labels = [("A", "A", 0, 0, GREEN), ("B", "B", 0, 0, RED), ("X", "X", 0, 0, CYAN), ("Y", "Y", 0, 0, YELLOW)]
    labels += shoulder_labels("LB", "LT", -1) + shoulder_labels("RB", "RT", 1)
    places = render([(m, Camera(yaw=0, elevation=50, distance=600), 0, WIDTH)], "xbox.png", labels)
    write_positions("xbox.yml", places)


PS_OUTLINE = [(-60, 32), (-24, 34), (24, 34), (60, 32), (72, 24), (78, 6), (84, -20), (92, -48), (90, -64),
              (78, -70), (64, -62), (50, -44), (34, -30), (0, -28), (-34, -30), (-50, -44), (-64, -62), (-78, -70),
              (-90, -64), (-92, -48), (-84, -20), (-78, 6), (-72, 24)]


def playstation():
    m = Model()
    top = 16
    gamepad_body(m, PS_OUTLINE)
    stick(m, "L3", -22, -14, top)
    stick(m, "R3", 22, -14, top)

    # The D-pad's four separate arrows
    for name, (dx, dy) in (("D-pad Up", (0, 1)), ("D-pad Down", (0, -1)), ("D-pad Left", (-1, 0)), ("D-pad Right", (1, 0))):
        cx, cy = -50 + dx * 9, 8 + dy * 9
        w, l = 3.5, 5
        if dx == 0:
            tip = cy - dy * l
            outline = [(cx - w, cy + dy * l), (cx + w, cy + dy * l), (cx + w, tip + dy * 2), (cx, tip), (cx - w, tip + dy * 2)]
        else:
            tip = cx - dx * l
            outline = [(cx + dx * l, cy - w), (cx + dx * l, cy + w), (tip + dx * 2, cy + w), (tip, cy), (tip + dx * 2, cy - w)]
        prism(m, outline, top, top + 3, YELLOW, bevel=0.8)
        m.point(name, (cx, cy, top + 3))

    for name, (dx, dy) in (("Triangle", (0, 1)), ("Cross", (0, -1)), ("Square", (-1, 0)), ("Circle", (1, 0))):
        face_button(m, name, 50 + dx * 11, 8 + dy * 11, top)

    # The touchpad, the Create and Options buttons, and the PS and mic buttons
    box(m, -24, 6, top, 24, 32, top + 2, WHITE, bevel=1.5)
    m.point("Touchpad", (0, 19, top + 2))
    for name, x in (("Create", -31), ("Options", 31)):
        box(m, x - 2, 22, top, x + 2, 30, top + 3, YELLOW, bevel=1)
        m.point(name, (x, 26, top + 3))
    face_button(m, "PS", 0, -6, top, r=4, height=2)
    box(m, -4, -16, top, 4, -12, top + 2, YELLOW, bevel=1)
    m.point("Mic", (0, -14, top + 2))
    shoulders(m, ("L1", "L2"), ("R1", "R2"), 33, top)
    m.points["Left X"] = m.points["Left Y"] = m.points["L3"]
    m.points["Right X"] = m.points["Right Y"] = m.points["R3"]

    def symbols(image, places):
        """The face buttons' symbols, on them."""
        draw = ImageDraw.Draw(image)
        r = 8 * SS
        w = 3 * SS
        for name in ("Triangle", "Cross", "Square", "Circle"):
            x, y = np.array(places[name]) * SS
            if name == "Triangle":
                draw.polygon([(x, y - r), (x + r, y + r * 0.75), (x - r, y + r * 0.75)], outline=GREEN, width=w)
            elif name == "Cross":
                draw.line([(x - r, y - r), (x + r, y + r)], fill=CYAN, width=w)
                draw.line([(x - r, y + r), (x + r, y - r)], fill=CYAN, width=w)
            elif name == "Square":
                draw.rectangle([x - r * 0.8, y - r * 0.8, x + r * 0.8, y + r * 0.8], outline=MAGENTA, width=w)
            else:
                draw.ellipse([x - r, y - r, x + r, y + r], outline=RED, width=w)

    labels = shoulder_labels("L1", "L2", -1) + shoulder_labels("R1", "R2", 1)
    places = render([(m, Camera(yaw=0, elevation=50, distance=600), 0, WIDTH)], "playstation.png", labels, symbols)
    places["Share"] = places["Create"]
    write_positions("ps4.yml", places)
    write_positions("ps5.yml", places)


# ---------------------------------------------------------------------------
# The X52 (whose stick has a bigger head than the real thing, so that its
# buttons can be told apart)
# ---------------------------------------------------------------------------

def x52():
    m = Model()

    # The stick: its base, with the three toggle switches along its front
    sx = -130
    box(m, sx - 52, -46, 0, sx + 52, 46, 30, WHITE, bevel=5)
    for n, x in enumerate((-30, 0, 30)):
        x += sx
        box(m, x - 10, -50, 5, x + 10, -46, 25, WHITE)
        prism(m, [(-50, 14), (-62, 19), (-62, 23), (-50, 18)], x - 3.5, x + 3.5, YELLOW, axes=(1, 2, 0))
        m.point(f"T{n * 2 + 1}", (x, -61, 22))
        m.point(f"T{n * 2 + 2}", (x, -55, 11))

    # The grip, leaning forwards, with the head on top
    sections = [(16, 18, 0, 30), (14, 16, 2, 40), (16, 20, 6, 62), (17, 21, 8, 80), (28, 32, 12, 98), (28, 32, 14, 114),
                (24, 28, 15, 122)]
    loft(m, [ring(12, rx, ry, sx, cy, z) for rx, ry, cy, z in sections], WHITE)
    top = 122

    # The head: the fire button under its red cover, A and B, and the hats
    box(m, sx - 7, 28, top, sx + 7, 40, top + 5, RED, bevel=1.5)
    m.point("Fire", (sx, 34, top + 5))
    face_button(m, "A", sx - 14, 22, top, r=5)
    face_button(m, "B", sx + 14, 24, top, r=5)
    cylinder(m, sx - 11, 2, top, top + 4, 6.5, YELLOW, sides=8, bevel=1.5)
    m.point("Hat 1", (sx - 11, 2, top + 4))
    cylinder(m, sx + 11, 4, top, top + 4, 6, YELLOW, sides=8, bevel=1.5)
    m.point("Hat 2", (sx + 11, 4, top + 4))

    # The mode switch on the left of the head, C on the right, the trigger in
    # front and the pinkie switch below it
    prism(m, polygon(8, 6, cx=12, cy=106), sx - 32, sx - 27, YELLOW, axes=(1, 2, 0))
    m.point("Mode 2", (sx - 32, 12, 106))
    m.point("Mode 1", (sx - 32, 6, 110))
    m.point("Mode 3", (sx - 32, 18, 110))
    box(m, sx + 27, 8, 100, sx + 31, 18, 110, YELLOW, bevel=1)
    m.point("C", (sx + 31, 13, 105))
    prism(m, [(28, 66), (40, 72), (38, 88), (28, 90)], sx - 6, sx + 6, YELLOW, axes=(1, 2, 0))
    m.point("Trigger", (sx, 39, 82))
    m.point("Trigger 2", (sx, 38, 73))
    prism(m, [(22, 44), (31, 46), (31, 56), (23, 58)], sx + 4, sx + 14, YELLOW, axes=(1, 2, 0))
    m.point("Pinkie", (sx + 9, 31, 51))
    m.point("Stick X", (sx, -10, 60))
    m.point("Stick Y", (sx, -10, 60))
    m.point("Twist", (sx, -10, 45))

    stick_model = m

    # The throttle: its base with the display and its buttons, and the slot
    m = Model()
    tx = 110
    box(m, tx - 60, -50, 0, tx + 60, 50, 24, WHITE, bevel=5)
    box(m, tx + 14, -46, 24, tx + 52, -30, 26, WHITE)
    for name, x in (("Function", 18), ("Start/Stop", 33), ("Reset", 48)):
        cylinder(m, tx + x, -24, 24, 27, 3, YELLOW, sides=6)
        m.point(name, (tx + x, -24, 27))
    box(m, tx - 8, -40, 24, tx + 8, 40, 26, WHITE)
    m.point("Throttle", (tx, -40, 26))

    # The handle, held from the left, with the thumb's controls on its right
    sections = [(12, 28, 0, 26), (14, 30, -2, 55), (20, 34, -4, 88), (22, 34, -4, 102), (18, 30, -2, 110)]
    loft(m, [ring(12, rx, ry, tx, cy, z) for rx, ry, cy, z in sections], WHITE)
    top = 110
    face_button(m, "D", tx - 7, 14, top, r=4.5)
    face_button(m, "E", tx - 7, -10, top, r=4.5)
    cylinder(m, tx + 8, 16, top, top + 4, 5, CYAN, sides=10)
    m.point("Upper Rotary", (tx + 8, 16, top + 4))
    side = tx + 21
    prism(m, polygon(8, 5.5, cx=-20, cy=94), side, side + 5, YELLOW, axes=(1, 2, 0))
    m.point("Hat 3", (side + 5, -20, 94))
    prism(m, polygon(8, 3, cx=-4, cy=96), side, side + 4, WHITE, axes=(1, 2, 0))
    prism(m, [(6, 80), (24, 80), (24, 85), (6, 85)], side, side + 3, CYAN, axes=(1, 2, 0))
    m.point("Slider", (side + 3, 15, 82))
    prism(m, polygon(8, 4.5, cx=-24, cy=64), side - 2, side + 3, YELLOW, axes=(1, 2, 0))
    m.point("Clutch", (side + 3, -24, 64))
    prism(m, polygon(10, 6, cx=tx, cy=72), 32, 38, CYAN, axes=(0, 2, 1))
    m.point("Lower Rotary", (tx, 38, 72))

    labels = [(f"T{n}", f"T{n}", 0, 44, WHITE) for n in (2, 4, 6)] + [(f"T{n}", f"T{n}", 0, -56, WHITE) for n in (1, 3, 5)] + [
        ("A", "A", -32, 0, WHITE), ("B", "B", 32, 0, WHITE), ("C", "C", 28, 0, WHITE), ("D", "D", 30, -4, WHITE),
        ("E", "E", -30, 0, WHITE), ("i", "Clutch", 26, 0, WHITE)]
    places = render([(stick_model, Camera(yaw=-30, elevation=24, distance=900), 140, WIDTH // 2 + 40),
                     (m, Camera(yaw=-30, elevation=24, distance=900), WIDTH // 2 - 40, WIDTH - 140)], "x52.png", labels)
    hat_places(places, "Hat 2", "Hat 2", 14)
    hat_places(places, "Hat 3", "Hat 3", 12)
    write_positions("x52.yml", places)
    write_positions("x52-pro.yml", places)


# ---------------------------------------------------------------------------
# The Pro Flight Yoke and its throttle quadrant
# ---------------------------------------------------------------------------

YOKE_OUTLINE = [(-150, 70), (-120, 76), (-104, 40), (-60, 30), (0, 28), (60, 30), (104, 40), (120, 76), (150, 70),
                (158, 20), (156, -60), (140, -80), (112, -72), (96, -30), (60, -22), (0, -20), (-60, -22), (-96, -30),
                (-112, -72), (-140, -80), (-156, -60), (-158, 20)]


def yoke():
    m = Model()

    # The yoke, facing us (its outline is in x and z, and it is 24 deep)
    outline = simplify(smooth(YOKE_OUTLINE, 3), 44)
    prism(m, outline, -12, 12, WHITE, axes=(0, 2, 1))
    front = -12

    # The column it slides in and out of, and the box it comes out of
    prism(m, polygon(8, 12, cx=0, cy=-4), 12, 120, WHITE, axes=(0, 2, 1))
    box(m, -80, 120, -50, 80, 200, 26, WHITE, bevel=6)
    m.point("Yoke Turn", (0, front, 12))
    m.point("Yoke Push", (0, front, -10))

    # The hat on top of the left handle, the button on the left handle, and D on the right
    prism(m, polygon(8, 7, cx=-130, cy=0), 70, 78, YELLOW, bevel=1.5)
    m.point("Hat", (-130, 0, 78))
    prism(m, [(-142, 10), (-130, 10), (-130, 30), (-142, 30)], front - 3, front, YELLOW, axes=(0, 2, 1))
    m.point("Left Button", (-136, front - 3, 20))
    prism(m, polygon(8, 6, cx=134, cy=40), front - 3, front, YELLOW, axes=(0, 2, 1))
    m.point("D", (134, front - 3, 40))

    # The rocker switches A, B and C in the middle
    for letter, x in (("A", -40), ("B", 0), ("C", 40)):
        prism(m, [(x - 7, -8), (x + 7, -8), (x + 7, 20), (x - 7, 20)], front - 2, front, WHITE, axes=(0, 2, 1))
        prism(m, [(x - 5, 8), (x + 5, 8), (x + 5, 17), (x - 5, 17)], front - 5, front - 2, YELLOW, axes=(0, 2, 1))
        prism(m, [(x - 5, -5), (x + 5, -5), (x + 5, 4), (x - 5, 4)], front - 5, front - 2, YELLOW, axes=(0, 2, 1))
        m.point(f"{letter}1", (x, front - 5, 13))
        m.point(f"{letter}2", (x, front - 5, -1))

    # The throttle quadrant, with its three levers and three rocker switches
    qx = 330
    box(m, qx - 75, 40, -70, qx + 75, 140, 10, WHITE, bevel=6)
    colours = (WHITE, CYAN, RED)
    for n, x in enumerate((-45, 0, 45)):
        x += qx
        box(m, x - 3, 50, 10, x + 3, 130, 12, WHITE)
        box(m, x - 2, 66, 12, x + 2, 72, 56, colours[n])
        box(m, x - 14, 60, 56, x + 14, 78, 70, colours[n], bevel=3)
        m.point(f"Lever {n + 1}", (x, 60, 63))
        prism(m, [(x - 9, -60), (x + 9, -60), (x + 9, -20), (x - 9, -20)], 37, 40, WHITE, axes=(0, 2, 1))
        prism(m, [(x - 6, -36), (x + 6, -36), (x + 6, -24), (x - 6, -24)], 34, 37, YELLOW, axes=(0, 2, 1))
        prism(m, [(x - 6, -56), (x + 6, -56), (x + 6, -44), (x - 6, -44)], 34, 37, YELLOW, axes=(0, 2, 1))
        m.point(f"T{n * 2 + 1}", (x, 34, -30))
        m.point(f"T{n * 2 + 2}", (x, 34, -50))

    labels = [("A", "A2", 0, 34, WHITE), ("B", "B2", 0, 34, WHITE), ("C", "C2", 0, 34, WHITE), ("D", "D", 0, -30, WHITE)] + [
        (f"T{n}", f"T{n}", 46, 0, WHITE) for n in range(1, 7)]
    places = render([(m, Camera(yaw=-14, elevation=16, distance=1600), 0, WIDTH)], "yoke.png", labels)
    write_positions("saitek-yoke.yml", places)



# ---------------------------------------------------------------------------
# The GRS Star Wars Flight Yoke: a box on a post, with a handle on each side
# ---------------------------------------------------------------------------

def rotate_new(model, start, pivot, angle):
    """Turn the vertices added since start about the y-axis through a pivot (positive tips the top towards +x)."""
    a = math.radians(angle)
    rotation = np.array([[math.cos(a), 0, math.sin(a)], [0, 1, 0], [-math.sin(a), 0, math.cos(a)]])
    pivot = np.array(pivot, dtype=float)
    for i in range(start, len(model.vertices)):
        model.vertices[i] = rotation @ (model.vertices[i] - pivot) + pivot
    return lambda p: rotation @ (np.array(p, dtype=float) - pivot) + pivot


def grs_yoke():
    m = Model()

    # The housing, a box with a panel on its front: a frame around a square
    # window, with the two function buttons under the window
    box(m, -65, -60, 0, 65, 60, 120, WHITE, bevel=3)
    front = -60
    prism(m, [(-40, 12), (40, 12), (40, 110), (-40, 110)], front - 1, front, CYAN, axes=(0, 2, 1))
    prism(m, [(-30, 42), (30, 42), (30, 102), (-30, 102)], front - 2, front - 1, CYAN, axes=(0, 2, 1))
    for n, x in ((1, -11), (2, 11)):
        prism(m, [(x - 8, 20), (x + 8, 20), (x + 8, 30), (x - 8, 30)], front - 4, front - 1, RED, bevel=0.8, axes=(0, 2, 1))
        m.point(f"Function {n}", (x, front - 4, 25))

    # The post it is mounted on
    cylinder(m, 0, 0, -30, 0, 22, WHITE, sides=10)
    m.point("Yoke Pitch", (0, front, 60))

    # Each side: a collar, a shaft tapering out to the handle, and the handle,
    # a flat grip that leans out at the top, with the thumb button at the top
    # of its front edge and the trigger on its back
    for side, name in ((-1, "L"), (1, "R")):
        sides = 12
        rings = []
        for x, r in ((65, 26), (72, 26), (72, 19), (128, 12)):
            rings.append([(side * x, r * math.cos(2 * math.pi * k / sides), 58 + r * math.sin(2 * math.pi * k / sides))
                          for k in range(sides)])
        loft(m, rings, WHITE, flip=side < 0)

        start = len(m.vertices)
        x = side * 150
        grip = rounded_rectangle(x - 24, -30, x + 24, 150, 12)
        prism(m, grip, -18, 18, WHITE, bevel=0, axes=(0, 2, 1))
        place = rotate_new(m, start, (x, 0, 58), side * 12)

        start = len(m.vertices)
        inner = x - side * 12
        prism(m, polygon(10, 7, cx=inner, cy=132), -22, -18, RED, axes=(0, 2, 1))
        prism(m, [(14, 70), (28, 76), (28, 106), (14, 110)], x - 10, x + 10, YELLOW, axes=(1, 2, 0))
        rotate_new(m, start, (x, 0, 58), side * 12)
        m.point(f"{name} Thumb", place((inner, -22, 132)))
        m.point(f"{name} Trigger", place((x, 28, 90)))
        if side > 0:
            m.point("Yoke Turn", place((x, -18, 58)))

    labels = [("1", "Function 1", 0, 34, WHITE), ("2", "Function 2", 0, 34, WHITE)]
    places = render([(m, Camera(yaw=-24, elevation=14, distance=1400), 0, WIDTH)], "grs-yoke.png", labels)
    write_positions("grs-yoke.yml", places)


if __name__ == "__main__":
    xbox()
    playstation()
    x52()
    yoke()
    grs_yoke()
