# Motion completion without the transparent pass

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

The original world-loop completion now handles both values of
`ClientMain.doTransparentRenderPass`:

- Enabled: the retained liquid redraw runs and sky motion reads this frame's OIT
  revealage, preserving transparent coverage/reactivity.
- Disabled: liquid geometry is not redrawn, and the existing transparent reveal
  attachment is cleared to one before sky motion. The unchanged retained shader
  computes `1 - revealage`, so this represents zero transparent coverage without
  reading stale contents from a preceding frame.

The tail requires current opaque/AfterOIT traversal, an open temporal window,
captured world view and matching rendered-frame identity before it records work.
An early-returned world loop cannot use an old camera sample to overwrite motion
or publish validity. The existing compiled-target, draw-failure and reset guards
remain in effect; missing targets or failed sky draws still reject coverage.

No shader math, target layout or provider algorithm changed. Native clear/pass
operations use the retained graph/resource tracking; the CPU framebuffer binding
is not redirected by the reveal clear.

Both transparency choices are connected in source. Native clear/sky pixels and
SR/FG operation remain unverified. Remaining analog/hint consumers, shader override
compatibility, runtime packaging and ordinary mod settings/world attachment stay
open; full port acceptance is not claimed.
