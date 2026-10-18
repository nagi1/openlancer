"""Tests of tools/make_icon.py. Run them with: python -B -m unittest discover -s tools"""
import os
import struct
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_icon as mi  # noqa: E402

SQUARE = [(1.0, 1.0), (3.0, 1.0), (3.0, 3.0), (1.0, 3.0)]


def element(text):
    return ET.fromstring(f'<svg xmlns="http://www.w3.org/2000/svg">{text}</svg>')[0]


def load_text(text):
    with tempfile.TemporaryDirectory() as folder:
        path = os.path.join(folder, "icon.svg")
        with open(path, "w", encoding="utf-8") as f:
            f.write(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 4 4">{text}</svg>')
        return mi.load(path)


def png_pixels(data):
    """The RGBA bytes of a PNG file that make_icon wrote: one IDAT chunk and filter 0 on each row."""
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    width, height = struct.unpack(">II", data[16:24])
    position, idat = 8, b""
    while position < len(data):
        length, kind = struct.unpack(">I4s", data[position:position + 8])
        if kind == b"IDAT":
            idat += data[position + 8:position + 8 + length]
        position += 12 + length
    raw = zlib.decompress(idat)
    stride = width * 4
    return width, height, b"".join(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)] for y in range(height))


class MakeIconTests(unittest.TestCase):
    def test_the_svg_of_the_repo_uses_only_the_supported_content(self):
        box, shapes = mi.load(mi.SOURCE)
        self.assertEqual((0.0, 0.0, 256.0, 256.0), box)
        self.assertGreater(len(shapes), 1)
        rgba = mi.render(box, shapes, 16)
        # The tile fills the middle of the icon, and its round corner leaves the corner pixel clear.
        self.assertEqual(255, rgba[(8 * 16 + 8) * 4 + 3])
        self.assertEqual(0, rgba[3])

    def test_a_square_covers_its_pixels_and_parts_of_pixels(self):
        cover = mi.coverage([SQUARE], 4, "nonzero")
        self.assertEqual([0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0], [round(c, 6) for c in cover])
        half = mi.coverage([[(0.5, 0.0), (4.0, 0.0), (4.0, 4.0), (0.5, 4.0)]], 4, "nonzero")
        self.assertAlmostEqual(0.5, half[0])
        self.assertAlmostEqual(1.0, half[1])

    def test_the_fill_rules_differ_for_a_hole_in_the_same_direction(self):
        outer = [(0.0, 0.0), (4.0, 0.0), (4.0, 4.0), (0.0, 4.0)]
        self.assertAlmostEqual(1.0, mi.coverage([outer, SQUARE], 4, "nonzero")[5])
        self.assertAlmostEqual(0.0, mi.coverage([outer, SQUARE], 4, "evenodd")[5])
        self.assertAlmostEqual(1.0, mi.coverage([outer, SQUARE], 4, "evenodd")[0])

    def test_a_stroke_on_a_circle_is_a_ring(self):
        box, shapes = load_text('<circle cx="2" cy="2" r="1.5" fill="none" stroke="#ffffff" stroke-width="1"/>')
        self.assertEqual(1, len(shapes))
        rgba = mi.render(box, shapes, 4)
        self.assertEqual(0, rgba[(1 * 4 + 1) * 4 + 3] // 128)
        self.assertGreater(rgba[(0 * 4 + 1) * 4 + 3], 0)

    def test_the_path_commands_make_closed_polygons(self):
        polygons = mi.parse_path("M0,0 h10 v10 H0 Z m20,0 l5,0 0,5 z L1 1 2 1")
        self.assertEqual([(0, 0), (10, 0), (10, 10), (0, 10)], polygons[0])
        self.assertEqual([(20, 0), (25, 0), (25, 5)], polygons[1])
        # A command after Z starts at the start of the last subpath.
        self.assertEqual([(20, 0), (1, 1), (2, 1)], polygons[2])

    def test_a_curve_ends_at_its_end_point(self):
        polygon = mi.parse_path("M0,0 C0,10 10,10 10,0 Q5,-5 0,0 Z")[0]
        self.assertEqual(1 + 2 * mi.CURVE_SEGMENTS, len(polygon))
        self.assertEqual((10.0, 0.0), polygon[mi.CURVE_SEGMENTS])
        self.assertAlmostEqual(7.5, polygon[mi.CURVE_SEGMENTS // 2][1])

    def test_a_transform_applies_from_right_to_left(self):
        matrix = mi.parse_transform("translate(10 0) rotate(90)")
        x, y = mi.apply(matrix, (1.0, 0.0))
        self.assertAlmostEqual(10.0, x)
        self.assertAlmostEqual(1.0, y)
        x, y = mi.apply(mi.parse_transform("rotate(180 5 5)"), (0.0, 0.0))
        self.assertAlmostEqual(10.0, x)
        self.assertAlmostEqual(10.0, y)

    def test_content_outside_the_supported_part_is_refused(self):
        refused = [
            '<text>A</text>',
            '<g opacity="0.5"><rect width="1" height="1"/></g>',
            '<rect width="1" height="1" stroke="#fff"/>',
            '<circle r="1" stroke-dasharray="2"/>',
            '<rect width="1" height="1" fill="url(#a)"/>',
            '<rect width="50%" height="1"/>',
            '<path d="M0,0 A1,1 0 0 1 2,2 Z"/>',
            '<polygon points="0,0 1,0 1,x"/>',
            '<rect width="1" height="1" transform="skewX(10)"/>',
        ]
        for text in refused:
            with self.subTest(text=text), self.assertRaises(mi.IconError):
                load_text(text)

    def test_the_png_holds_the_pixels(self):
        rgba = bytes(range(2 * 2 * 4))
        self.assertEqual((2, 2, rgba), png_pixels(mi.png(rgba, 2)))

    def test_the_ico_has_a_png_for_256_pixels_and_bitmaps_below(self):
        small = bytes([255, 0, 0, 255]) * (16 * 16 - 1) + bytes(4)
        large = bytes([0, 0, 255, 255]) * (256 * 256)
        data = mi.ico([(16, small), (256, large)])
        reserved, kind, count = struct.unpack("<HHH", data[:6])
        self.assertEqual((0, 1, 2), (reserved, kind, count))
        entries = [struct.unpack("<BBBBHHII", data[6 + 16 * i:22 + 16 * i]) for i in range(count)]
        self.assertEqual([(16, 16), (0, 0)], [(e[0], e[1]) for e in entries])
        bitmap = data[entries[0][7]:entries[0][7] + entries[0][6]]
        width, height, planes, bits = struct.unpack("<iiHH", bitmap[4:16])
        self.assertEqual((16, 32, 1, 32), (width, height, planes, bits))
        # The bitmap starts at the bottom row, in the order B, G, R, A. The clear pixel is the last pixel of that row.
        self.assertEqual(bytes([0, 0, 255, 255]), bitmap[40:44])
        self.assertEqual(bytes(4), bitmap[40 + 15 * 4:40 + 16 * 4])
        # The mask marks the clear pixel: the last bit of the first mask row.
        mask = bitmap[40 + 16 * 16 * 4:]
        self.assertEqual(16 * 4, len(mask))
        self.assertEqual(0x01, mask[1])
        self.assertEqual(256, png_pixels(data[entries[1][7]:entries[1][7] + entries[1][6]])[0])


if __name__ == "__main__":
    unittest.main()
