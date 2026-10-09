# Retained scene/UI separation

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or
game runs. New test work remains deferred until integration is complete.

`GameGraphicsAdapter.UiSeparation` ports the retained HUD-less scene snapshot,
transparent-black UI target clearing, default-target redirection and final
premultiplied composition. Existing framebuffer slots 23 and 24 are reused.
The adapter owns the `ui-compose` shader instead of requiring an injected
ShaderPrograms field. The unchanged GLSL sources are embedded in the game
project; available asset replacements are read first. Shader reload invalidates
the owned program and its pipeline caches. The backend's existing native shader
selection/mod-override scan remains part of the unfinished concrete host setup.

The fullscreen cache now accepts backend-owned program IDs as well as original
game shader objects. It retains program/target-format identity and live-pipeline
checks. Snapshot capture reports success only when the fullscreen draw succeeds.
Failure to provide the UI target/program leaves GUI drawing on the default
target. Composition closes redirection before rebinding the window and restoring
its depth/blend state; repeated composition is harmless.

`UiConsumerPatches` provides a required `graphics-ui` group:

- The original platform blit postfix captures the scene and opens the UI scope.
- A checked ClientMain.RenderToDefaultFramebuffer substitution composes before
  its Done render stage, preserving screenshot and AVI timing.
- ScreenManager.Render composes menu UI at its end; an exception closes the
  scope without swallowing the error.
- The screen's raw depth clear and depth-range calls are replaced with typed
  wrappers. Original GL pass-through remains while routing is dormant. On the
  Vulkan route, the original 20000 values clamp to the same default 0..1 depth
  interval, and clearing follows the current UI/default redirection.
- The original ShaderRegistry.ReloadShaders prefix invalidates the UI program.

Original and incoming bodies must retain one screen depth-clear call, one
depth-range call, two client render-stage calls and exactly one Done-stage
argument sequence. These guards are implemented but have not been executed in
this increment.

## Remaining integration

The original blit still needs its native replacement; its new postfix does not
remove its other OpenGL operations. AO/TAA, bloom/god rays/final/blit, dedicated
scene/motion producers, controller/settings factories and complete startup
registration remain open. The UI group alone cannot activate graphics routing.
No rewritten menu, UI pixel or enabled FG result is accepted from this work.
