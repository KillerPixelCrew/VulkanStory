# G1 shader selection repair validation — 2026-09-30

Status: **game compilation failed; no tests ran.** One bounded batch ran once.
No implementation source changed, no command was rerun, and no native/game,
package or deployment occurred. The profile-tool build was skipped.

The Release game-test command exited 1 within 60 seconds, before test execution;
no TRX was produced. The
[summary](../../artifacts/validation/g1-shader-routing-20260930-02/summary.json),
[stdout](../../artifacts/validation/g1-shader-routing-20260930-02/test.stdout.log)
and [stderr](../../artifacts/validation/g1-shader-routing-20260930-02/test.stderr.log)
are retained.

`ShaderConsumerPatches.cs:144` reports CS0103 for `ScreenManager` in the new
selection wrapper. The original shader source sits under the enclosing
`Vintagestory.Client` namespace; our `VulkanStory.Game` source imports only
`Vintagestory.Client.NoObf`. The original snapshot declares `ScreenManager` in
`Vintagestory.Client`. Import or qualify that namespace in the next implementation
turn, then validate the official target and original/incoming IL checks. No
source repair or second run occurred in this validation turn.

The previous injected-target issue has a source repair, but no successful
compilation or patch acceptance for it yet. Three platform prefixes and two
selection transpilers remain unverified together. Surrounding uniform/sampler/UBO
GL routes, successful shader frames, complete startup/menu/world, providers,
SDL controls and release remain open.
