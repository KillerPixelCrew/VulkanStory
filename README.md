# VulkanStory

A Vulkan renderer, SDL3 window and input layer, and modern upscaling / frame
generation for **Vintage Story 1.22.7**, delivered as a mod for the official,
unmodified game.

> **Status: development builds.** Windows x64 builds install and run on recorded
> NVIDIA and Intel hardware. Release acceptance is still open: AMD execution, the
> Linux path, and parts of moving-scene quality and pacing have not been verified yet.
> The [roadmap](docs/ROADMAP.md) is the authoritative feature and defect status.

## Features

- **Native Vulkan 1.3 renderer** replacing the game's OpenGL path: frame graph,
  bindless textures, asynchronous pipeline compilation with a persistent pipeline
  cache, and a native shader corpus compiled offline to SPIR-V.
- **Temporal rendering**: native TAA with per-object, sky and liquid motion vectors,
  plus GTAO ambient occlusion alongside the vanilla SSAO path.
- **Upscaling**: NVIDIA DLSS, AMD FSR 3.1 and FSR 4, and Intel XeSS, selected in the
  game's own Options menu, with automatic fallback when a provider is unavailable.
- **Frame generation**: DLSS-G (via NVIDIA Streamline), FSR 3 frame generation and
  XeSS-FG, with latency control (Reflex / PC Latency, Anti-Lag, XeLL).
- **SDL3 window and input**: high-DPI aware windowing, raw mouse, touch, and full
  gamepad support with remapping, a radial menu, on-screen glyphs and analog movement
  (with an optional server-side input companion).
- **Extension points** for other mods to add renderer passes. A separate
  compatibility module for translating supported OpenGL usage by other mods is planned.

## Requirements

- Vintage Story **1.22.7** (official installation). Other versions are refused at startup.
- **Windows 10/11 x64** with a Vulkan 1.3 capable GPU and driver.
  Linux x64 packages exist but have not yet been built or run on Linux.
- Vendor features need matching hardware: DLSS / DLSS-G need NVIDIA RTX, FSR 4 needs
  AMD RDNA 4, and XeSS-FG needs eligible Intel Arc hardware. FSR 3 and XeSS upscaling
  run more widely.

## Installation (players)

VulkanStory starts **before** the game creates its window, so it is installed beside
the game executable, not only into `Mods`.

1. Close the game.
2. Extract the client ZIP into the folder that contains `Vintagestory.exe`, keeping
   the folder layout. This adds `hostfxr.dll`, `VulkanStory/`, `Mods/vulkanstory` and
   `Mods/vulkanstoryinput`. The game's own files are not modified.
3. Start the game with your normal shortcut.

Updating, disabling, removal and coexistence with other DLL proxies are described in
the [client README](packaging/README-client.txt) (Windows) and the
[Linux client README](packaging/README-linux-client.txt). In short:

- **Update**: use `VulkanStory/tools/deploy-runtime.ps1`. It verifies ownership and
  backs up everything it replaces.
- **Disable**: turn the mod off in the mod manager, or set `Enabled=0` under
  `[Bootstrap]` in `VulkanStory/loader.ini` to skip early startup entirely.
- **Remove**: use `VulkanStory/tools/remove-runtime.ps1`. Saves and settings stay.

In game, renderer options live in the regular **Options** menu. Chat commands:
`.vulkanstory settings`, `.vulkanstory status`, `.vulkanstory controller`.

When reporting a problem, include `%LOCALAPPDATA%\VulkanStory\Logs\bootstrap-*.jsonl`
and the game's `Logs/client-main.log`.

## Building from source

Prerequisites:

- .NET SDK 10.0.100 or newer (see `global.json`)
- An official Vintage Story 1.22.7 installation. Copy
  `Directory.Build.local.props.example` to `Directory.Build.local.props` and point
  `VintageStoryPath` at it. Game assemblies are only compiled against, never shipped.
- CMake and Ninja (native bootstrap), the Vulkan SDK, and MinGW-w64 `g++`/`gcc`
  (provider bridges)
- Vendor SDKs are pinned git submodules under `sdk/` (DLSS, FidelityFX for FSR 3
  and FSR 4, XeSS, Streamline 2.14.1). Clone with `--recurse-submodules`, or run
  `git submodule update --init`.

