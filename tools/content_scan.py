"""Fail when the repo contains retail Freelancer content.

Usage: python tools/content_scan.py

The scan checks every file that git tracks or would add (the submodule is excluded):
  1. Retail file types (disc images, cabinets, fonts, UTF models and textures, cutscenes) are not allowed.
     The one exception is a THN patch of a mod (decision D70): an ASCII .thn file below the patches/ folder of a
     mod (the patches/ folder next to its mod.json) whose first statement after its comment block is
     "ol_thn_patch = 1". A patch holds only keys and changed values.
  2. No file may have the same SHA-256 as a retail file in data-manifest/vanilla-1.0.tsv.
Exit code 0 means clean. Exit code 1 means the scan found retail content.

The tests are in tools/test_content_scan.py: python -B -m unittest discover -s tools
"""
import hashlib
import os
import posixpath
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(REPO, "data-manifest", "vanilla-1.0.tsv")
SUBMODULE = "external/"
FORBIDDEN_EXTENSIONS = {
    ".iso", ".cab", ".ttf", ".fl", ".lrpk",
    ".utf", ".cmp", ".3db", ".mat", ".txm", ".vms", ".sph",
    ".ale", ".anm", ".dfm", ".thn", ".sur", ".wmv",
}
PATCHES_FOLDER = "patches"
MOD_MANIFEST = "mod.json"
# The bytes of ASCII text: the printable characters, tab and the line ends.
TEXT_BYTES = frozenset(range(0x20, 0x7F)) | {0x09, 0x0A, 0x0D}
THN_PATCH_START = re.compile(r"ol_thn_patch[ \t]*=[ \t]*1(?![0-9A-Za-z_.])")


def repo_files():
    out = subprocess.run(
        ["git", "-C", REPO, "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        check=True, capture_output=True).stdout
    names = [n.decode("utf-8") for n in out.split(b"\0") if n]
    return [n for n in names if not n.startswith(SUBMODULE) and os.path.isfile(os.path.join(REPO, n))]


def retail_hashes():
    if not os.path.isfile(MANIFEST):
        return set()
    with open(MANIFEST, encoding="utf-8") as f:
        return {line.rstrip("\n").split("\t")[2] for line in f if line.strip()}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def mod_folders(names):
    """The folders of the mods in a list of repo paths: each folder that holds a mod.json, relative to the repo."""
    return {posixpath.dirname(n) for n in names if posixpath.basename(n) == MOD_MANIFEST}


def in_mod_patches(name, mods):
    """True when the file is below the patches/ folder of a mod: the patches/ folder next to its mod.json.

    Another folder with the name patches/ does not count. For example, the overlay serves a file in files/DATA/patches/
    of a mod as a whole file.
    """
    parts = name.split("/")
    return any(parts[i] == PATCHES_FOLDER and "/".join(parts[:i]) in mods for i in range(len(parts) - 1))


def is_thn_patch(name, data, mods):
    """True for a THN patch of a mod: ASCII text in the patches/ folder of a mod that starts with "ol_thn_patch = 1".

    name is the path relative to the repo, with "/" separators. mods is the set of mod folders (see mod_folders). Lua
    3.2 has only line comments, so the comment block is the lines at the top that are empty or start with "--".
    """
    if not in_mod_patches(name, mods) or not all(b in TEXT_BYTES for b in data):
        return False
    for line in data.decode("ascii").splitlines():
        text = line.strip(" \t")
        if text and not text.startswith("--"):
            return THN_PATCH_START.match(text) is not None
    return False


def problem(name, path, hashes, mods):
    """The problem of one file, or None. path is the file on disk. mods is the set of mod folders (see mod_folders)."""
    extension = os.path.splitext(name)[1].lower()
    if extension in FORBIDDEN_EXTENSIONS:
        if extension != ".thn":
            return f"retail file type: {name}"
        with open(path, "rb") as f:
            if not is_thn_patch(name, f.read(), mods):
                return f"retail file type: {name} (a .thn file must be a THN patch in the patches/ folder of a mod)"
    if hashes and sha256(path) in hashes:
        return f"identical to a retail file: {name}"
    return None


def main():
    hashes = retail_hashes()
    if not hashes:
        print(f"warning: no manifest at {MANIFEST}. Only file types are checked.")
    names = repo_files()
    mods = mod_folders(names)
    problems = [p for p in (problem(name, os.path.join(REPO, name), hashes, mods) for name in names) if p]
    for p in problems:
        print(p)
    print(f"content scan: {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
