# Current-frame motion publication

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

The original world-loop completion now publishes motion validity before the
later SR/post/presentation chain. The session-owned ledger requires:

- Compiled motion writers enabled and the compiled motion location matching the
  actual primary framebuffer attachment.
- Current rendered-frame identity, captured world view, an open temporal window,
  and completed original opaque/AfterOIT stage traversal.
- Successful liquid velocity redraw and sky/reactive draw through their actual
  return values, with no rejected primary-scene draw recorded during the loop.

Native terrain, entity and particle draw refusal and stated draw refusal invalidate
the frame. A failed native draw inside an already-owned terrain scope invalidates
instead of silently certifying skipped geometry. Missing declared AnimationPrev
binding for a primary-scene animation upload also invalidates. Native entity
passes now close in `finally` and count only successful draws.

Scene exceptions and mid-frame reset requests clear validity. Reset frames can
reach SR with its existing reset flag, but cannot be offered to frame generation.
The existing temporal snapshot retains the jitter actually applied after its
draw window closes. Shader reload, target replacement and settings/provider reset
continue to clear coverage.

The transparent scene pass supplies sky/cloud revealage when enabled. A later
source increment also covers its disabled setting: the liquid redraw is skipped,
revealage is cleared to one (zero coverage), and sky motion still runs. A current
scene-sample guard prevents tail work after an early-returned world loop.
Compilation flags and successful CPU recording do not prove GPU pixels.

## Remaining work

Motion/provider output remains unverified. Remaining analog/hint consumers,
shader override compatibility, packaged native
runtimes and ordinary mod settings/world integration remain open. This increment
removes the permanently-false publication path without accepting a rendered world
or enabled provider.
