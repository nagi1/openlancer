"""Print the file count and the content digest of an extracted Freelancer data folder.

Usage: python verify_base.py <folder> [--write-manifest <file>]

The digest is the SHA-256 of a sorted list of lines. There is one line for each file:
"<lowercase relative path with '/'>\t<size>\t<lowercase sha256 hex>\n".
The retail English v1.0 disc gives: 8418 5b4ee64cf64bb9c5525cee786f54380170dbde5617d1aaf5ef3655237c9cfaeb

--write-manifest writes these lines to a file. The manifest holds paths, sizes and hashes only,
and content_scan.py uses it to find retail files in the repo.
"""
import argparse
import hashlib
import os


def file_sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def manifest_lines(root):
    lines = []
    for folder, _, names in os.walk(root):
        for name in names:
            path = os.path.join(folder, name)
            rel = os.path.relpath(path, root).replace(os.sep, "/").lower()
            lines.append(f"{rel}\t{os.path.getsize(path)}\t{file_sha256(path)}\n")
    lines.sort()
    return lines


def main():
    parser = argparse.ArgumentParser(description="Digest of an extracted Freelancer data folder.")
    parser.add_argument("folder")
    parser.add_argument("--write-manifest", metavar="FILE")
    args = parser.parse_args()
    if not os.path.isdir(args.folder):
        parser.error(f"not a folder: {args.folder}")

    lines = manifest_lines(args.folder)
    if args.write_manifest:
        with open(args.write_manifest, "w", encoding="utf-8", newline="\n") as f:
            f.writelines(lines)
    print(len(lines), hashlib.sha256("".join(lines).encode()).hexdigest())


if __name__ == "__main__":
    main()
