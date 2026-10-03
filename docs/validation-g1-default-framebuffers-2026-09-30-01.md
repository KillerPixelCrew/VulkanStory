# G1 default framebuffer/load/unload validation — 2026-09-30

Status: **37 game tests, exercised CPU lifecycle fixture and clean profile-tool
build passed.** One bounded batch ran once. No implementation source changed,
no command was rerun, and no native allocation, game, package or deployment ran.

Release game tests and the dependent profile-tool build exited 0 within their
60-second limits. The [summary](../artifacts/validation/g1-default-framebuffers-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-default-framebuffers-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-default-framebuffers-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-default-framebuffers-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-default-framebuffers-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-default-framebuffers-20260930-01/build.stderr.log)
are retained. Both stderr logs are empty; neither command timed out.

| Gate | Result |
| --- | --- |
| Game cases | 37/37 executed/passed; zero failures/skips. All three framebuffer fixtures passed. |
| Compile | Complete copied default allocation body and target/noise helpers compiled, including TAA/SR/UI/FG slots through 25. No successful positive resource allocation was exercised. |
| Bind/install | Fourteen prefix signatures and cached original private metadata accepted; eight existing original/incoming call-site bodies matched their 34 anchors. Expanded group installation succeeded; new six setup/disposal/load/unload owners/removal assertions passed. |
| Missing host | Original patched setup and enum default load rejected before GL when the explicit host was absent. |
| Minimized setup | Original patched setup returned a 26-slot null list, adopted original SSAO/shadow/scale fields, cleared stale history/motion publication and avoided completed-target/reset callbacks. Provider-plan callback ran and returned no plan. |
| Default load/unload | Actual patched enum load used supplied 640x360 pixels; transparent unload restored depth writes and a 320x180 scaled viewport. Reference unload preserved its original Primary delegation and viewport behavior. |
| Placeholder lifetime | Owned zero-ID shadow placeholders bound with original field visibility and default sentinel. Depth/cull state changed as expected. Attachment swap rejected a nonallocated target. Foreign-list disposal rejected before cleanup; list disposal set disposed flags, unbound current target and invoked FG/AO callbacks once. Repeated list/individual disposal did not repeat callbacks; released binding rejected. |
| Build | Zero warnings/errors. No additional profile inventory execution. |

The borrowed device had no Vulkan context, active frame or positive target.
The placeholder factory created only managed objects with zero public IDs; no
positive GPU handles were fabricated. This does not prove shared positive-depth
deduplication/retirement, full setup/rebuild allocation, sampler/format/noise
pixels, compact depth, upscaler planning, TAA/UI/FG inputs, composite routing,
fault cleanup, GPU clears/draws or live resize/shutdown.

Callbacks were fixture-owned CPU delegates. The process session still must wire
real SDL pixels/settings, provider planning/disablement, temporal publication,
FG reset and AO release. Query/capture must replace remaining pixel-pack-buffer
uses; other graphics/state/post/scene/startup routes remain required before the
complete transaction can activate. No backend/contract/bootstrap suite, enabled
SDK or live game was run. Full renderer/SDL/provider/menu/world/release acceptance
remains open. See [implementation/provenance](game-default-framebuffers-implementation.md).
