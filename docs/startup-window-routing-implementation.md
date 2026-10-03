# Original startup window and platform routing

Date: 2026-09-30. Source-only implementation; no build/test/probe/game run.
Test work remains deferred while full integration is finished.

The original platform constructor is retained and associated with the process
runtime after construction. A window-request prefix creates SDL/Vulkan from the
original NativeWindowSettings, including size/state/border. The successful path
returns no GameWindowNative: the original window field stays null, and checked
startup call substitutions supply SDL centering and pixel dimensions. No SDL
handle is cast into a GLFW object or pointer.

ClientProgram.Start retains its argument/data-path/login/clean-install/mutex,
audio, single-player server, ScreenManager.Start and cleanup control flow. Its
GLFW error callback is bypassed on the prepared native route; the unused callback
return/pop is checked before substitution. The three shutdown calls replace the
GLFW pointer/iconify and NativeWindow.Dispose operations with SDL minimization
and ordered session shutdown. The previously added frame group supplies SDL Run.
Each group's original and incoming call counts are checked at installation.

If preparation fails and releases resources before commit, original startup can
continue. Aggregate cleanup/rollback failures propagate; after commit, renderer
calls cannot switch back to GL. Real runtime fallback and teardown are unverified.

The platform-start subset replaces its GLFW event subscriptions and graphics
initialization with the existing SDL adapter, retained default framebuffer setup,
minimal GUI shader compilation, window pixels, line capability and CPU metadata.
It adds Vulkan-backed graphics information/error/debug/texture-limit routes,
VSync and resize handling. Original WindowExit logging, server flags and URI
cleanup remain; a postfix queues the SDL close request onto the pump thread.

A startup composition builds the six required transaction groups only when a
complete graphics-api group is supplied. Graphics composition requires ordinary
resource/query/platform groups plus scene and post groups, and rolls back installed
children in reverse order. Scene/post coverage and actual service factories are
still missing, so no complete active profile is registered by this increment.

Remaining work includes the rest of mandatory window consumers, native scene/post
routes, concrete settings/controller/temporal/provider factories, late mod/world
attachment and release delivery. This is not first-window, menu, world, SDK or
player-package acceptance. Sources are the exact original startup/platform flow
and retained SDL/Vulkan ownership paths; no modified game/donor dependency is added.
