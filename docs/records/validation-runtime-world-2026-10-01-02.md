# SVG route real-world loading result — 2026-10-01

One bounded validation batch, run once: Game/backend Release build, fresh stage,
owned deployment and original client launch with --openWorld "foggy village story".
No tests, native/shader rebuilds, archives, source repairs or second launch.
Artifacts: `artifacts/validation/runtime-world-20261001-152735/`.

Build passed with zero warnings/errors. Stage/deploy passed with the matching
WithExtent native bundle from runtime-world-20261001-152358. Existing save/WAL/SHM
were backed up in the batch before launch. No development superflat was used.

PID 40092 stayed alive through the first 30-second interval, then crashed during
level finalization at 15:28:12, before the planned 90-second bound. Exit code 0 is
crash-handler return, not success. No close request was issued. Bootstrap captured
runtime.stopped/device drain before normal process exit.

## Progress and exact failure

The preceding SvgLoader.GL.GenTexture error did not recur. World block assets
progressed through loading and client reconnection to level finalization. Native
shader logs advanced to 55 native/4 rewritten/0 failed, followed by additional
program links; these are scoped log counts, not complete shader acceptance.

The preserved first exception at screen frame 1040 is:
GL.GetString(StringName) -> ClientSystemStartup.HandleLevelFinalize -> packet task
-> ClientMain.ExecuteMainThreadTasks -> connecting-screen render.

Original source calls GL.GetString(Renderer=7937) after AmbientManager.LateInit
only to check for an Intel Arc advisory when AllowSSBOs is set. Existing platform
GetGraphicsCardRenderer routing already returns Device.RendererString, but this
startup consumer bypasses it. A later implementation must route that exact query
while preserving the original finalization, focus/pause callbacks and advisory.
Do not replace the entire world finalization body or fabricate a renderer string.

## Provider and remaining acceptance

The initial optional backbuffer extent and missing debug-utils warnings are both
absent in this batch. The three SDK hook-map warnings remain visible/unresolved.
No generated-frame evidence or SR evaluation was captured. Playable world,
presented pixels, temporal motion correctness, input/settings, vendor execution
and complete world-session shutdown remain unverified. Source is now compiled
and installed; candidate archives remain stale. No repair or rerun this turn.
