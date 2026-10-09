# G1 texture routing validation — 2026-09-30

Status: **game adapter compiled; test project compilation failed.** One bounded
batch ran once. No implementation source changed, no command was rerun, and no
native/game/package/deployment check ran.

The Release game-test command exited 1 before executing tests. The dependent
profile-tool build was skipped. No TRX was produced. The
[summary](../../artifacts/validation/g1-texture-routing-20260930-01/summary.json),
[stdout](../../artifacts/validation/g1-texture-routing-20260930-01/test.stdout.log)
and [stderr](../../artifacts/validation/g1-texture-routing-20260930-01/test.stderr.log)
are retained. The command completed within its 60-second limit.

## Failure and source diagnosis

`TextureConsumerRoutingTests.cs:3` reports CS0246 for `HarmonyLib`.
The fixture directly calls Harmony patch metadata APIs. The game project has
an explicit official `Lib/0Harmony.dll` reference with copy disabled, but the
test project has no direct Harmony reference. Add that same official compile
reference with `Private=false` to the test project in the next implementation
turn. Its existing official-game runtime resolver remains responsible for loading
the game-supplied DLL; do not package an extra Harmony copy.

Backend and game integration assemblies compiled in this batch, including the
renderer association and texture bodies/prefix source. This does not validate
the three target signatures or actual Harmony installation/removal, because
the fixture never ran. Successful texture routing, complete graphics/startup
groups, menus/worlds, providers, SDL controls and release remain open.
