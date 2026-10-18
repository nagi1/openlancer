"""Make the icon files of the OpenLancer programs from assets/icon/openlancer.svg.

Usage: python tools/make_icon.py

The script writes two files next to the SVG file:
  openlancer.ico      the Windows icon of the programs, with 16, 24, 32, 48, 64, 128 and 256 pixels
  openlancer-256.png  the window icon of the programs on Linux and macOS, with 256 pixels

Run the script after each change of the SVG file, and commit the three files together. The script needs only the
Python standard library. It draws each size from the vector shapes, so the small sizes stay sharp.

The script reads a small part of SVG and refuses all other content:
  - the elements svg (with viewBox), g, title, desc, rect, circle, ellipse, polygon and path,
  - the path commands M, L, H, V, C, Q and Z, absolute and relative,
  - the attributes fill (#rgb, #rrggbb or none), fill-opacity, fill-rule, opacity, stroke, stroke-width and
    stroke-opacity, and transform (translate, rotate, scale and matrix). Only circle and ellipse can have a stroke.
The tests are in tools/test_make_icon.py: python -B -m unittest discover -s tools
"""
import math
import os
import re
import struct
import sys
import xml.etree.ElementTree as ET
import zlib

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICON_FOLDER = os.path.join(REPO, "assets", "icon")
SOURCE = os.path.join(ICON_FOLDER, "openlancer.svg")
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
PNG_SIZE = 256
SVG = "{http://www.w3.org/2000/svg}"
# The number of sample rows in each pixel row. The coverage along a row is exact, so 16 rows give smooth edges.
SAMPLE_ROWS = 16
# The number of line segments for a full ellipse, a corner of a rect and a curve of a path.
ELLIPSE_SEGMENTS = 192
CORNER_SEGMENTS = 24
CURVE_SEGMENTS = 24
IDENTITY = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
SHAPE_ATTRIBUTES = {"id", "fill", "fill-opacity", "fill-rule", "opacity", "stroke", "stroke-width", "stroke-opacity",
                    "transform"}
