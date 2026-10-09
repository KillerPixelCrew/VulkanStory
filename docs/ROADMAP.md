# Roadmap

Current release: [v0.1.0-dev](https://github.com/KillerPixelCrew/VulkanStory/releases/tag/v0.1.0-dev) (Windows x64, Vintage Story 1.22.7).

## Done

- Vulkan renderer, SDL3 window and input
- TAA, render scale, SSAO / GTAO
- Upscaling: DLSS, FSR 3.1, FSR 4, XeSS
- Frame generation: DLSS-G (up to 6×), FSR 3 FG, XeSS-FG
- Low latency: Reflex / PC Latency, Anti-Lag, XeLL
- Options menu integration
- Controllers (remapping, radial menu, glyphs, analog movement + server companion), touch
- Screenshot, timelapse and AVI capture
- Render-pass extension points for other mods
- Windows install, update and removal
- Loading screen stays up until the first world frame is complete

## Known defects

- **REN-07:** Geometry edges against the sky jitter with TAA and FSR.
- **REN-08:** A pipeline first used mid-game (new entity, particle or mod shader) skips its draw while compiling, causing one jittered frame and a TAA/SR history reset.

## Planned

- **PACK-01: Shader packs** (an Iris/Oculus equivalent for Vintage Story).
  1. Pack contract: replaceable programs, provided uniforms/samplers, readable and writable attachments, pass order, options. Decide which internal layouts become public API.
  2. Loader: `shaderpacks/` folder, pack selection and per-pack options in the Options menu, hot reload; compiled at runtime with the shipped shaderc.
  3. Per-program fallback to the built-in shaders on compile/link errors, with in-game error reporting.
  4. Pipeline cache keyed by the active pack.
  5. Pack-declared composite/deferred passes in the frame graph.

- **VR-01: OpenXR VR** (a Vivecraft equivalent for Vintage Story).
  1. OpenXR session and swapchains on the existing Vulkan device; headset presentation alongside or instead of the SDL window.
  2. Stereo rendering (two views or multiview) with per-eye projection/view from head tracking.
  3. Per-eye temporal state: TAA history, motion vectors and jitter; per-eye or disabled upscaler and frame-generation contexts where the SDKs don't support VR.
  4. Motion-controller input through OpenXR actions: movement, look, interaction, hotbar, inventory, with bindings for common controllers.
  5. GUI on world-space panels, plus comfort options (snap/smooth turn, vignette, seated/standing).
  6. Full-body pose from head and hand tracking via inverse kinematics on the player model.
  7. Server companion mod that streams head/hand poses so other players see VR players' tracked movement (like Vivecraft's server plugin), with fallback to normal animation for players without it.

## Deferred

- **GL-02 / GL-03:** OpenGL compatibility adapters and profiles for other mods, once a target mod is chosen.
- **TEST-01:** Tests.
- **LINUX-01:** Linux release.
