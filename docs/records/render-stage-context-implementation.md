# Retained render-stage graph context — 2026-10-01

Implementation only; no builds, tests, probes, packages or game launches.
The preceding goal turn implemented controller panel access/lifecycle boundaries.

Compared donor VulkanClientPlatform.Frame/Graph stage listeners with the current
original TriggerRenderStage Harmony bracket. Declared custom passes were wired,
but stage GPU marks and generic stated draw context remained absent.

Stage entry now invokes the existing idempotent latency stage listener and marks
stage_<EnumRenderStage>. Stage exit, after registered custom passes, closes the
backend stage pass and marks after_<EnumRenderStage>, restoring outer stage state
in finally. Labels are cached as in the donor. Narrow renderer/native pass GPU
sections retain their existing implementation.

Generic stated draws now obtain a cached stage declaration when no explicit
matching post/mod declaration is active. Before/shadow/opaque/OIT keep AllowSplit;
later stages add OpenSampling, matching donor StageFlags. Attached color slots
and actual sampler reads are still derived by the existing stated draw backend.
Explicit post/custom pass declarations take precedence. Target IDs remain owned
by the adapter, and target switches produce a new cached declaration. Outside
stages, the current generic fallback remains unchanged.

Framebuffer publication and stage exit clear the cached declaration. No temporal
math, attachment layout, vendor SDK or graphics algorithm changes. Source is
unbuilt and stage timing/sampling behavior is unverified in the new host. Existing
successful scene evidence predates this correction; full goal remains open.
