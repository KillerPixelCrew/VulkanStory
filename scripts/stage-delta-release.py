#!/usr/bin/env python3
"""Assemble a local GUI delta release from published installer, pack and runtime payload.

Inputs are locally trusted build artifacts. This neither signs nor publishes a release.
Game originals and donor assemblies do not belong in the payload.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tarfile
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
for name in ("installer", "pack", "payload", "output"):
    parser.add_argument("--" + name, type=Path, required=True)
parser.add_argument("--uninstaller", type=Path,
                    help="self-contained single-file optimum CLI for durable removal")
parser.add_argument("--local-proof", action="store_true",
                    help="permit a LOCAL-PROOF-ONLY payload for installation tests; never publish it")
parser.add_argument("--linux-archive", type=Path,
                    help="write a Linux tar.gz with executable modes preserved, even when staged on Windows")
args = parser.parse_args()
if (args.payload / "LOCAL-PROOF-ONLY.txt").exists() and not args.local_proof:
    parser.error("Local proof payloads cannot be staged for release")
output = args.output.resolve()
if output.exists():
    parser.error("Output must be a new directory")
for root in (args.installer, args.pack, args.payload):
    if output.is_relative_to(root.resolve()):
        parser.error("Output must be outside the input directories")
    if root.is_symlink() or any(p.is_symlink() for p in root.rglob("*")):
        parser.error("Input directories must not contain symbolic links")
if args.uninstaller is not None:
    if not args.uninstaller.is_file() or args.uninstaller.is_symlink():
        parser.error("--uninstaller must be a regular published executable")
    if output.is_relative_to(args.uninstaller.resolve().parent):
        parser.error("Output must be outside the uninstaller directory")
manifest = json.loads((args.pack / "manifest.json").read_text(encoding="utf-8"))
if manifest["format"] != "optimum-vcdiff-1" or manifest["rid"] not in ("win-x64", "linux-x64"):
    parser.error("Unsupported delta pack")
rid = manifest["rid"]
if args.uninstaller is None:
    parser.error("Delta releases require --uninstaller")
uninstaller_name = "delta-uninstaller.exe" if rid == "win-x64" else "delta-uninstaller"
if args.linux_archive:
    if rid != "linux-x64":
        parser.error("--linux-archive requires a linux-x64 pack")
    if args.linux_archive.exists():
        parser.error("Linux archive must not already exist")
    archive_output = args.linux_archive.resolve()
    if archive_output.is_relative_to(output) or any(archive_output.is_relative_to(root.resolve())
                                                 for root in (args.installer, args.pack, args.payload)):
        parser.error("Linux archive must be outside release and input directories")
launcher = "Optimum.exe" if rid == "win-x64" else "Optimum"
sdl_native = "SDL3.dll" if rid == "win-x64" else "libSDL3.so"
for name in (launcher, "Optimum.dll", "Optimum.deps.json", "Optimum.runtimeconfig.json",
             "Optimum.Bootstrap.Core.dll", "Optimum.Render.Vulkan.dll", "Optimum.Api.Contracts.dll",
             "Optimum.GameContent.dll", sdl_native, "gamecontrollerdb.txt",
             "ControllerMappings-LICENSE.txt", "shaders-vk/shaders.manifest.json"):
    if not (args.payload / name).is_file():
        parser.error("Incomplete runtime payload: " + name)
if not (args.installer / "delta-decoder/decoder.json").is_file():
    parser.error("Publish the installer with DeltaDecoderDirectory first")
inventory = []
targets = {entry["path"].lower() for entry in manifest["files"]}
for path in sorted(args.payload.rglob("*")):
    if not path.is_file():
        continue
    relative = path.relative_to(args.payload).as_posix()
    if (relative.lower() in targets or "donor" in path.name.lower() or relative.lower().startswith(".optimum/")
            or path.name.lower().startswith(("vintagestory", "vsessentials", "vssurvivalmod", "vscreativemod"))):
        parser.error("Game targets, donors and launcher state must not be in the payload: " + relative)
    with path.open("rb") as stream:
        sha256 = hashlib.file_digest(stream, "sha256").hexdigest()
    inventory.append({"path": relative, "size": path.stat().st_size, "sha256": sha256})
output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(dir=output.parent, prefix="release-stage-") as temporary:
    stage = Path(temporary) / "release"
    shutil.copytree(args.installer, stage)
    shutil.copytree(args.pack, stage / "delta-pack")
    shutil.copytree(args.payload, stage / "runtime-payload")
    descriptor = {
        "gameVersion": manifest["gameVersion"], "optimumVersion": manifest["optimumVersion"],
        "rid": rid, "payloadFiles": inventory}
    if args.uninstaller is not None:
        shutil.copy2(args.uninstaller, stage / uninstaller_name)
        with args.uninstaller.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        descriptor["uninstaller"] = {"path": uninstaller_name,
                                     "size": args.uninstaller.stat().st_size, "sha256": digest}
    (stage / "delta-release.json").write_text(json.dumps(descriptor, indent=2) + "\n", encoding="utf-8")
    if args.linux_archive:
        archive = args.linux_archive.resolve()
        archive.parent.mkdir(parents=True, exist_ok=True)
        executable_paths = {"Optimum.Installer", "delta-decoder/xdelta3",
                            "runtime-payload/Optimum", "runtime-payload/delta-decoder/xdelta3"}
        if args.uninstaller is not None:
            executable_paths.add(uninstaller_name)
        with tempfile.NamedTemporaryFile(prefix="release-archive-", suffix=".tar.gz",
                                         dir=archive.parent, delete=False) as temporary_archive:
            archive_stage = Path(temporary_archive.name)
        try:
            with tarfile.open(archive_stage, "w:gz") as tar:
                for path in sorted(stage.rglob("*")):
                    relative = path.relative_to(stage).as_posix()
                    info = tar.gettarinfo(path, arcname="optimum-release/" + relative)
                    info.mode = 0o755 if path.is_dir() or relative in executable_paths else 0o644
                    if path.is_file():
                        with path.open("rb") as source:
                            tar.addfile(info, source)
                    else:
                        tar.addfile(info)
            stage.rename(output)
            os.replace(archive_stage, archive)
        finally:
            archive_stage.unlink(missing_ok=True)
    else:
        stage.rename(output)
print(output)
