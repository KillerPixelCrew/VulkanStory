# VulkanStory

Fresh Vintage Story Vulkan renderer and SDL window/input mod, targeting the official 1.22.7 Windows x64 client.

The [Roadmap](docs/ROADMAP.md) is authoritative for current feature status, source/package identity and remaining work. [Development evidence](docs/development-evidence.md) separates applied source from build, runtime and hardware checks. The full port remains open.

**Historical checkpoint — 2026-10-01:** the Vulkan backend, SDL host, official-game integration, providers and mod API were wired and compiled; isolated native/DLSS/FSR3/XeSS captures and a development ZIP were recorded. These scoped results do not certify later source or full feature parity.

Routine runtime work uses the [isolated hidden harness](docs/headless-harness.md) with a snapshot of **foggy village story**. It does not deploy to or control the user's game. New test writing remains deferred; implementation and validation turns stay separate.

The intended Windows install is to extract a package into the existing game directory and launch the usual Vintage Story shortcut. A native DLL proxy activates VulkanStory before graphics startup. The game keeps its original executable, assemblies, assets, and data directory. VulkanStory owns its renderer, window, settings, and runtime patches.

This is a mod with an early native bootstrap. Its initial installation goes beside the executable; a ZIP placed only in `Mods` cannot supply the required early startup. Windows activation uses a `hostfxr.dll` proxy and a .NET startup hook. Player instructions ship in [the client README](packaging/README-client.txt). Existing `version.dll` is a separate coexistence acceptance item.

## Design documents

- [Architecture](docs/architecture.md): components, dependencies, runtime ownership, rendering contracts, and compatibility with other mods.
- [Bootstrap and installation](docs/bootstrap-and-installation.md): native activation, normal-shortcut installation, existing DLL proxies, failure handling, and Linux activation.
- [Porting plan](docs/porting-plan.md): source migration map, development workflow, and bounded acceptance milestones.
- [Imported source inventory](porting/README.md): renderer, SDL, shader, native bridge, contract, and test source moved into this repository.

## Project direction

- Develop entirely in this repository against official, unmodified game assemblies.
- Use `D:\Coding\VulkanStory` as reference material for reusable graphics and SDL implementations. The runtime loads no donor code/assemblies from that checkout; vendor SDK build inputs are supplied separately.
- Replace the old inheritance and injected-member integration with a Harmony adapter that owns its additional state outside game objects.
- Transplant working renderer, shader, SDL, and provider code as directly as possible. The fresh project changes ownership and integration; it is not a rewrite of the rendering algorithms. Defer optional internal refactors until feature parity.
- Preserve the feature target: native Vulkan rendering, SDL input/windowing, temporal rendering, ambient occlusion, upscaling, frame generation, latency control, capture, and renderer extension points.
- Add a distinct compatibility module for translating supported OpenGL usage by other mods into Vulkan.
- Exclude the Optimum launcher, donor assemblies, Cecil transplantation, binary delta runtime, installer, and unrelated server/gameplay optimizations.

## Development entry points

Set `VintageStoryPath` in `Directory.Build.local.props` to the official installation. Production build scripts are `scripts/build-runtime.ps1` and `scripts/build-provider-bridges.ps1`; staging/archive scripts are `scripts/stage-runtime.ps1` and `scripts/package-runtime.ps1`. SDK inputs are development dependencies; players receive the packaged redistributables.

Use the harness against a fresh stage for routine renderer checks. The ownership-aware updater is `scripts/deploy-runtime.ps1`; installed deployment is separate from harness work. No automatic replacement of the user's running game is part of development checks.

The [October-1 candidate](docs/validation-client-candidate-2026-10-01-01.md) and [native-resolution world checkpoint](docs/validation-graph-native-world-2026-10-01-01.md) are historical payloads. Use the Roadmap for the current stage and its acceptance limits.
