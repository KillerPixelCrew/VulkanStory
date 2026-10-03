# Retained volumetric cloud draw

Date: 2026-09-30. Source implementation only. No builds, tests, probes,
packages or game runs. Tests remain deferred until integration is complete.

`GameGraphicsAdapter.Clouds` migrates the retained volumetric draw on the
transparent target. It uses the registered original cloud program, owned mesh
layout/topology, current OIT blend/draw-buffer contract and viewport, with depth
test/write disabled and bound-depth sampling declared. Samplers use existing
program binding records. An unbound liquidDepth sampler names the actual liquid
depth target, preserving the retained native fix. Missing prerequisites use the
existing stated mesh route. The native pass closes on exceptional paths.

`CloudConsumerPatches` resolves the original FluffyClouds renderer from the
official built-in assembly supplied by the version/profile owner. It guards
two Enable calls, one Disable call and one typed RenderMesh call in both original
and incoming IL. Wrappers update the retained state and native draw without
changing original shader loading, camera inversion, time/frame values or cloud
texture selection. The game project does not reference a forked Essentials DLL.

This is a graphics-scene-cloud-volumetric subset. Cloud-map creation, short-data
uploads, point-light uniforms, framebuffer/viewport save/restore and cleanup still
need their lifecycle adapter. Entities/hands, dense motion, shader-mode publication
and remaining host factories are also open. No new code or guard has been compiled
or run, and no cloud/world/provider result is accepted.