ELEMENT_ATTRIBUTES = {
    "rect": {"x", "y", "width", "height", "rx", "ry"},
    "circle": {"cx", "cy", "r"},
    "ellipse": {"cx", "cy", "rx", "ry"},
    "polygon": {"points"},
    "path": {"d"},
}
NUMBER = re.compile(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
PATH_TOKEN = re.compile(r"[A-Za-z]|" + NUMBER.pattern)
# The text between the numbers and the commands of a list.
SEPARATORS = re.compile(r"[\s,]*")
TRANSFORM = re.compile(r"\s*(translate|rotate|scale|matrix)\s*\(([^)]*)\)\s*,?")


class IconError(Exception):
    """The SVG file has content that the script does not read."""


class Shape:
    """One filled area: closed polygons in the units of the viewBox, a color, an alpha and a fill rule."""

    def __init__(self, polygons, color, alpha, rule):
        self.polygons = polygons
        self.color = color
        self.alpha = alpha
        self.rule = rule


def multiply(m, n):
    """The transform that applies n first and m second."""
    a, b, c, d, e, f = m
    a2, b2, c2, d2, e2, f2 = n
    return (a * a2 + c * b2, b * a2 + d * b2, a * c2 + c * d2, b * c2 + d * d2,
            a * e2 + c * f2 + e, b * e2 + d * f2 + f)


def apply(m, point):
    a, b, c, d, e, f = m
    x, y = point
    return (a * x + c * y + e, b * x + d * y + f)


def numbers(text):
    return [float(n) for n in NUMBER.findall(text or "")]


def number_list(text, what):
    """The numbers of a list such as the points of a polygon. Any other text in the list is an error."""
    if not SEPARATORS.fullmatch(NUMBER.sub(" ", text or "")):
        raise IconError(f"{what} must hold only numbers: {text}")
    return numbers(text)


def parse_transform(text):
    """The matrix of an SVG transform list. The functions apply from right to left, as SVG tells."""
    m = IDENTITY
    position = 0
    text = (text or "").strip()
    while position < len(text):
        match = TRANSFORM.match(text, position)
        if not match:
            raise IconError(f"transform not supported: {text}")
        name, values = match.group(1), number_list(match.group(2), "transform")
        if name == "translate" and len(values) in (1, 2):
            n = (1.0, 0.0, 0.0, 1.0, values[0], values[1] if len(values) == 2 else 0.0)
        elif name == "scale" and len(values) in (1, 2):
            n = (values[0], 0.0, 0.0, values[1] if len(values) == 2 else values[0], 0.0, 0.0)
        elif name == "rotate" and len(values) in (1, 3):
            angle = math.radians(values[0])
            cos, sin = math.cos(angle), math.sin(angle)
            n = (cos, sin, -sin, cos, 0.0, 0.0)
            if len(values) == 3:
                cx, cy = values[1], values[2]
                n = multiply((1.0, 0.0, 0.0, 1.0, cx, cy), multiply(n, (1.0, 0.0, 0.0, 1.0, -cx, -cy)))
        elif name == "matrix" and len(values) == 6:
            n = tuple(values)
        else:
            raise IconError(f"transform not supported: {text}")
        m = multiply(m, n)
        position = match.end()
    return m


def parse_color(text):
    """The color of a paint as (r, g, b) from 0 to 1, or None for "none"."""
    text = (text or "").strip()
    if text == "none":
        return None
    if re.fullmatch(r"#[0-9A-Fa-f]{6}", text):
        return tuple(int(text[i:i + 2], 16) / 255 for i in (1, 3, 5))
    if re.fullmatch(r"#[0-9A-Fa-f]{3}", text):
        return tuple(int(text[i] * 2, 16) / 255 for i in (1, 2, 3))
    raise IconError(f"color not supported: {text}")


def ellipse(cx, cy, rx, ry, reverse=False):
    points = []
    for i in range(ELLIPSE_SEGMENTS):
        angle = 2 * math.pi * i / ELLIPSE_SEGMENTS
        points.append((cx + rx * math.cos(angle), cy + ry * math.sin(angle)))
    return points[::-1] if reverse else points


def rounded_rect(x, y, width, height, rx, ry):
    rx, ry = min(rx, width / 2), min(ry, height / 2)
    if rx <= 0 or ry <= 0:
        return [(x, y), (x + width, y), (x + width, y + height), (x, y + height)]
    points = []
    # The corners in the order of the outline: top right, bottom right, bottom left, top left.
    for cx, cy, start in ((x + width - rx, y + ry, -90), (x + width - rx, y + height - ry, 0),
                          (x + rx, y + height - ry, 90), (x + rx, y + ry, 180)):
        for i in range(CORNER_SEGMENTS + 1):
            angle = math.radians(start + 90 * i / CORNER_SEGMENTS)
            points.append((cx + rx * math.cos(angle), cy + ry * math.sin(angle)))
    return points


def parse_path(d):
    """The closed polygons of path data. Each subpath closes, because the script fills each shape."""
    if not SEPARATORS.fullmatch(PATH_TOKEN.sub(" ", d or "")):
        raise IconError(f"path data not supported: {d}")
    tokens = PATH_TOKEN.findall(d or "")
    polygons, current = [], []
    x = y = start_x = start_y = 0.0
    command = None
    i = 0

    def take(count):
        nonlocal i
        if i + count > len(tokens) or any(t.isalpha() for t in tokens[i:i + count]):
            raise IconError(f"path data not complete: {d}")
        values = [float(t) for t in tokens[i:i + count]]
        i += count
        return values

    def close():
        if len(current) > 2:
            polygons.append(list(current))
        current.clear()

    while i < len(tokens):
        if tokens[i].isalpha():
            command = tokens[i]
            i += 1
        elif command is None:
            raise IconError(f"path data must start with a command: {d}")
        relative = command.islower()
        base_x, base_y = (x, y) if relative else (0.0, 0.0)
        kind = command.upper()
        if kind in ("L", "H", "V", "C", "Q") and not current:
            # A command after Z starts a new subpath at the start of the last one, as SVG tells.
            current.append((x, y))
        if kind == "Z":
            close()
            x, y = start_x, start_y
            command = None
        elif kind == "M":
            close()
            dx, dy = take(2)
            x, y = base_x + dx, base_y + dy
            start_x, start_y = x, y
            current.append((x, y))
            # The pairs after a moveto are lineto pairs.
            command = "l" if relative else "L"
        elif kind == "L":
            dx, dy = take(2)
            x, y = base_x + dx, base_y + dy
            current.append((x, y))
        elif kind == "H":
            x = base_x + take(1)[0]
            current.append((x, y))
        elif kind == "V":
            y = base_y + take(1)[0]
            current.append((x, y))
        elif kind in ("C", "Q"):
            values = take(6 if kind == "C" else 4)
            controls = [(base_x + values[j], base_y + values[j + 1]) for j in range(0, len(values), 2)]
            p0 = (x, y)
            for step in range(1, CURVE_SEGMENTS + 1):
                t = step / CURVE_SEGMENTS
                u = 1 - t
                if kind == "C":
                    (x1, y1), (x2, y2), (x3, y3) = controls
                    point = (u ** 3 * p0[0] + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t ** 3 * x3,
                             u ** 3 * p0[1] + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t ** 3 * y3)
                else:
                    (x1, y1), (x2, y2) = controls
                    point = (u * u * p0[0] + 2 * u * t * x1 + t * t * x2, u * u * p0[1] + 2 * u * t * y1 + t * t * y2)
                current.append(point)
            x, y = controls[-1]
        else:
            raise IconError(f"path command not supported: {command}")
    close()
    return polygons


def number(element, name, default=0.0):
    value = element.get(name)
    if value is None:
        return default
    values = numbers(value)
    if len(values) != 1 or not re.fullmatch(r"\s*" + NUMBER.pattern + r"\s*", value):
        raise IconError(f"<{local(element)} {name}> must be a number in user units: {value}")
    return values[0]


def local(element):
    return element.tag[len(SVG):] if element.tag.startswith(SVG) else element.tag


def shapes_of(element, matrix):
    """The shapes of one shape element, in the order that SVG paints them: the fill, then the stroke."""
    name = local(element)
    unknown = set(element.attrib) - SHAPE_ATTRIBUTES - ELEMENT_ATTRIBUTES[name]
    if unknown:
        raise IconError(f"<{name}> attributes not supported: {', '.join(sorted(unknown))}")
    matrix = multiply(matrix, parse_transform(element.get("transform")))
    opacity = number(element, "opacity", 1.0)
    rule = element.get("fill-rule", "nonzero")
    if rule not in ("nonzero", "evenodd"):
        raise IconError(f"fill-rule not supported: {rule}")
    fill = parse_color(element.get("fill", "#000000"))
    stroke = parse_color(element.get("stroke", "none"))
    if name == "rect":
        rx = number(element, "rx", -1.0)
        ry = number(element, "ry", -1.0)
        rx, ry = (rx if rx >= 0 else max(ry, 0.0)), (ry if ry >= 0 else max(rx, 0.0))
        outline = [rounded_rect(number(element, "x"), number(element, "y"), number(element, "width"),
                                number(element, "height"), rx, ry)]
    elif name in ("circle", "ellipse"):
        cx, cy = number(element, "cx"), number(element, "cy")
        rx = number(element, "r") if name == "circle" else number(element, "rx")
        ry = rx if name == "circle" else number(element, "ry")
        outline = [ellipse(cx, cy, rx, ry)]
    elif name == "polygon":
        values = number_list(element.get("points"), "<polygon points>")
        if len(values) < 6 or len(values) % 2:
            raise IconError(f"<polygon points> needs three or more pairs: {element.get('points')}")
        outline = [list(zip(values[0::2], values[1::2]))]
    else:
        outline = parse_path(element.get("d"))

    def placed(polygons):
        return [[apply(matrix, p) for p in polygon] for polygon in polygons]

    result = []
    if fill is not None:
        result.append(Shape(placed(outline), fill, opacity * number(element, "fill-opacity", 1.0), rule))
    if stroke is not None:
        if name not in ("circle", "ellipse"):
            raise IconError(f"<{name}> cannot have a stroke. Draw the outline as a filled shape.")
        half = number(element, "stroke-width", 1.0) / 2
        # The ring is the outer ellipse and the inner ellipse in the other direction, so the nonzero rule leaves a hole.
        ring = [ellipse(cx, cy, rx + half, ry + half), ellipse(cx, cy, max(rx - half, 0.0), max(ry - half, 0.0), True)]
        result.append(Shape(placed(ring), stroke, opacity * number(element, "stroke-opacity", 1.0), "nonzero"))
    return result


def load(path):
    """The viewBox (x, y, width, height) and the shapes of an SVG file, in paint order."""
    root = ET.parse(path).getroot()
    if root.tag != SVG + "svg":
        raise IconError("the root element must be <svg> in the SVG namespace")
    box = number_list(root.get("viewBox"), "viewBox")
    if len(box) != 4 or box[2] <= 0 or box[3] <= 0:
        raise IconError("<svg> needs a viewBox with a width and a height")
    shapes = []

    def visit(element, matrix):
        for child in element:
            name = local(child)
            if name in ("title", "desc"):
                continue
            if name == "g":
                unknown = set(child.attrib) - {"id", "transform"}
                if unknown:
                    raise IconError(f"<g> attributes not supported: {', '.join(sorted(unknown))}")
                visit(child, multiply(matrix, parse_transform(child.get("transform"))))
            elif name in ELEMENT_ATTRIBUTES:
                shapes.extend(shapes_of(child, matrix))
            else:
                raise IconError(f"element not supported: <{name}>")

    visit(root, IDENTITY)
    return tuple(box), shapes


def coverage(polygons, size, rule):
    """The part of each pixel that the polygons cover, from 0 to 1, row by row. The polygons are in pixels."""
    rows = [[] for _ in range(size)]
    for polygon in polygons:
        for (x0, y0), (x1, y1) in zip(polygon, polygon[1:] + polygon[:1]):
            if y0 == y1:
                continue
            direction = 1 if y1 > y0 else -1
            if y0 > y1:
                x0, y0, x1, y1 = x1, y1, x0, y0
            edge = (x0, y0, y1, (x1 - x0) / (y1 - y0), direction)
            for row in range(max(0, int(math.floor(y0))), min(size, int(math.ceil(y1)))):
                rows[row].append(edge)
    inside = (lambda w: w != 0) if rule == "nonzero" else (lambda w: w % 2 != 0)
    weight = 1 / SAMPLE_ROWS
    result = [0.0] * (size * size)
    for row, edges in enumerate(rows):
        if not edges:
            continue
        partial = [0.0] * (size + 1)
        # The pixels between the ends of a span are full. A running sum adds them, so a span costs the same at each
        # width.
        full = [0.0] * (size + 2)
        for sample in range(SAMPLE_ROWS):
            sy = row + (sample + 0.5) / SAMPLE_ROWS
            crossings = sorted((x0 + (sy - y0) * slope, direction)
                               for x0, y0, y1, slope, direction in edges if y0 <= sy < y1)
            winding = 0
            start = 0.0
            for x, direction in crossings:
                was = inside(winding)
                winding += direction
                if not was and inside(winding):
                    start = x
                elif was and not inside(winding):
                    xa, xb = max(start, 0.0), min(x, float(size))
                    if xb <= xa:
                        continue
                    ia, ib = int(xa), int(xb)
                    if ia == ib:
                        partial[ia] += (xb - xa) * weight
                    else:
                        partial[ia] += (ia + 1 - xa) * weight
                        full[ia + 1] += weight
                        full[ib] -= weight
                        partial[ib] += (xb - ib) * weight
        running = 0.0
        for column in range(size):
            running += full[column]
            result[row * size + column] = min(1.0, partial[column] + running)
    return result


def render(box, shapes, size):
    """The RGBA bytes of the image at a size, with straight alpha."""
    x0, y0, width, height = box
    scale_x, scale_y = size / width, size / height
    pixels = [(0.0, 0.0, 0.0, 0.0)] * (size * size)
    for shape in shapes:
        polygons = [[((x - x0) * scale_x, (y - y0) * scale_y) for x, y in polygon] for polygon in shape.polygons]
        r, g, b = shape.color
        for i, cover in enumerate(coverage(polygons, size, shape.rule)):
            a = cover * shape.alpha
            if a <= 0:
                continue
            dr, dg, db, da = pixels[i]
            out = a + da * (1 - a)
            keep = da * (1 - a)
            pixels[i] = ((r * a + dr * keep) / out, (g * a + dg * keep) / out, (b * a + db * keep) / out, out)
    data = bytearray()
    for pixel in pixels:
        data.extend(min(255, int(channel * 255 + 0.5)) for channel in pixel)
    return bytes(data)


def png(rgba, size):
    """A PNG file of RGBA bytes."""
    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    stride = size * 4
    raw = b"".join(b"\0" + rgba[y * stride:(y + 1) * stride] for y in range(size))
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def bmp_icon(rgba, size):
    """An icon image in the BMP form: a 32-bit BGRA bitmap from the bottom row up, then a 1-bit mask."""
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = bytearray()
    for y in range(size - 1, -1, -1):
        for x in range(size):
            r, g, b, a = rgba[(y * size + x) * 4:(y * size + x) * 4 + 4]
            pixels += bytes((b, g, r, a))
    # Each mask row has a whole number of 32-bit words. A set bit marks a pixel without color for old programs.
    mask_stride = (size + 31) // 32 * 4
    mask = bytearray()
    for y in range(size - 1, -1, -1):
        row = bytearray(mask_stride)
        for x in range(size):
            if rgba[(y * size + x) * 4 + 3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row
    return header + bytes(pixels) + bytes(mask)


def ico(images):
    """A Windows icon file of (size, RGBA bytes) pairs. The 256 pixel image is a PNG, the others are bitmaps."""
    entries, data = [], []
    offset = 6 + 16 * len(images)
    for size, rgba in images:
        body = png(rgba, size) if size >= 256 else bmp_icon(rgba, size)
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(body), offset))
        data.append(body)
        offset += len(body)
    return struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(data)


def main():
    box, shapes = load(SOURCE)
    images = [(size, render(box, shapes, size)) for size in ICO_SIZES]
    outputs = {
        os.path.join(ICON_FOLDER, "openlancer.ico"): ico(images),
        os.path.join(ICON_FOLDER, f"openlancer-{PNG_SIZE}.png"): png(dict(images)[PNG_SIZE], PNG_SIZE),
    }
    for path, body in outputs.items():
        with open(path, "wb") as f:
            f.write(body)
        print(f"{os.path.relpath(path, REPO)}: {len(body)} bytes")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except IconError as e:
        print(f"make_icon: {e}", file=sys.stderr)
        sys.exit(1)
