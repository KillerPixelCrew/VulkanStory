# Retained occlusion-query ring port

Date: 2026-09-30. Implementation only: no builds, tests, probes, packaging, or game runs.

The unchanged retained `Frame/QueryRing.cs` joins the backend compile list. Its per-slot pool/reset/harvest behavior, timeline-gated polling, previous-result fallback, suspended/resumed query segments, abandoned/deleted objects and disposal are preserved from baseline `386e0d05386d0b228b439d09aeca851428f7bbf3`. It already has no game dependency.

`--sdl-mesh-query` extends the indexed pixel preflight by wiring the actual render-target scope hooks to the ring. One query covers two rendering scopes containing indexed draws, and another covers an empty scope. It checks unavailable state before submission, availability after the existing readback wait, a finite positive draw result versus zero empty result, and deletion. The original triangle/background pixel checks and presentation remain part of this path. No extra query wait is added; completion comes from the readback timeline wait. Pools survive until waited teardown.

This is source-only. The next bounded batch should run backend tests, build the tool and execute this one native query path. A successful run would not prove exact sample totals on all hardware, multi-pool overflow, slot recycling, partial-frame submissions, other-mod query use, or game glare/culling behavior. The full device facade, graphics/API routing, startup/window ownership, provider/world rendering and port acceptance remain open.

The [first bounded validation](validation-p0-query-2026-09-30-01.md) passed 47 tests, a clean build, native segmented/empty query checks, selected pixel assertions and presentation. Broader query lifecycle and game integration remain open.
