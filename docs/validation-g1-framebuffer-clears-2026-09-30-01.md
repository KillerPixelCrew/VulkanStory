# G1 framebuffer clear and draw-buffer routing validation — 2026-09-30

Status: **36 game tests and a clean profile-tool build passed.** One bounded
batch ran once. No implementation source changed, no command was rerun, and
no native framebuffer, game, package or deployment check ran.

Release game tests and the dependent profile-tool build both exited 0 within
their 60-second limits. The [summary](../artifacts/validation/g1-framebuffer-clears-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-framebuffer-clears-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-framebuffer-clears-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-framebuffer-clears-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-framebuffer-clears-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-framebuffer-clears-20260930-01/build.stderr.log)
are retained. Both stderr logs are empty; neither command timed out.

| Gate | Result |
| --- | --- |
| Game cases | 36/36 executed/passed; zero failures/skips. Both framebuffer fixtures passed. |
| Official bindings | Eight prefix signatures and cached curFb/clearColor metadata accepted. Eight original call-site bodies matched their exact selection/color-clear anchors. |
| Incoming IL/install | All eight transpilers accepted incoming Harmony instructions: 18 DrawBuffer, 8 DrawBuffers and 8 color ClearBuffer calls. The configured group spans fourteen distinct methods; actual installation succeeded. |
| Owners/removal | Expanded fixture asserts ownership/removal for clear prefixes and selection/post method bodies; prior public framebuffer setter checks also passed. |
| CPU dispatch | Original patched clear-color setter and Primary/Transparent clear prefixes dispatched without GL. Original private clear array retained its independent default. Direct selection wrappers applied None/Back masks; invalid counts/selectors rejected without changing the mask. |
| CPU clear/ownership guards | Direct post-clear wrapper dispatched; unsupported clear kind rejected. Both original reference-clear overloads rejected foreign targets. Default clear rejected an unready target set before current-target mutation. Previous null binding/original-field/viewport fixture passed again. |
| Build | Zero warnings/errors; no extra profile inventory execution. |

The borrowed device had no Vulkan context, active frame or native framebuffer.
No attachment pixels or submitted clears were produced. Direct wrapper dispatch
does not prove that complete original load/unload/setup/post methods can run
without GL; they retain other unported operations. Default positive target
restore, explicit-reference successful clears, depth/color masks on GPU,
SSAO/OIT/motion clears, positive selection, full clear-color values, partial
channel-mask parity and in-flight resource lifetimes remain unverified.

The batch did not repeat backend/contract/bootstrap suites, run a live game,
evaluate providers or deploy a payload. Next implementation completes default
target setup/shared-depth lifecycle and load/unload, followed by remaining
query/capture/state/post/startup routes. The incomplete subset cannot activate
the mandatory complete graphics group. Menu/world rendering, SDL startup,
enabled providers and release acceptance remain open. See
[implementation/provenance](game-framebuffer-clears-implementation.md).
