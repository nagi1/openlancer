"""Tests of tools/content_scan.py. Run them with: python -B -m unittest discover -s tools"""
import hashlib
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import content_scan  # noqa: E402

PATCH = (b"-- My mod: a wider Trent shot.\r\n"
         b"\r\n"
         b"  -- A second comment line.\r\n"
         b"ol_thn_patch = 1\r\n"
         b"entities = {\r\n"
         b"\t{ ol_match = \"Cam_02\", ol_set = { [\"cameraprops.fovh\"] = 20 } }\r\n"
         b"}\r\n")
PATCH_NAME = "samples/mods/ol-sample/patches/DATA/SCRIPTS/STORY/s002xe.thn"
# The mod folders of the tests: each holds a mod.json.
MODS = frozenset({"samples/mods/ol-sample", "mods/m"})


class ContentScanTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.folder.cleanup()

    def problem(self, name, data, hashes=frozenset()):
        path = os.path.join(self.folder.name, "file")
        with open(path, "wb") as f:
            f.write(data)
        return content_scan.problem(name, path, hashes, MODS)

    def assert_refused(self, name, data):
        self.assertRegex(self.problem(name, data) or "", "^retail file type: ")

    def test_an_ascii_thn_patch_in_a_patches_folder_passes(self):
        self.assertIsNone(self.problem(PATCH_NAME, PATCH))
        self.assertIsNone(self.problem("mods/m/patches/x.thn", b"ol_thn_patch=1"))
        self.assertIsNone(self.problem("mods/m/patches/x.thn", b"\n\t ol_thn_patch = 1 -- the version\n"))

    def test_a_bytecode_thn_fails(self):
        bytecode = b"\x1bLua\x32\x01\x04\x00\x00\x00ol_thn_patch = 1"
        self.assert_refused(PATCH_NAME, bytecode)

    def test_a_text_thn_that_does_not_start_with_the_patch_version_fails(self):
        for text in (b"duration = 5\nol_thn_patch = 1\n",
                     b"-- ol_thn_patch = 1\n",
                     b"ol_thn_patch = 10\n",
                     b"ol_thn_patch = 1.5\n",
                     b"ol_thn_patch_x = 1\n",
                     b"",
                     b"-- only a comment\n"):
            with self.subTest(text=text):
                self.assert_refused(PATCH_NAME, text)

    def test_a_thn_patch_outside_a_patches_folder_fails(self):
        for name in ("samples/mods/ol-sample/files/DATA/SCRIPTS/x.thn", "x.thn", "patches.thn",
                     "mods/m/patches_old/x.thn", "mods/m/Patches/x.thn"):
            with self.subTest(name=name):
                self.assert_refused(name, PATCH)

    def test_a_thn_patch_in_a_patches_folder_that_is_not_the_one_of_a_mod_fails(self):
        # The overlay serves a file below files/ as a whole file, also in a folder with the name patches.
        for name in ("samples/mods/ol-sample/files/DATA/patches/x.thn",
                     "samples/mods/ol-sample/files/patches/DATA/x.thn",
                     "patches/librelancer/x.thn", "docs/patches/x.thn", "mods/other/patches/x.thn"):
            with self.subTest(name=name):
                self.assert_refused(name, PATCH)
        # A patch of an install path with a patches folder in it is still a patch of the mod.
        self.assertIsNone(self.problem("mods/m/patches/DATA/patches/x.thn", PATCH))

    def test_the_mod_folders_are_the_folders_that_hold_a_mod_json(self):
        names = ["samples/mods/ol-sample/mod.json", "samples/mods/ol-sample/patches/DATA/goods.ini",
                 "samples/mods/ol-sample/files/mod.json.txt", "README.md"]
        self.assertEqual({"samples/mods/ol-sample"}, content_scan.mod_folders(names))

    def test_a_thn_patch_with_other_characters_fails(self):
        self.assert_refused(PATCH_NAME, PATCH + b"-- caf\xe9\n")
        self.assert_refused(PATCH_NAME, PATCH + b"\x00")

    def test_the_hash_check_stays_for_a_thn_patch(self):
        hashes = {hashlib.sha256(PATCH).hexdigest()}
        self.assertEqual(f"identical to a retail file: {PATCH_NAME}", self.problem(PATCH_NAME, PATCH, hashes))

    def test_other_retail_types_still_fail_and_other_files_pass(self):
        self.assertEqual("retail file type: mods/m/patches/x.cmp", self.problem("mods/m/patches/x.cmp", PATCH))
        self.assertIsNone(self.problem("mods/m/patches/DATA/goods.ini", b"[Good]\nnickname = x\n"))


if __name__ == "__main__":
    unittest.main()
