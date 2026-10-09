# Real-world loading and Streamline extent result — 2026-10-01

One bounded validation batch, run once. No tests, source repairs, archive regeneration
or second launch. Complete artifacts: `artifacts/validation/runtime-world-20261001-152358/`.

## Result

Streamline bridge built successfully against release SDK 2.14.1/Vulkan SDK
1.4.357.0. Managed Game/backend Release build passed with zero warnings/errors.
The other four unchanged bridges were copied from their recorded build. A fresh
native bundle, fresh stage and owned installation update all passed. Matching
new native export/backend were deployed together.

Before launch, the existing foggy village story.vcdbs and its WAL/SHM files were
backed up to this batch's save-backup directory. Original Vintagestory.exe launched
with --openWorld "foggy village story" (basename, as the game's parser requires).
No development superflat or new world was used.

PID 25740 activated the SDL/Vulkan runtime, remained running through the first
30-second interval, then crashed at 15:24:36 before the planned 90-second bound.
No close request was needed. Crash handler returned process exit 0; this is a
failed world-loading check. No runtime.stopped drain marker was captured.

The preserved first exception is at screen frame 1062:
OpenTK.Graphics.OpenGL.GL.GenTexture -> SvgLoader.LoadSvg -> GuiAPI.LoadSvgWithPadding
-> BlockShapeFromAttributes.OnLoaded -> BlockClutter.OnLoaded -> client startup
OnAllAssetsLoaded_Blocks -> connecting-screen render. The rasterizer itself is
CPU/native SVG work; its LoadSvg upload directly allocates/binds/uploads OpenGL
textures. That allocation/upload path has no SVG-specific route in the current
Game integration. Add routing around this exact texture-upload boundary in a
later implementation turn while preserving rasterization, sizing and color data.

## Provider result and limits

The VK_EXT_debug_utils warning is absent in this run, consistent with the new
advertised-extension request. The optional backbuffer extent warning still occurs
at first presentation. Therefore the new tagging source is not accepted as a
complete fix: investigate initial non-game presents before a scene-tag/suspend
call occurs. The three unsupported hooks remain as previously diagnosed from
SDK source. No warning filtering or feature downgrades occurred.

World asset loading progressed and builtin/VulkanStory mods loaded, but playable
world rendering, real-scene SR/FG, presented pixels, audio/input and complete
shutdown remain unverified. The installed payload now contains the extent
increment; archives remain stale. Next implementation priority is SVG upload
routing and initial-presentation extent ownership. No rerun in this turn.
