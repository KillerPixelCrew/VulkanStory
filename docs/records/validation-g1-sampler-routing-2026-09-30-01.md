# G1 sampler/binding validation — 2026-09-30

Status: **game compilation failed; no tests ran.** One bounded batch ran once.
No implementation source changed, no command was rerun, and no native/game,
package or deployment check occurred. The profile-tool build was skipped.

Release game tests exited 1 before execution; no TRX was produced. The
[summary](../../artifacts/validation/g1-sampler-routing-20260930-01/summary.json),
[stdout](../../artifacts/validation/g1-sampler-routing-20260930-01/test.stdout.log)
and [stderr](../../artifacts/validation/g1-sampler-routing-20260930-01/test.stderr.log)
are retained. The command completed within 60 seconds.

`StatedRenderState.cs:184` reports CS0103 for `Graph`. Its retained redirect
condition qualifies `Graph.PassDeclaration.DefaultFramebuffer`, which relied
on the original sibling namespace arrangement. After moving to VulkanStory.Game,
the existing graph namespace import does not define the short alias. Add an
explicit `Graph = VulkanStory.Render.Vulkan.Graph` alias (or fully qualify the
reference) in the next implementation turn; preserve the redirect algorithm.

No successful compilation or sampler target/Stop anchor installation acceptance
exists for this increment. Successful binding state, generic native draws,
UBO/disposal/platform resources and complete startup/menu/world/provider/SDL
and release acceptance remain open. No source fix or second batch ran here.