```powershell
# Vendor SDKs (submodules) and the Streamline 2.14.1 release runtimes (hash-pinned download)
git submodule update --init
pwsh scripts/fetch-streamline-release.ps1

# Managed projects, native bootstrap and the full shader corpus
pwsh scripts/build-runtime.ps1 -Configuration Release

# The five native provider bridges (NGX, FSR3, FSR4, XeSS-FG, Streamline); SDK roots default to sdk/
pwsh scripts/build-provider-bridges.ps1 -OutputDirectory <fresh-dir>

# Collect redistributables, stage a package and build the ZIPs
pwsh scripts/prepare-native-bundle.ps1 -BridgesDirectory <bridges> -CoreNativeDirectory ... -OutputDirectory <fresh-dir>
pwsh scripts/stage-runtime.ps1 -NativeDirectory ... -ShadersDirectory artifacts/runtime-shaders/Release/shaders-vk -OutputDirectory <fresh-dir> ...
pwsh scripts/package-runtime.ps1 -StagingDirectory <stage> -OutputDirectory <fresh-dir>
```

Every script documents its parameters (`Get-Help scripts/<name>.ps1 -Full`). None of
them launch the game. Renderer checks normally run in the isolated
[headless harness](docs/headless-harness.md) against a world snapshot rather than in
your own installation.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/VulkanStory.Bootstrap` | Early managed startup, activated by the native `hostfxr.dll` proxy |
| `src/VulkanStory.Render.Vulkan` | Vulkan device, frame graph, pipelines, upscalers, frame generation, presentation, latency |
| `src/VulkanStory.Platform.Sdl` | SDL3 window, events and coordinates (game-independent) |
| `src/VulkanStory.Game` | Game integration: Harmony patches, graphics/platform adapters, temporal state, controller input |
| `src/VulkanStory.Mod` | Mod entry point, settings UI and chat commands |
| `src/VulkanStory.Contracts`, `src/Shared` | Shared contracts and settings choices |
| `src/VulkanStory.Input`, `src/VulkanStory.Input.Companion` | Analog movement protocol and the optional server companion |
| `native/` | Native bootstrap and provider bridges (NGX, FSR3, FSR4, XeSS-FG, Streamline) |
| `shaders/native` | Native GLSL shader corpus |
| `tools/` | Shader compiler, game-profile and preflight tools |
| `scripts/` | Build, stage, package, deploy and removal scripts |
| `sdk/` | Vendor SDKs as pinned submodules; `streamline-release-2.14.1/` is fetched, not tracked |
| `packaging/`, `profiles/` | Package inventories, notices and supported game profiles |
| `tests/` | Unit and integration tests |
| `docs/` | Design documents and roadmap; dated implementation/validation records in `docs/records/` |

## Documentation

- [Roadmap](docs/ROADMAP.md): current status, known defects and remaining work
- [Architecture](docs/architecture.md): components, ownership, rendering contracts, mod compatibility
- [Bootstrap and installation](docs/bootstrap-and-installation.md): native activation, install/update/removal, Linux
- [Porting plan](docs/porting-plan.md): how the original implementation was migrated
- [Development evidence](docs/development-evidence.md): what has been built, run and verified, and on which hardware

## Background

VulkanStory began as part of [Optimum](https://github.com/StratumServer/Optimum), a
modified-client fork. This branch is a fresh implementation as an ordinary mod: it
compiles against the official game assemblies, keeps its own state outside game
objects, and integrates through Harmony instead of a modified client. Working
renderer, shader, SDL and provider code was migrated as directly as possible, with
its provenance preserved.

## License and attribution

Code migrated from Optimum keeps its original license and attribution; a rename or
move does not change a file's license. The Optimum license texts, notice and per-path
license scope are in [`packaging/notices/source-provenance`](packaging/notices/source-provenance).
Third-party notices for SDL, Shaderc, Silk.NET, PromptFont and the vendor SDK runtimes
are under [`packaging/notices`](packaging/notices) and ship with every package under
`VulkanStory/licenses`. Vendor SDKs are subject to their own license terms.

Vintage Story is © Anego Studios. This project is not affiliated with or endorsed by
Anego Studios, NVIDIA, AMD or Intel.
