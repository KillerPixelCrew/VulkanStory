# Required composition pipeline readiness — 2026-10-01

Implementation only: no builds, tests, probes, packages, snapshots or launches.

Headless captures previously forced synchronous pipeline compilation globally.
Normal runs retain asynchronous scene compilation. Source inspection found final
composition, luma, HUD-less scene copy and UI composition used the ordinary
skippable fullscreen lookup, unlike the already-corrected mandatory final blit.
A pending pipeline can therefore leave required output unwritten on first use.
This is a concrete code path, not proof of the earlier black image's precise cause.

Those required native fullscreen draws now request blocking pipeline readiness
on cache misses. Final composition's stated fallback and final blit's stated
fallback propagate the same policy through DrawOwnedFullscreen/RecordStatedDraw/
StatedDraw.Record. Legacy luma also requests required readiness. Generic scene
meshes/mod draws and optional post effects retain asynchronous lookup. Shader,
blend, sampler, resource formats and render algorithms are unchanged.

Final composition now rejects an undrawn required output with native/stated refusal
information. Stated draws retain native refusal text instead of a silent false.
An unsupported/missing pass is not treated as successful composition.

Diagnostic source capture waits for 60 current world samples and, when SR is
selected, successfully composed upscaled output. This replaces counting early
attached/loading frames. The harness adds -AsyncPipelines, mapping to an explicit
false device SynchronousPipelines setting, so an isolated hidden run can exercise
the normal compile policy instead of capture automatically masking it. Default
capture behavior stays deterministic/synchronous.

No native ABI changed. The matching bundle from headless-cleanup-20261001-184218
can be reused after a new Game/backend/SDL build. Installed game untouched. Cold
asynchronous capture, first-use composition, broad visual parity, provider transitions,
SDK hook warnings and remaining feature/release gates remain unverified.
