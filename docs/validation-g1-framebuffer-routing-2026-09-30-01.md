# G1 framebuffer ownership/binding validation — 2026-09-30

Status: **35 game tests, successful null-binding CPU fixture and clean
profile-tool build passed.** One bounded batch ran once. No implementation source
changed, no command was rerun, and no native framebuffer/game/package/deployment ran.

Release game tests and profile-tool build exited 0 within 60-second limits.
The [summary](../artifacts/validation/g1-framebuffer-routing-20260930-01/summary.json),
[test stdout](../artifacts/validation/g1-framebuffer-routing-20260930-01/test.stdout.log),
[test stderr](../artifacts/validation/g1-framebuffer-routing-20260930-01/test.stderr.log),
[TRX](../artifacts/validation/g1-framebuffer-routing-20260930-01/game.trx),
[build stdout](../artifacts/validation/g1-framebuffer-routing-20260930-01/build.stdout.log)
and [build stderr](../artifacts/validation/g1-framebuffer-routing-20260930-01/build.stderr.log)
are retained.

| Gate | Result |
| --- | --- |
| Game cases | 35/35 executed/passed; zero failures/skips. |
| Binding/install | Four official framebuffer signatures and original curFb metadata matched; prefixes installed. |
| Actual null binding | Public setter updated the original field/getter to null and native target sentinel -1, retaining viewport offset/width. |
| Ownership | Foreign positive target rejected before original field mutation. Setter owner/removal assertion passed; dormant getter retained its original value. |
| Build | Zero warnings/errors. No additional profile inventory execution. |

No native target or attachment was created, positively bound or disposed. The
private viewport-preserving setter installed but was not invoked by the fixture.
Positive viewport behavior, released-state/repeated disposal, shared textures,
in-flight scope transitions and rendered pixels remain unverified. Complete
default framebuffer set/load/unload/clear/query/capture/post-chain and full
startup/menu/world/provider/SDL/release acceptance remain open. See
[implementation/provenance](game-framebuffer-routing-implementation.md).
