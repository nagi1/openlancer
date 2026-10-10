"""Fail when the repo contains retail Freelancer content.

Usage: python tools/content_scan.py

The scan checks every file that git tracks or would add (the submodule is excluded):
  1. Retail file types (disc images, cabinets, fonts, UTF models and textures, cutscenes) are not allowed.
  2. No file may have the same SHA-256 as a retail file in data-manifest/vanilla-1.0.tsv.
Exit code 0 means clean. Exit code 1 means the scan found retail content.
"""
import hashlib
import os
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


def main():
    hashes = retail_hashes()
    if not hashes:
        print(f"warning: no manifest at {MANIFEST}. Only file types are checked.")
    problems = []
    for name in repo_files():
        if os.path.splitext(name)[1].lower() in FORBIDDEN_EXTENSIONS:
            problems.append(f"retail file type: {name}")
        elif hashes and sha256(os.path.join(REPO, name)) in hashes:
            problems.append(f"identical to a retail file: {name}")
    for p in problems:
        print(p)
    print(f"content scan: {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
