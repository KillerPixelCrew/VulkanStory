# Concrete process session services

Updated 2026-10-01. Source implementation only; no builds, tests, probes,
packages or game runs. Tests remain deferred until integration is finished.

`GameSessionServices` now contains normalized renderer settings and the selected
game data path. Its loader reads the existing `ModConfig/vulkanstory.json` store.
It no longer requires external device, shader, frame, input, framebuffer, terrain
or provider teardown callbacks.

The process session supplies the concrete behavior:

- Device configuration selects the user's `vulkanstory-vulkan` cache, packaged
  `shaders-vk` directory and original game debug setting. The owned SR registry
  contributes device requirements before initialization and brings providers up
  afterwards; device identity is logged through the original platform logger.
- Shader callbacks use owned handheld settings and real link diagnostics.
  Texture mipmapping/error checks use original platform/client settings.
- Per-frame bloom, god rays, SSAO, shadow quality, VSync and frame cap come from
  original game settings/platform state. FXAA keeps the retained temporal/scale
  condition. The original frame handler runs directly after device frame begin.
- Any still-open owned UI scope is composed before FG preparation/presentation.
  Normal original UI/post routes remain responsible for scene/UI production.
- Existing owned controller, SR, FG and device teardown retire their resources;
  the obsolete external provider-stop callback is removed.

The framebuffer, terrain, controller and IME owner increments provide the other
session services. These changes replace placeholder dependencies with actual
owners; they do not register or activate the incomplete startup profile.

## Remaining work

Complete graphics/scene profile assembly and registration, per-frame motion
coverage, analog/hint consumers, native shader/provider runtime delivery and
ordinary mod settings/world attachment remain open. The package must supply the
configured shader corpus; source configuration is not native shader acceptance.
No menu/world rendering, current device initialization or provider execution was
verified by this increment.
