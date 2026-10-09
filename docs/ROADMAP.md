# VulkanStory Roadmap

Updated 2026-10-09. Current status and remaining work.

Current release: [`v0.1.0-dev`](https://github.com/KillerPixelCrew/VulkanStory/releases/tag/v0.1.0-dev)
(win-x64), built from branch `rewrite`. Vendor SDKs are pinned submodules under
`sdk/`; the Streamline release runtimes are fetched by `scripts/fetch-streamline-release.ps1`.

## Features

| Area | Status |
| --- | --- |
| Vulkan renderer, SDL3 window/input, official startup and graphics routing | Done |
| Native TAA with sharpening and mip bias; render scale 25–100 % | Done |
| Ambient occlusion: vanilla SSAO and GTAO with presets | Done |
| Upscaling: DLSS, FSR 3.1, FSR 4, XeSS, with automatic fallback | Done |
| Frame generation: DLSS-G (up to 6×), FSR 3 FG, XeSS-FG | Done |
| Low latency: Reflex / PC Latency, Anti-Lag, XeLL | Done |
| Original Options menu integration (main menu and in world) | Done |
| Controllers: remapping, radial menu, glyphs, analog movement and server companion; touch | Done |
| Screenshots, timelapse and AVI capture | Done |
| Render-pass extension points for other mods | Done |
| Windows install/update/removal through the native `hostfxr.dll` proxy | Done |
| First world load holds the loading image until the frame is complete (REN-06) | Done |
| OpenGL compatibility for other mods: loader-bound discovery and refusal | Done |

## Known defects

| ID | Defect |
| --- | --- |
| REN-07 | Geometry edges against the sky jitter with TAA and FSR upscaling (foliage against terrain is stable). Reading the TAA/FSR3 reactive value from the nearest-depth tap did not fix it. Next candidates: sky, cloud and fog layers not sharing the scene jitter, and depth/dilation at sky pixels. |
| REN-08 | The first use of a new pipeline mid-game (new entity, particle or mod shader variant) skips that draw while it compiles, which costs one unresolved jittered frame and a TAA/SR history reset. |

## Deferred work

| ID | Work | Deferred because |
| --- | --- | --- |
| GL-02 | Shared-resource/state adapters for other mods' OpenGL usage | Waits for a target mod to be selected |
| GL-03 | Concrete mod compatibility profiles | No target mods yet (user, 2026-10-08) |
| TEST-01 | Write tests, including a decision on the unused `*ForTests` hooks | Tests are deferred until integration is finished |
| LINUX-01 | Linux release package | Linux is not part of the current release |

## Working rules

- Official Vintage Story 1.22.7, Windows x64; use the complete **foggy village story**.
- Routine checks use the isolated harness. DLSS-FG checks run visible and focused;
  hidden windows cannot establish its output gain. Other routine checks stay hidden.
  The harness must be silent: all six audio levels are zeroed and verified before
  launch; the copied cache is excluded.
- Do not change installed settings/save or deploy/open/focus/close the user's game
  for routine checks. Visible launches/deployment require the user's instruction.
- Keep implementation and validation turns separate. A validation turn has one
  bounded batch, once. New tests stay deferred until integration is finished.
- Preserve working donor algorithms, attribution and architecture boundaries.
  No launcher/transplant pipeline or modified official game assemblies.
