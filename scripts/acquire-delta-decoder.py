#!/usr/bin/env python3
"""Acquire the pinned xdelta release and stage a verified decoder bundle.

The checked-in release-inputs.json pins the archives and extracted executable.
Pass --asset-dir to use already downloaded archives without a network request.
"""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile


MAX_DECODER_ARCHIVE = 5 * 1024 * 1024


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verified_asset(spec: dict, repository: str, version: str,
                   asset_dir: Path | None, temporary: Path) -> Path:
    name = spec["name"]
    if Path(name).name != name or not name.startswith(f"xdelta3-{version}"):
        raise ValueError("Invalid pinned decoder asset name")
    if asset_dir is not None:
        asset = asset_dir / name
        if asset.is_symlink() or not asset.is_file():
            raise ValueError("Missing regular pinned decoder asset: " + name)
    else:
        asset = temporary / name
        url = f"https://github.com/{repository}/releases/download/v{version}/{name}"
        request = urllib.request.Request(url, headers={"User-Agent": "Optimum-Release-Build"})
        with urllib.request.urlopen(request, timeout=30) as response, asset.open("xb") as output:
            while chunk := response.read(1024 * 1024):
                if output.tell() + len(chunk) > MAX_DECODER_ARCHIVE:
                    raise ValueError("Decoder asset is too large")
                output.write(chunk)
    if not 1 <= asset.stat().st_size <= MAX_DECODER_ARCHIVE or sha256(asset) != spec["sha256"]:
        raise ValueError("Pinned decoder archive hash mismatch: " + name)
    return asset


def selected_tar_member(archive: Path, member: str) -> bytes:
    with tarfile.open(archive, "r:gz") as source:
        info = source.getmember(member)
        if not info.isfile() or not 1 <= info.size <= MAX_DECODER_ARCHIVE:
            raise ValueError("Invalid decoder archive member: " + member)
        stream = source.extractfile(info)
        if stream is None:
            raise ValueError("Missing decoder archive member: " + member)
        with stream:
            data = stream.read(MAX_DECODER_ARCHIVE + 1)
        if len(data) != info.size:
            raise ValueError("Decoder archive member was truncated: " + member)
        return data


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=("win-x64", "linux-x64"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--client-archive", type=Path,
                        help="also verify the pinned original client archive")
    parser.add_argument("--asset-dir", type=Path,
                        help="directory holding the two pinned upstream archives")
    parser.add_argument("--manifest", type=Path,
                        default=Path(__file__).with_name("release-inputs.json"))
    args = parser.parse_args()
    lock = json.loads(args.manifest.read_text(encoding="utf-8"))
    if args.client_archive is not None:
        client = lock["clients"][args.rid]
        if (args.client_archive.name != client["archive"] or
                args.client_archive.is_symlink() or not args.client_archive.is_file() or
                sha256(args.client_archive) != client["sha256"]):
            parser.error("Client archive does not match the pinned release input")
    decoder = lock["decoder"]
    version = decoder["version"]
    repository = decoder["repository"]
    if repository != "jmacd/xdelta" or version != "3.2.0":
        parser.error("Unsupported decoder source")
    output = args.output.resolve()
    if output.exists():
        parser.error("Output must be a new directory")
    asset_dir = args.asset_dir.resolve(strict=True) if args.asset_dir else None
    if asset_dir is not None and output.is_relative_to(asset_dir):
        parser.error("Output must be outside decoder inputs")
    output.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="decoder-acquire-", dir=output.parent) as temporary:
        stage = Path(temporary)
        binary_archive = verified_asset(decoder[args.rid], repository, version, asset_dir, stage)
        source_archive = verified_asset(decoder["sourceArchive"], repository, version, asset_dir, stage)
        license_bytes = selected_tar_member(source_archive, f"xdelta3-{version}/LICENSE")
        if args.rid == "win-x64":
            with zipfile.ZipFile(binary_archive) as source:
                member = source.getinfo(f"xdelta3-{version}-windows-x86_64/xdelta3.exe")
                if not 1 <= member.file_size <= MAX_DECODER_ARCHIVE or member.is_dir():
                    raise ValueError("Invalid decoder archive member")
                binary_bytes = source.read(member)
            binary_name = "xdelta3.exe"
        else:
            binary_bytes = selected_tar_member(binary_archive,
                                               f"xdelta3-{version}-linux-x86_64/xdelta3")
            binary_name = "xdelta3"
        if hashlib.sha256(binary_bytes).hexdigest() != decoder[args.rid]["binarySha256"]:
            raise ValueError("Extracted decoder executable does not match the pinned hash")
        binary_path = stage / binary_name
        license_path = stage / "LICENSE-xdelta.txt"
        binary_path.write_bytes(binary_bytes)
        license_path.write_bytes(license_bytes)
        if not license_path.read_text(encoding="utf-8").strip():
            raise ValueError("Decoder license is empty")
        subprocess.run([sys.executable, str(Path(__file__).with_name("bundle-delta-decoder.py")),
                        "--executable", str(binary_path), "--license", str(license_path),
                        "--rid", args.rid, "--output", str(stage / "bundle")], check=True)
        shutil.move(str(stage / "bundle"), output)
    print(output)


if __name__ == "__main__":
    main()
