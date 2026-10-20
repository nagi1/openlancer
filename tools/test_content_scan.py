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


def sha256(data):
    return hashlib.sha256(data).hexdigest()


class ContentRuleTests(unittest.TestCase):
    """The rules after the retail file types: signatures, binary files, native source, Markdown, decompiled code."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.folder.cleanup()

    def problem(self, name, data, hashes=frozenset(), allow=None):
        path = os.path.join(self.folder.name, "file")
        with open(path, "wb") as f:
            f.write(data)
        return content_scan.problem(name, path, hashes, MODS, allow or content_scan.Allow())

    def assert_decompiled(self, rule, line, name="src/X.cs"):
        with self.subTest(line=line):
            self.assertRegex(self.problem(name, line.encode("utf-8") + b"\n") or "",
                             rf"^decompiled-code pattern \({rule}\): {name}:1: ")

    def test_a_thumbnail_of_the_editor_fails_by_its_type_and_its_signature(self):
        # The editor renders thumbnails from game data (section 7.5 of the M23 design).
        self.assertEqual("retail file type: docs/x.olthumb (a thumbnail that the editor renders from game data)",
                         self.problem("docs/x.olthumb", b"OLTH\x01\x89PNG"))
        self.assertEqual("retail file signature: docs/preview.png starts like a thumbnail that the editor renders "
                         "from game data", self.problem("docs/preview.png", b"OLTH\x01\x89PNG"))

    def test_a_retail_signature_fails_under_any_name(self):
        for data in (b"BINI\x01\x00\x00\x00", b"UTF \x01\x01\x00\x00", b"\x1bLua\x32\x01\x04"):
            for name in ("mods/m/files/DATA/goods.ini", "docs/notes.txt", "x"):
                with self.subTest(data=data, name=name):
                    self.assertRegex(self.problem(name, data) or "", f"^retail file signature: {name} starts like ")

    def test_a_binary_file_fails_unless_the_allow_list_has_it(self):
        png = b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR"
        self.assertRegex(self.problem("docs/images/a.png", png) or "", "^binary file: docs/images/a.png ")
        allow = content_scan.Allow([("docs/images/*.png", "binary"), ("x.dll", "binary")])
        self.assertIsNone(self.problem("docs/images/a.png", png, allow=allow))
        self.assertEqual(["allow-list entry allows nothing: x.dll\tbinary"], allow.unused())
        self.assertEqual([], allow.unused(fork=True))

    def test_native_source_fails(self):
        for name in ("src/a.cpp", "src/a.c", "src/a.h", "src/a.hpp", "src/a.asm"):
            with self.subTest(name=name):
                self.assertRegex(self.problem(name, b"int main() { return 0; }\n") or "", f"^native source: {name} ")

    def test_a_markdown_file_outside_the_list_fails(self):
        for name in ("SUMMARY.md", "docs/NEW.md"):
            with self.subTest(name=name):
                self.assertRegex(self.problem(name, b"# Notes\n") or "", f"^Markdown file not in the list: {name} ")
        for name in ("docs/PLAY.md", "docs/GAME_LINK.md", "docs/MCP.md", "docs/MISSIONS.md",
                     "data-manifest/README.md"):
            with self.subTest(name=name):
                self.assertIsNone(self.problem(name, b"# Title\n\nText.\n"))

    def test_decompiler_output_fails_in_code(self):
        for rule, line in (
                ("auto-label", "int FUN_00412a30(int a)"),
                ("auto-label", "sub_4F3C20();"),
                ("auto-variable", "int param_1 = 0;"),
                ("auto-variable", "uVar3 = 0;"),
                ("auto-variable", "undefined4 x;"),
                ("auto-variable", "extraout_EAX = 0;"),
                ("decompiler-helper", "x = CONCAT31(a, b);"),
                ("decompiler-helper", "*(_DWORD *)p = 0;"),
                ("retail-address", "Call(0x004F3C20);"),
                ("retail-address", "var f = 0x6260F00;"),
                ("tool-marker", "// From the Ghidra output."),
                ("tool-marker", "/* IDA Pro listing */"),
                ("tool-marker", "// Freelancer-Decomp says so")):
            self.assert_decompiled(rule, line)

    def test_the_known_safe_forms_pass(self):
        lines = ("const uint A = 0x42F6E97C;",
                 "const uint B = 0x4B800000;",
                 "const int Color = 0x50A0E0;",
                 "const uint C = 0x40000000;",
                 "var text = ThnDecompile.Decompile(bytes);",
                 "class DecompiledThn {}",
                 "var decompiled = 1;",
                 "Log(\"SDL3 gave undefined mouse button\");",
                 "int myVar1 = 0, tmpVar2 = 0;",
                 "static int LOWORD(nint v) => (int)(v & 0xFFFF);",
                 "var localPos = Vector3.Zero;",
                 "// the THN file of ThnDecompile.cs")
        self.assertIsNone(self.problem("src/Safe.cs", "\n".join(lines).encode("utf-8")))

    def test_a_block_comment_over_several_lines_counts(self):
        self.assertEqual("decompiled-code pattern (tool-marker): src/X.cs:2: Ghidra",
                         self.problem("src/X.cs", b"/*\n * Ghidra listing\n */\nclass X {}\n"))

    def test_the_allow_list_permits_one_rule_of_one_file(self):
        allow = content_scan.Allow([("src/A.cs", "tool-marker")])
        self.assertIsNone(self.problem("src/A.cs", b"// Ghidra names this table.\n", allow=allow))
        self.assertRegex(self.problem("src/B.cs", b"// Ghidra names this table.\n", allow=allow) or "",
                         r"^decompiled-code pattern \(tool-marker\): src/B.cs:1: ")
        self.assertEqual("decompiled-code pattern (auto-variable): src/A.cs:2: param_1",
                         self.problem("src/A.cs", b"// Ghidra names this table.\nint param_1;\n", allow=allow))
        self.assertEqual([], allow.unused())

    def test_added_lines_of_a_diff(self):
        diff = (b"diff --git a/src/A.cs b/src/A.cs\n"
                b"index 1111111..2222222 100644\n"
                b"--- a/src/A.cs\n"
                b"+++ b/src/A.cs\n"
                b"@@ -3,0 +4,2 @@ class A\n"
                b"+int a = 0;\n"
                b"+int param_1 = 0;\n"
                b"@@ -10,0 +12 @@ class A\n"
                b"+++ counter;\n")
        hunks = list(content_scan.added_lines(diff))
        self.assertEqual([("src/A.cs", 4, ["int a = 0;", "int param_1 = 0;"]), ("src/A.cs", 12, ["++ counter;"])],
                         hunks)
        name, start, lines = hunks[0]
        self.assertEqual("decompiled-code pattern (auto-variable): external/Librelancer/src/A.cs:5: param_1",
                         content_scan.decompiled_problem(f"{content_scan.FORK}/{name}", lines,
                                                         content_scan.Allow(), start))

    def test_other_forms_of_decompiler_output_fail(self):
        for rule, line in (
                ("auto-label", "x = PTR_FUN_005e1c40;"),
                ("auto-label", "_DAT_005e1c44 = 0;"),
                ("auto-label", "goto switchD_00412a5c_caseD_3;"),
                ("auto-label", "goto LABEL_12;"),
                ("auto-label", "v3 = j_sub_401020(a1);"),
                ("auto-variable", "void *pvVar4;"),
                ("auto-variable", "x = in_stack_00000008;"),
                ("auto-variable", "extraout_var = 0;"),
                ("decompiler-helper", "float10 x;"),
                ("ida-comment", "int v5; // [esp+14h] [ebp-4h]"),
                ("ida-comment", "int v3; // eax"),
                ("decompiler-helper", "v7 = __ROL4__(v3, 5);"),
                ("decompiler-helper", "v8 = BYTE1(v3);"),
                ("decompiler-helper", "int __usercall f@<eax>(int a1@<ecx>)"),
                ("decompiler-helper", "g = (code *)0x4f3c20;"),
                ("retail-address", "Call(0x004F3C20u);")):
            self.assert_decompiled(rule, line)

    def test_a_power_of_two_is_not_an_address(self):
        self.assertIsNone(self.problem("src/X.cs", b"const uint WS_DLGFRAME = 0x00400000;\n"))
        self.assertIsNone(self.problem("src/X.cs", b"const uint Size = 0x00400000u;\n"))
        self.assert_decompiled("retail-address", "Call(0x00400001);")

    def test_markdown_gets_only_the_whole_line_rules(self):
        self.assertEqual("decompiled-code pattern (auto-label): docs/PLAY.md:1: FUN_00412a30",
                         self.problem("docs/PLAY.md", b"Call FUN_00412a30 first.\n"))
        self.assertIsNone(self.problem("docs/PLAY.md", b"No code from Freelancer-Decomp.\n"))
        self.assertIsNone(self.problem("docs/PLAY.md", b"Open a decompiled THN file.\n"))

    def test_the_hash_rule_ignores_line_ends(self):
        self.assertEqual("identical to a retail file: x.txt",
                         self.problem("x.txt", b"a\nb\n", {sha256(b"a\r\nb\r\n")}))
        self.assertEqual("identical to a retail file: x.txt",
                         self.problem("x.txt", b"a\r\nb\r\n", {sha256(b"a\nb\n")}))
        binary = b"a\r\n\x00b\r\n"
        allow = content_scan.Allow([("x.bin", "binary")])
        self.assertIsNone(self.problem("x.bin", binary, {sha256(b"a\n\x00b\n")}, allow))
        self.assertEqual("identical to a retail file: x.bin", self.problem("x.bin", binary, {sha256(binary)}, allow))

    def test_the_hash_rule_reads_each_manifest(self):
        folder = os.path.join(self.folder.name, "manifests")
        os.makedirs(folder)
        for build, data in (("vanilla-1.0", b"retail\n"), ("hde-0.7.1", b"hd edition\n")):
            with open(os.path.join(folder, build + ".tsv"), "w", encoding="utf-8") as f:
                f.write(f"data/{build}.ini\t{len(data)}\t{sha256(data)}\n")
        with open(os.path.join(folder, "README.md"), "w", encoding="utf-8") as f:
            f.write("not a manifest\n")
        hashes = content_scan.retail_hashes(folder)
        self.assertEqual({sha256(b"retail\n"), sha256(b"hd edition\n")}, hashes)
        self.assertEqual("identical to a retail file: mods/m/files/x.ini",
                         self.problem("mods/m/files/x.ini", b"hd edition\r\n", hashes))
        self.assertEqual(2, len(content_scan.manifests(folder)))
        self.assertIn(os.path.join(content_scan.MANIFEST_FOLDER, "hde-0.7.1.tsv"), content_scan.manifests())


if __name__ == "__main__":
    unittest.main()
