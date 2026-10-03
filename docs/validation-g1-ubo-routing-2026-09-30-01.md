# G1 UBO lifecycle routing validation — 2026-09-30

Status: **30 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
buffer write/draw, game launch, package or deployment occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-ubo-routing-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-ubo-routing-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-ubo-routing-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-ubo-routing-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-ubo-routing-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-ubo-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 30/30 executed/passed; zero failures/skips. |
| UBO bindings | CreateUBO, Bind, Unbind, object-range Update and Dispose resolved and installed. |
| Disposal anchor | Original/incoming IL contained one expected GL.DeleteBuffers call; receiver-carrying replacement installed. |
| Fixture behavior | Bind rejected an active ownerless UBO before original GL. Bind/Dispose ownership and removal assertions passed. |
| Compilation | Mod-owned metadata/handle adapter compiled; tool build reports zero warnings/errors. |

No real buffer was created/updated/disposed through the adapter, and no pixel
or range-byte result was produced. Pin failure cleanup, base disposed state,
repeated deletion and real GPU snapshots remain unverified. Generic Update<T>
raw GL uploads are still unported. Complete resources/state/draw/startup,
menu/world/provider/SDL/release acceptance remain open. No extra profile
inventory ran. See [implementation/provenance](game-ubo-routing-implementation.md).
