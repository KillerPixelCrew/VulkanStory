# G1 sampler/binding repair validation — 2026-09-30

Status: **30 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
binding/draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-sampler-routing-20260930-02/summary.json),
[test stdout](../artifacts/validation/g1-sampler-routing-20260930-02/test.stdout.log),
[test stderr](../artifacts/validation/g1-sampler-routing-20260930-02/test.stderr.log),
[TRX](../artifacts/validation/g1-sampler-routing-20260930-02/game.trx),
[build stdout](../artifacts/validation/g1-sampler-routing-20260930-02/build.stdout.log)
and [build stderr](../artifacts/validation/g1-sampler-routing-20260930-02/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 30/30 executed/passed; zero failures/skips. |
| Expanded compilation | Retained state/native-draw helper and sampler adapter compiled; Graph alias failure resolved. |
| Bindings/anchors | GenSampler and explicit-unit 2D/cube signatures resolved; Stop had one expected BindSampler call in original/incoming IL. |
| Harmony fixture | Expanded group installed, including selection and Stop unbind transpilers. Sampler/2D prefix ownership/removal passed alongside existing uniform/selection checks. |
| Tool build | Zero warnings/errors. No additional inventory execution. |

The fixture never executes successful sampler/texture creation/binding, Stop
cleanup against a real adapter, or the generic native draw helper. Native sampled
pixels, custom-sampler state, clamping, per-program registries and lifetimes
remain unverified. UBO/disposal, platform resources/state/draw and complete
startup/menu/world/provider/SDL/release acceptance remain open. See
[implementation/provenance](game-sampler-routing-implementation.md).
