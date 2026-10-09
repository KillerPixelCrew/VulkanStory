# Bounded menu diagnostic startup — 2026-10-01

One build/stage/owned-update/launch batch; no tests, fixes, reruns or archive builds.
Game build passed with zero errors/warnings. Stage/update passed. PID 35540
activated SDL/Vulkan and subsequently recorded device drain/shutdown. Crash
handling exited 0. Installed candidate now contains the diagnostic build.

Artifacts: `artifacts/validation/runtime-startup-20261001-145823/` including full
logs/trace, result JSON, backup, stage and copied client-crash.log.

## Captured first exception

Frame 3, original screen instruction 144:
`GuiScreen.RenderToDefaultFramebuffer(float)`.

Preserved first stack:
BindingsBase.Uninitialized → ShaderProgramBase.Use_Patch1 →
MainMenuRenderAPI.Render2DTexture → GuiComposer.Render →
GuiScreenLoadingGame.RenderToDefaultFramebuffer → ScreenManager.Render.

This identifies the shader Use subtree of the loading GUI, superseding the
previous flattened-stack uncertainty. No successfully rendered menu/world or
evaluated provider frame is accepted by this batch.

## Source diagnosis

Use's original body contains GL.UseProgram, then original uniform/sampler/UBO
calls, including the GUI lightPosition three-float uniform. SelectionTranspiler
routes its direct UseProgram call; leaf uniform/sampler/UBO routes are installed
after selection wrappers in the current installation order. Small original
setters can be inlined when a caller wrapper is compiled before their detours
are installed. This is a supported source-level hypothesis for the raw GL call,
not proof of a particular native GL function from the current stack.

Next implementation: install shader leaf routes before rebuilding Use/Stop
caller wrappers, preserving original active-shader checks and uniform semantics.
If further narrowing is needed, use the existing first-error diagnostic rather
than dummy GL bindings or dropping shader behavior. No source fix or second
launch was performed in this turn. Native rendering/SDK acceptance remains open.

## Following shader installation source correction — 2026-10-01

Uniform and UBO routes now install first, followed by sampler/disposal routes and
platform methods, then the final Use/Stop selection wrappers. Thus the final
callers are rebuilt after every leaf route, including Stop's sampler and UBO
paths. Existing guards, setters, original state updates and GL fallback for
inactive routing are unchanged. All targets still validate before installation;
the same owner/transaction removal handles partial installation failures.

Implementation only: no build/tests/package/deploy/game run. The inlining
hypothesis remains unverified until the corrected order runs; the installed
candidate still contains the preceding loading-screen failure. Temporary bounded
menu diagnostics remain to identify any surviving path.
