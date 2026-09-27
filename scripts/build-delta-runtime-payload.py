#!/usr/bin/env python3
"""Assemble a LOCAL delta runtime payload from published Optimum outputs.

This is a validation artifact. Native SPIR-V programs and vendor SDK binaries
still need a redistribution review before this payload can enter a player release.
"""
import argparse
from pathlib import Path
import shutil
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--launcher", required=True, type=Path, help="self-contained launcher publish directory")
parser.add_argument("--rid", required=True, choices=("win-x64", "linux-x64"))
parser.add_argument("--compiled", required=True, type=Path, help="Optimum compiled net10.0 directory")
parser.add_argument("--native-shaders", required=True, type=Path, help="compiled shaders-vk directory")
parser.add_argument("--native-runtime-dir", type=Path,
                    help="staged package root with Windows audio and optional graphics runtime DLLs")
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()
if args.output.exists():
    parser.error("Output already exists")
launcher_name = "Optimum.exe" if args.rid == "win-x64" else "Optimum"
required = (launcher_name, "Optimum.dll", "Optimum.deps.json", "Optimum.runtimeconfig.json",
            "Optimum.Bootstrap.Core.dll")
for name in required:
    if not (args.launcher / name).is_file():
        parser.error("Incomplete self-contained launcher publish: " + name)
compiled = ("Optimum.Api.Contracts.dll", "Optimum.GameContent.dll", "Optimum.Render.Vulkan.dll",
            "Silk.NET.Core.dll", "Silk.NET.Shaderc.dll", "Silk.NET.Vulkan.dll",
            "Silk.NET.Vulkan.Extensions.EXT.dll", "Silk.NET.Vulkan.Extensions.KHR.dll")
for name in compiled:
    if not (args.compiled / name).is_file():
        parser.error("Missing Optimum build output: " + name)
if not (args.native_shaders / "shaders.manifest.json").is_file():
    parser.error("Missing native shader manifest")
native_name = "shaderc_shared.dll" if args.rid == "win-x64" else "libshaderc_shared.so"
shaderc = args.compiled / "runtimes" / args.rid / "native" / native_name
if not shaderc.is_file():
    parser.error(f"Missing {args.rid} shaderc native library")
sdl_name = "SDL3.dll" if args.rid == "win-x64" else "libSDL3.so"
sdl = args.compiled / "runtimes" / args.rid / "native" / sdl_name
if not sdl.is_file():
    parser.error(f"Missing {args.rid} SDL3 native library")
if args.rid == "win-x64":
    if args.native_runtime_dir is None or not (args.native_runtime_dir / "OpenAL32.dll").is_file():
        parser.error("Windows payload needs --native-runtime-dir with root-level OpenAL32.dll")
controller_sources = Path(__file__).resolve().parent.parent / "sources" / "controller"
for name in ("gamecontrollerdb.txt", "SDL_GameControllerDB-LICENSE.txt"):
    if not (controller_sources / name).is_file():
        parser.error("Missing controller mapping source: " + name)
args.output.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="payload-stage-", dir=args.output.parent) as temporary:
    stage = Path(temporary) / "payload"
    shutil.copytree(args.launcher, stage)
    for name in compiled:
        shutil.copy2(args.compiled / name, stage / name)
    for name in ("OptimumFsr3.dll", "OptimumFsr4.dll", "OptimumNgx.dll", "OptimumXessFg.dll"):
        source = args.compiled / name
        if source.is_file():
            shutil.copy2(source, stage / name)
    if args.rid == "win-x64":
        # The game resolves OpenAL from the process directory. Keep the optional
        # graphics runtimes alongside the renderer, as in the Windows source package.
        windows_runtimes = (
            "OpenAL32.dll", "OptimumStreamline.dll", "sl.interposer.dll", "sl.common.dll",
            "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll", "Streamline-LICENSE.txt",
            "Reflex-LICENSE.txt", "nvngx_dlss.dll", "nvngx_dlssg.dll", "Dlss-LICENSE.txt",
            "libxess.dll", "libxess_fg.dll", "libxell.dll", "Xess-LICENSE.txt",
            "Xess-third-party-programs.txt", "amd_fidelityfx_vk.dll",
            "amd_fidelityfx_upscaler_dx12.dll", "Fsr3-LICENSE.txt", "Fsr4-LICENSE.md",
        )
        for name in windows_runtimes:
            source = args.compiled / name
            if not source.is_file():
                source = args.native_runtime_dir / name
            if source.is_file():
                shutil.copy2(source, stage / name)
    shutil.copy2(shaderc, stage / native_name)
    shutil.copy2(sdl, stage / sdl_name)
    shutil.copy2(controller_sources / "gamecontrollerdb.txt", stage / "gamecontrollerdb.txt")
    shutil.copy2(controller_sources / "SDL_GameControllerDB-LICENSE.txt",
                 stage / "ControllerMappings-LICENSE.txt")
    shutil.copytree(args.native_shaders, stage / "shaders-vk")
    forbidden = [p for p in stage.rglob("*") if p.is_file() and
                 (p.name.lower().startswith(("vintagestory", "vsessentials", "vssurvivalmod", "vscreativemod"))
                  or "donor" in p.name.lower())]
    if forbidden:
        parser.error("Published launcher contains game/donor files: " + ", ".join(str(p) for p in forbidden[:5]))
    (stage / "LOCAL-PROOF-ONLY.txt").write_text(
        "This payload has not passed dependency and shader redistribution review. Do not publish.\n",
        encoding="utf-8")
    stage.rename(args.output)
print(args.output)
