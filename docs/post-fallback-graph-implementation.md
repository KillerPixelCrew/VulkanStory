# Stated post fallback graph declarations — 2026-10-01

Implementation only; no builds, tests, probes, staging or game launches.
The previous turn migrated stage graph context and GPU labels.

Native post pass declarations already mark transient outputs. DrawPostTriangle's
stated fallback declaration did not carry donor PassReads/PassTransientSlots.
Migrated the donor Graph Post branch into GameGraphicsAdapter.PostGraph.cs,
retaining existing target indices, color/depth read helpers and missing-handle
filtering. Bright/god-rays/luma conservatively name Primary and both TAA scene/glow
histories; blur/SSAO reads follow the retained predecessor/history targets.
Actual bound shader sampler reads are still merged by the existing stated draw.

The fallback declaration now supplies those reads and marks color 0 transient
for the retained bright/bloom/SSAO blur slots. Target allocation and alias opt-in,
post shaders, formats, bindings and algorithms remain unchanged. Explicit mod
declarations and native routes retain their existing logic.

Source is unbuilt/unrun. Current successful native/world capture evidence does
not prove this fallback branch or transient alias parity. Full renderer/SDL/
provider acceptance remains open; tests remain deferred.
