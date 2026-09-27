#!/usr/bin/env python3
"""Package the staged delta data for verified release download.

The installer executable is distributed separately. This archive contains the
versioned pack, runtime payload, decoder, descriptor, and removal helper.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile
import zipfile


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release", type=Path, required=True,
                        help="directory produced by stage-delta-release.py")
    parser.add_argument("--output", type=Path, required=True,
                        help="new versioned .zip archive")
    parser.add_argument("--local-proof", action="store_true",
                        help="permit a local proof bundle for download tests")
    args = parser.parse_args()

    release = args.release.resolve(strict=True)
    output = args.output.resolve()
    if output.exists() or output.suffix.lower() != ".zip" or output.is_relative_to(release):
        parser.error("Output must be a new .zip outside the release directory")
    if (release / "runtime-payload/LOCAL-PROOF-ONLY.txt").exists() and not args.local_proof:
        parser.error("Local proof bundles cannot be packaged for download")
    if release.is_symlink() or any(path.is_symlink() for path in release.rglob("*")):
        parser.error("Release directories must not contain symbolic links")

    descriptor = json.loads((release / "delta-release.json").read_text(encoding="utf-8"))
    manifest = json.loads((release / "delta-pack/manifest.json").read_text(encoding="utf-8"))
    for key in ("gameVersion", "optimumVersion", "rid"):
        if descriptor.get(key) != manifest.get(key):
            parser.error("Release descriptor and delta pack do not match: " + key)
    rid = descriptor["rid"]
    if rid not in ("win-x64", "linux-x64"):
        parser.error("Unsupported release platform")
    expected_name = (f"Optimum-v{descriptor['optimumVersion']}-"
                     f"VS{descriptor['gameVersion']}-{rid}-Delta.zip")
    if output.name != expected_name:
        parser.error("Archive name must be " + expected_name)

    removal = "delta-uninstaller.exe" if rid == "win-x64" else "delta-uninstaller"
    if descriptor.get("uninstaller", {}).get("path") != removal:
        parser.error("Release has no matching removal helper")
    selected = []
    for relative in ("delta-release.json", removal):
        selected.append(release / relative)
    for folder in ("delta-pack", "runtime-payload", "delta-decoder"):
        directory = release / folder
        if not directory.is_dir():
            parser.error("Missing release directory: " + folder)
        selected.extend(path for path in directory.rglob("*") if path.is_file())
    for path in selected:
        if not path.is_file() or path.stat().st_size == 0:
            parser.error("Missing or empty release file: " + str(path))

    uninstaller = descriptor["uninstaller"]
    if (uninstaller.get("size") != (release / removal).stat().st_size or
            uninstaller.get("sha256", "").lower() != digest(release / removal)):
        parser.error("Removal helper does not match the release descriptor")
    payload_root = release / "runtime-payload"
    inventoried = set()
    for entry in descriptor.get("payloadFiles", []):
        relative = entry.get("path", "")
        path = payload_root / relative
        if (not relative or not path.is_file() or
                path.resolve().is_relative_to(payload_root) is False or
                relative in inventoried or
                path.stat().st_size != entry.get("size") or
                digest(path) != entry.get("sha256", "").lower()):
            parser.error("Runtime payload does not match the release descriptor: " + relative)
        inventoried.add(relative)
    actual_payload = {path.relative_to(payload_root).as_posix()
                      for path in payload_root.rglob("*") if path.is_file()}
    if inventoried != actual_payload:
        parser.error("Release payload inventory is incomplete")

    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(prefix="delta-download-", suffix=".zip",
                                     dir=output.parent, delete=False) as temporary:
        staged = Path(temporary.name)
    try:
        with zipfile.ZipFile(staged, "w", compression=zipfile.ZIP_DEFLATED,
                             compresslevel=6, allowZip64=True) as archive:
            for path in sorted(selected):
                relative = path.relative_to(release).as_posix()
                info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.create_system = 3
                executable = relative in {removal, "delta-decoder/xdelta3",
                                          "runtime-payload/Optimum",
                                          "runtime-payload/delta-decoder/xdelta3"}
                info.external_attr = (0o100755 if executable else 0o100644) << 16
                with path.open("rb") as source, archive.open(info, "w", force_zip64=True) as target:
                    while chunk := source.read(1024 * 1024):
                        target.write(chunk)
        if staged.stat().st_size > 2 * 1024 * 1024 * 1024:
            parser.error("Download archive exceeds the installer's 2 GiB limit")
        os.replace(staged, output)
    finally:
        staged.unlink(missing_ok=True)
    print(f"{output} sha256:{digest(output)}")


if __name__ == "__main__":
    main()
