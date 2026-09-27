#!/usr/bin/env python3
"""Verify a packed Velopack release contains every declared delta payload file."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import zipfile


def packed_path(relative: str) -> str:
    path = PurePosixPath(relative)
    if ("\\" in relative or path.is_absolute() or not path.parts or
            any(part in ("", ".", "..") for part in relative.split("/"))):
        raise ValueError(f"Invalid payload path: {relative!r}")
    return "lib/app/runtime-payload/" + path.as_posix()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path, help="Velopack full .nupkg")
    args = parser.parse_args()

    with zipfile.ZipFile(args.package) as package:
        descriptor = json.loads(package.read("lib/app/delta-release.json"))
        files = descriptor["payloadFiles"]
        if not isinstance(files, list) or not files:
            raise ValueError("Delta release has no payload inventory")
        seen = set()
        for entry in files:
            relative = entry["path"]
            name = packed_path(relative)
            if name in seen:
                raise ValueError(f"Duplicate payload path: {relative}")
            seen.add(name)
            info = package.getinfo(name)
            if info.file_size != entry["size"]:
                raise ValueError(f"Packed size differs from descriptor: {relative}")
            digest = hashlib.sha256()
            with package.open(info) as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(chunk)
            if digest.hexdigest().lower() != entry["sha256"].lower():
                raise ValueError(f"Packed hash differs from descriptor: {relative}")
        for name in ("lib/app/delta-pack/manifest.json", "lib/app/delta-decoder/decoder.json",
                     "lib/app/delta-uninstaller.exe"):
            package.getinfo(name)
    print(f"Verified {len(seen)} packed payload files and release inputs: {args.package}")


if __name__ == "__main__":
    main()
