# Direct animated content draw routing

Updated 2026-10-01. Source integration only. No builds, tests, probes, packages
or game runs; tests remain deferred until integration is finished.

`DirectAnimatedMotionConsumerPatches` connects the three original Echo Chamber
`IRenderAPI.RenderMesh` calls to the retained native entity seam, using each
draw's declared `entityTex` binding. The original renderer calculates and uploads
one model/animated pose before all three draws; the existing Animation history
route supplies its previous pose once. Each draw opens only its own primary
motion window and closes it in `finally`, preserving an enclosing owner.

The patch checks the original renderer signature and all three original/incoming
draw anchors. Inactive routing and non-animated programs preserve the original
draw call. Original texture binding, pose, lighting, model transform and game
renderer lifecycle stay with the original Survival implementation.

Provenance: retained `VSSurvivalMod/Lore/ResoArchives/EchoChamberRenderer.cs`
and its matching motion patch at baseline
`386e0d05386d0b228b439d09aeca851428f7bbf3`.

Source inspection also confirmed that first-person hands register the
`entityanimated` pass and call the original batched shape renderer, whose
multi-texture draws already reach `EntityConsumerPatches` and its animated motion
scope. Their Animation upload and temporal view/projection use the existing
owned histories. This is source path evidence, not runtime acceptance.

Complete scene/profile composition, remaining renderer coverage and per-frame
motion validity remain unfinished. No direct animated pixels, first-person hand
behavior, Harmony installation or vendor input has been accepted by this increment.
