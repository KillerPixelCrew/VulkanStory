#!/usr/bin/env python3
"""Developer-only VCDIFF experiment. Requires a separately supplied xdelta3 executable.

Build: binary-delta-poc.py build --xdelta PATH --original DIR --patched DIR --output NEW_DIR
Apply: binary-delta-poc.py apply --xdelta PATH --original DIR --pack DIR --output NEW_DIR

Never writes into the source install. Outputs must not exist. No downloads or publication.
This is a format/size experiment, not the production installer or a signed release format.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
from importlib.util import module_from_spec, spec_from_file_location


TARGETS = ("VintagestoryLib.dll", "VintagestoryAPI.dll",
           "Mods/VSEssentials.dll", "Mods/VSSurvivalMod.dll")
_lang_spec = spec_from_file_location("merge_optimum_lang", Path(__file__).with_name("merge-optimum-lang.py"))
_lang_module = module_from_spec(_lang_spec)
_lang_spec.loader.exec_module(_lang_module)
merge_language = _lang_module.merge


def shader_source(kind, name, original):
    """An added shader still has an exact official input, so no empty-base patch."""
    destination = f"assets/game/{kind}/{name}"
    if (original / destination).is_file():
        return destination
    if kind == "shaderincludes":
        raise ValueError(f"New shader include needs an explicit official source: {name}")
    if name.startswith("chunkliquidmotion."):
        source = "chunkliquid" + Path(name).suffix
    elif name == "scene-ssao.fsh":
        source = "ssao.fsh"
    elif name.endswith(".vsh"):
        source = "standard.vsh"
    elif name.endswith(".fsh"):
        source = "final.fsh"
    else:
        raise ValueError(f"Unexpected added shader: {name}")
    reference = f"assets/game/shaders/{source}"
    if not (original / reference).is_file():
        raise ValueError(f"Missing official shader reference: {reference}")
    return reference


def inputs(args, stage):
    for relative in TARGETS:
        yield relative, relative, args.patched / relative
    if args.asset_overlays:
        for kind in ("shaders", "shaderincludes"):
            for path in sorted((args.asset_overlays / kind).iterdir()):
                if not path.is_file():
                    continue
                if path.suffix not in (".vsh", ".fsh", ".gsh"):
                    raise ValueError(f"Unsupported asset override: {path}")
                relative = f"assets/game/{kind}/{path.name}"
                yield relative, shader_source(kind, path.name, args.original), path
        english = args.asset_overlays / "lang/en.json"
        for localized in sorted((args.asset_overlays / "lang").glob("*.json")):
            relative = f"assets/game/lang/{localized.name}"
            original = args.original / relative
            if not original.is_file():
                raise ValueError(f"Missing official language source: {relative}")
            target = stage / "merged-lang" / localized.name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(merge_language(original, english, localized))
            yield relative, relative, target


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check(path, size, sha256):
    if path.stat().st_size != size or digest(path) != sha256:
        raise ValueError(f"Size/hash mismatch: {path}")


def run_delta(executable, *args):
    # Environment options must not silently alter the recorded format/settings.
    environment = dict(os.environ)
    environment.pop("XDELTA", None)
    start = time.perf_counter()
    subprocess.run([executable, *map(str, args)], check=True, capture_output=True,
                   timeout=120, env=environment)
    return round((time.perf_counter() - start) * 1000, 2)


def build(args, stage):
    entries = []
    for relative, source_relative, target in inputs(args, stage):
        source = args.original / source_relative
        delta_name = relative + ".vcdiff"
        delta = stage / delta_name
        delta.parent.mkdir(parents=True, exist_ok=True)
        entry = {"path": relative, "delta": delta_name,
                 "inputSize": source.stat().st_size, "inputSha256": digest(source),
                 "outputSize": target.stat().st_size, "outputSha256": digest(target)}
        if source_relative != relative:
            entry["sourcePath"] = source_relative
        # Disable application headers (local filenames) and external decompression.
        entry["encodeMs"] = run_delta(args.xdelta, "-e", "-9", "-A", "-D", "-s", source, target, delta)
        entry.update(deltaSize=delta.stat().st_size, deltaSha256=digest(delta))
        with tempfile.TemporaryDirectory(prefix="delta-verify-") as temporary:
            reconstructed = Path(temporary) / "reconstructed.dll"
            entry["decodeMs"] = run_delta(args.xdelta, "-d", "-D", "-s", source, delta, reconstructed)
            check(reconstructed, entry["outputSize"], entry["outputSha256"])
        check(source, entry["inputSize"], entry["inputSha256"])
        entries.append(entry)
    if (stage / "merged-lang").exists():
        shutil.rmtree(stage / "merged-lang")
    manifest = {"format": "optimum-vcdiff-1", "gameVersion": args.game_version,
                "optimumVersion": args.optimum_version, "rid": args.rid, "files": entries}
    (stage / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def apply(args, stage):
    manifest = json.loads((args.pack / "manifest.json").read_text(encoding="utf-8"))
    if manifest.get("format") != "optimum-vcdiff-1":
        raise ValueError("Unsupported delta manifest")
    entries = manifest["files"]
    # Fixed target allowlist for the experiment; no manifest-controlled arbitrary paths.
    paths = [entry["path"] for entry in entries]
    if len(paths) < len(TARGETS) or len(paths) > 256 or len(set(paths)) != len(paths) or not set(TARGETS).issubset(paths):
        raise ValueError("Unexpected or duplicate targets")
    for entry in entries:
        if entry["delta"] != entry["path"] + ".vcdiff":
            raise ValueError("Unexpected delta path")
        shader = False
        if entry["path"] not in TARGETS:
            parts = Path(entry["path"]).parts
            shader = (len(parts) == 4 and parts[:3] in (("assets", "game", "shaders"), ("assets", "game", "shaderincludes"))
                      and Path(entry["path"]).suffix in (".vsh", ".fsh", ".gsh"))
            language = (len(parts) == 4 and parts[:3] == ("assets", "game", "lang")
                        and re.fullmatch(r"[A-Za-z0-9-]+\.json", parts[3]) is not None)
            if not shader and not language:
                raise ValueError("Unexpected asset target")
        source = entry.get("sourcePath", entry["path"])
        if entry.get("sourcePath") and not shader:
            raise ValueError("Only shaders may use an alternate source")
        if source not in TARGETS and (not source.startswith(("assets/game/shaders/", "assets/game/shaderincludes/", "assets/game/lang/")) or ".." in Path(source).parts):
            raise ValueError("Unexpected source path")
        check(args.original / source, entry["inputSize"], entry["inputSha256"])
        check(args.pack / entry["delta"], entry["deltaSize"], entry["deltaSha256"])
    for entry in entries:
        output = stage / entry["path"]
        output.parent.mkdir(parents=True, exist_ok=True)
        run_delta(args.xdelta, "-d", "-D", "-s", args.original / entry.get("sourcePath", entry["path"]),
                  args.pack / entry["delta"], output)
        check(output, entry["outputSize"], entry["outputSha256"])
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for command in ("build", "apply"):
        sub = commands.add_parser(command)
        sub.add_argument("--xdelta", required=True)
        sub.add_argument("--original", type=Path, required=True)
        sub.add_argument("--output", type=Path, required=True)
        sub.add_argument("--patched" if command == "build" else "--pack", type=Path, required=True)
        if command == "build":
            sub.add_argument("--game-version", required=True)
            sub.add_argument("--optimum-version", required=True)
            sub.add_argument("--rid", choices=("win-x64", "linux-x64"), required=True)
            sub.add_argument("--asset-overlays", type=Path,
                             help="directory with shaders/, shaderincludes/ and lang/ override files")
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        parser.error("Output already exists; choose a new directory")
    for source in (args.original, args.patched if args.command == "build" else args.pack):
        if output.is_relative_to(source.resolve()):
            parser.error("Output must be outside the input directories")
    output.parent.mkdir(parents=True, exist_ok=True)
    # Publish the local result only after all files have verified. A failure leaves
    # no apparent successful output and never alters an existing install/cache.
    with tempfile.TemporaryDirectory(prefix="delta-stage-", dir=output.parent) as temporary:
        stage = Path(temporary) / "result"
        stage.mkdir()
        manifest = build(args, stage) if args.command == "build" else apply(args, stage)
        stage.rename(output)
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
