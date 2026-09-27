#!/usr/bin/env python3
"""Build a versioned delta pack from exact client inputs and fresh Cecil outputs.

Run after the platform build and runtime-donor preparation. The output is a new
pack directory; temporary patched assemblies and reconstruction stay local.
"""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


TARGETS = ("VintagestoryLib.dll", "VintagestoryAPI.dll",
           "Mods/VSEssentials.dll", "Mods/VSSurvivalMod.dll")
DONORS = ("VintagestoryLib.Donor.dll", "VintagestoryAPI.Contracts.dll",
          "VSEssentials.Donor.dll", "VSSurvivalMod.Donor.dll")


def require_file(path):
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"Required regular file missing: {path}")


def run(*command):
    result = subprocess.run([str(part) for part in command], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {command[0]}\n"
                           + result.stdout[-4000:] + result.stderr[-4000:])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=("win-x64", "linux-x64"), required=True)
    parser.add_argument("--original", type=Path, required=True)
    parser.add_argument("--donors", type=Path, required=True,
                        help="the staged package's .optimum/donors directory")
    parser.add_argument("--api-runtime-donor", type=Path,
                        help="version-matched API runtime donor used for patching; the stock API remains the delta base")
    parser.add_argument("--patcher", type=Path, required=True,
                        help="freshly built Optimum.Patcher.dll")
    parser.add_argument("--decoder", type=Path, required=True,
                        help="verified xdelta3 executable")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    lock = json.loads((root / "scripts/release-inputs.json").read_text(encoding="utf-8"))
    game_version = lock["gameVersion"]
    if json.loads((root / "forks.json").read_text(encoding="utf-8"))["vintageStoryVersion"] != game_version:
        parser.error("Pinned client version differs from forks.json")
    optimum_version = (root / "VERSION").read_text(encoding="utf-8").strip()
    if not optimum_version:
        parser.error("VERSION is empty")
    for relative in TARGETS:
        require_file(args.original / relative)
    if args.rid == "win-x64":
        if args.api_runtime_donor is None:
            parser.error("win-x64 requires --api-runtime-donor for the stock 1.22.7 API")
        require_file(args.api_runtime_donor)
        marker = args.api_runtime_donor.parent / "runtime-donor-version.txt"
        if not marker.is_file() or marker.read_text(encoding="utf-8").strip() != f"version-{game_version}.txt":
            parser.error("API runtime donor version marker does not match the pinned game")
    elif args.api_runtime_donor is not None:
        require_file(args.api_runtime_donor)
    for donor in DONORS:
        require_file(args.donors / donor)
    for path in (args.patcher, args.decoder):
        require_file(path)
    for kind in ("shaders", "shaderincludes", "lang"):
        if not (root / "sources" / kind).is_dir():
            parser.error("Missing asset overlay directory: " + kind)
    output = args.output.resolve()
    if output.exists():
        parser.error("Output must be a new directory")
    inputs = (args.original, args.donors, args.patcher.parent, args.decoder.parent)
    if args.api_runtime_donor is not None:
        inputs += (args.api_runtime_donor.parent,)
    for source in inputs:
        if output.is_relative_to(source.resolve()):
            parser.error("Output must be outside all inputs")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="delta-build-", dir=output.parent) as temporary:
        temporary = Path(temporary)
        patched = temporary / "patched"
        (patched / "Mods").mkdir(parents=True)
        originals = [args.original / name for name in TARGETS]
        donors = [args.donors / name for name in DONORS]
        targets = [patched / name for name in TARGETS]
        run("dotnet", "exec", args.patcher, originals[0], donors[0], targets[0])
        run("dotnet", "exec", args.patcher, "--api",
            args.api_runtime_donor or originals[1], donors[1], targets[1])
        for name, index in (("VSEssentials", 2), ("VSSurvivalMod", 3)):
            run("dotnet", "exec", args.patcher, "--mod", name,
                originals[index], donors[index], targets[index])
        pack = temporary / "pack"
        delta_script = root / "scripts/binary-delta-poc.py"
        run(sys.executable, delta_script, "build", "--xdelta", args.decoder,
            "--original", args.original, "--patched", patched,
            "--asset-overlays", root / "sources", "--game-version", game_version,
            "--optimum-version", optimum_version, "--rid", args.rid, "--output", pack)
        manifest = json.loads((pack / "manifest.json").read_text(encoding="utf-8"))
        expected = 46 + len(list((root / "sources/lang").glob("*.json")))
        if len(manifest["files"]) != expected or manifest["rid"] != args.rid:
            raise ValueError(f"Expected four DLLs, 42 shaders and {expected - 46} language deltas")
        run(sys.executable, delta_script, "apply", "--xdelta", args.decoder,
            "--original", args.original, "--pack", pack,
            "--output", temporary / "reconstructed")
        shutil.move(str(pack), output)
    print(f"Verified {len(manifest['files'])} reconstructed files: {output}")


if __name__ == "__main__":
    main()
