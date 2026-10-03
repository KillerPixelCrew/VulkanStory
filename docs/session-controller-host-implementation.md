# Session controller and GUI attachment

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

The external platform callback factory is removed from `GameSessionServices`.
The session now constructs the retained SDL mapper, a concrete
`SessionControllerHost`, the GUI text coordinator and all platform callbacks.

- Actual SDL focus, relative mouse mode, pixel dimensions and cursor position
  feed the host. Controller keys/buttons use the existing source-aware input
  bridge; cursor warps retain the adapter's physical/controller distinction.
- Native gamepad updates precede the vendor input-start marker and SDL pump.
  The adapter owns the only queue drain. Hotplug/activity feeds the mapper and
  mapping runs after queued event dispatch, before GUI synchronization/rendering.
- The controller keyboard screen/dialog targets feed the retained IME coordinator.
  Physical input changes the owned hint source and requests GUI recomposition.
- Resize, input-age recording, pacing/markers and graphics drain bind directly
  to their real session owners. Controller disposal/profile flush, input release
  and GUI text stop occur while the SDL window is alive.
- Controller movement state belongs to the session. Analog readiness requires
  explicit acknowledgment for the active client; no injected game field or
  implicit multiplayer capability is assumed.
- Game-assembly controller P/Invokes register with the same package-selected SDL
  resolver as the window/event assembly, avoiding separate native search paths.

Source inspection also removed a stale validation reference to the deleted
external framebuffer factory. No validation commands were run.

## Remaining integration

Analog packet/companion negotiation and movement-consumer hooks, hint consumers,
mapping database delivery, remaining service construction and complete startup
profile registration are unfinished. This attachment is source-only and remains
dormant with the incomplete profile. No live controller/IME, haptics, resize,
shutdown or latency-marker ordering has been accepted in the new host.
