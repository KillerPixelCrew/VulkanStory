# Optimum roadmap

This roadmap tracks product-level renderer work. **Done** means the implementation is
integrated into the Vulkan renderer, exposed through the product configuration where
appropriate, and has passed the available automated and in-game validation. A vendor or
GPU that is not present on the development machine is recorded as a validation gap; it
does not move an otherwise complete integration back into planned work.

## Done

| Area | Delivered | Evidence |
| --- | --- | --- |
| **Vulkan-native renderer** | Native Vulkan rendering and shaders, frame graph and synchronization, async uploads, bindless resources, native post-processing, TAA, GTAO, separate HUD-less scene/UI resources, and Vulkan presentation | [`vulkan.md`](vulkan.md) |
| **Upscalers** | NVIDIA DLSS Super Resolution, Intel XeSS Super Resolution, AMD FSR 3.1 Super Resolution, and AMD FSR 4 through the Vulkan/DX12 interop path; presets, fallback, and live switching share one renderer contract | [`upscaler.md`](upscaler.md) |
| **Frame generation** | NVIDIA DLSS Frame Generation, Intel XeSS Frame Generation, and AMD FSR 3 Frame Generation consume the common depth, motion, HUD-less scene, and premultiplied UI resources | [`frame-generation.md`](frame-generation.md) |
| **Multi-frame generation** | The UI exposes 2x through 6x output. DLSS-G and XeSS-FG clamp the request to the active SDK/GPU maximum; FSR 3 FG correctly remains at its supported 2x mode | [`frame-generation.md`](frame-generation.md) |
| **Low latency** | NVIDIA Reflex, Intel XeLL, and AMD Anti-Lag are integrated with the engine frame identity, frame cap, sleep, and simulation/render/present markers. Provider changes select the compatible latency path. Input sampling order and the PCL input ping are still open (see below) | [`frame-generation.md`](frame-generation.md) |
| **Optimization pass** | GPU timestamps identified the major costs; persistent chunk meshes moved to mapped VRAM where supported, GTAO prefiltering was parallelized, foliage alpha testing was moved ahead of expensive lighting, redundant pipeline binds were removed, and Vulkan-specific stray OpenGL work was fixed | [`performance-profile-2026-09-26.md`](performance-profile-2026-09-26.md) |
| **XeSS-FG pacing** | The next frame's Vulkan work waits on the GPU for XeSS-FG's DX12 work instead of time-slicing with it, and the proxy `Present` runs on a present thread; the bimodal 2/10 ms presents are gone | [`performance-profile-2026-09-26.md`](performance-profile-2026-09-26.md) |

The optimization pass reduced the measured native 1080p GPU frame from **10.30 ms
to 4.49 ms** on the RTX 4070 Laptop test system. Focused-window 1080p Quality runs
also demonstrated 2x output from all three frame-generation providers: 334 FPS for
FSR 3 FG, 258 FPS for DLSS-G, and 286 FPS for XeSS-FG (up from 229 before its pacing
fix) in the captured scene. The test system is an Optimus laptop whose Intel iGPU
scans out, so present and pacing numbers include that cross-adapter path. These numbers
characterize that test system and scene; they are not performance guarantees.

## Validation and polish

These are follow-up validation or tuning tasks, not missing feature integrations:

- Fix input sampling order for the latency SDKs: the Reflex/XeLL/Anti-Lag sleep currently
  runs after OpenTK has pumped window input, so input waits through the sleep. Sleep
  before the input pump, mark a real `InputSample`, and answer Streamline PCL's
  `statsWindowMessage` ping with `ePCLatencyPing`.
- Confirm XeSS-FG pacing on a dGPU-wired display and on Intel GPUs, where the driver
  paces presentation instead of the SDK's cross-vendor pacer.
- Run FSR 4 on supported AMD hardware; its unavailable-provider fallback and code path
  are covered, but the current NVIDIA development system cannot execute the provider.
- Resolve or conclusively classify the two real-window resize synchronization-validation
  reports documented by the performance pass.
- Extend visible-window and cross-vendor coverage for resize, minimize/restore, history
  reset, motion-vector scale/sign, UI recomposition, and multi-frame limits.
- Keep measuring before taking further barrier, descriptor, or interop shortcuts; the
  profile lists candidates, not pre-approved optimizations.

## Next

- **Controller support (SDL3):** first-class gamepad play in the spirit of Minecraft
  Java's controller mods (Controlify, Controllable), built into the client rather than
  added as a mod:
  - **Input backend:** SDL3's gamepad, sensor, and haptic subsystems next to the existing
    GLFW window, through a C# binding such as `ppy.SDL3-CS`. Hot-plug, the community
    mapping database, and Xbox, PlayStation (DualShock 4, DualSense), Switch Pro, and
    Steam Deck controllers, without fighting Steam Input.
  - **Sampling:** controllers are polled at the frame's input-sampling point, after the
    latency sleep, so Reflex/XeLL/Anti-Lag and frame generation cover controller input
    exactly like keyboard and mouse.
  - **Gameplay:** actions map onto the game's hotkey system so existing and mod hotkeys
    remain bindable. Analog movement and camera look with deadzones and response curves,
    optional gyro aiming, hotbar cycling, sneak/sprint toggles, and radial menus for
    hotbar, tool modes, and other quick actions.
  - **Menus:** full GUI navigation without a mouse. A virtual cursor that snaps to slots
    and widgets, D-pad focus movement, inventory and crafting slot actions (pick up, split,
    move stack), and an on-screen keyboard for chat, signs, and text fields.
  - **Feedback and settings:** controller-specific button glyphs in hints and keybinding
    screens, rumble and DualSense trigger/haptic effects, per-controller profiles, and a
    remapping and sensitivity UI in the Optimum settings. Switching seamlessly between
    controller and keyboard/mouse mid-session.
  - **Validation:** a device matrix (Xbox, DualSense, Switch Pro, Steam Deck, generic
    DirectInput) and proof that keyboard/mouse play and latency markers are unchanged
    with SDL3 active. SDL3 is zlib-licensed.
- **Mod-facing renderer API:** stabilize native pass, resource, motion-writer, and
  capability contracts so mods can participate without OpenGL assumptions.
- **HDR output:** add an HDR scene range and tone mapper, display-referred UI, HDR10 or
  scRGB presentation, and an FG-compatible format path.
- **Auto-PBR materials:** generate normal, roughness/metalness, and emissive companion
  atlas data from Vintage Story material classes, with authored resource-pack data taking
  precedence.
- **Ray tracing and denoising:** add acceleration-structure maintenance for the editable
  chunk world, then stage ray-query AO, shadows, and reflections with cross-vendor
  denoising and DLSS Ray Reconstruction where available.
- **Final documentation cleanup:** remove stale implementation notes, retain the reasons
  behind non-obvious synchronization and SDK decisions, and make the renderer entry
  points approachable once the remaining systems stop moving.

## Product rules carried forward

- Frame generation always consumes a genuinely HUD-less scene and a separate UI resource;
  generated UI is not acceptable.
- Vendor features remain optional at runtime. Missing SDK binaries or unsupported hardware
  must fall back cleanly without preventing Vulkan from starting.
- Requested settings and effective SDK state are distinct. The renderer reports clamping,
  fallback, and generated-present counts rather than pretending a requested multiplier ran.
- Correctness and pacing are judged on visible output and GPU/SDK evidence, not merely by
  successful feature creation or a passing headless capture.
