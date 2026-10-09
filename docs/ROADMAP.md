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

## Deferred

- **GL-02 / GL-03:** OpenGL compatibility adapters and profiles for other mods, once a target mod is chosen.
- **TEST-01:** Tests.
- **LINUX-01:** Linux release.
