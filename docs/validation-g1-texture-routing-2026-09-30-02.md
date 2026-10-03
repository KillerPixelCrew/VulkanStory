# G1 texture routing reference-repair validation — 2026-09-30

Status: **29 game tests and clean profile-tool build passed.** One bounded batch
ran once. No implementation source changed, no command was rerun, and no native
resource/game/package/deployment check occurred.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-texture-routing-20260930-02/summary.json),
[test stdout](../artifacts/validation/g1-texture-routing-20260930-02/test.stdout.log),
[test stderr](../artifacts/validation/g1-texture-routing-20260930-02/test.stderr.log),
[TRX](../artifacts/validation/g1-texture-routing-20260930-02/game.trx),
[build stdout](../artifacts/validation/g1-texture-routing-20260930-02/build.stdout.log)
and [build stderr](../artifacts/validation/g1-texture-routing-20260930-02/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 29/29 executed/passed; zero failures/skips. |
| Texture target binding | Three declared official signatures resolved with expected void returns and concrete method bodies. |
| Harmony fixture | Texture subset installed; deletion target had the expected owner. An active missing-adapter deletion threw before original GL. Owner-specific removal cleared that deletion prefix. |
| Compilation | Game/backend/test references and profile tool compiled; tool build reports zero warnings/errors. |

The [earlier compile failure](validation-g1-texture-routing-2026-09-30-01.md)
is resolved by the explicit official Harmony reference. The fixture uses a
constructor-bypassed original platform and has no graphics adapter/device/window.
It does not exercise successful creation/mipmaps/deletion, dormant original GL,
native pixels, lifetime/drain, full graphics transaction or live startup.
The profile tool was built but not executed for another IL inventory in this
batch. Actual game menus/worlds, providers, SDL controls and release remain open.
