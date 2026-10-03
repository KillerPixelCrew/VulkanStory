# World finalization live checkpoint — 2026-10-01

One bounded validation batch, run once: Release Game/backend build, fresh stage,
owned installation update and original executable --openWorld "foggy village story".
No tests, native/shader rebuilds, archives, source repairs or second launch.
Artifacts: `artifacts/validation/runtime-world-20261001-153130/`.

## Result

Build passed with zero warnings/errors; stage/deploy passed. The existing save
and WAL/SHM were backed up before launch. Matching native WithExtent bridge was
reused from runtime-world-20261001-152358. No development superflat was used.

PID 8720 remained alive for all three 30-second intervals, with title Vintage
Story and window handle 3018000. Logs recorded server GameReady/WorldReady,
Received level finalize, resumed client/server simulation and world activity.
The direct finalization GL query failure did not recur. No render.screen.failed,
new critical exception or stated-draw refusal was recorded.

At the 90-second bound, CloseMainWindow returned true and the process exited
within 10 seconds, code 0. Bootstrap recorded device drain before SDL teardown
and normal Main/process return. Server saved the existing world (3539 chunks,
713 mapchunks, 32 mapregions) and shut down. No new crash reporter remained.
Historical copied crash log is not evidence of a current failure.

## Scope and remaining work

This proves bounded startup through user-world finalization/simulation and normal
close on the installed candidate. No screenshot, pixel capture or interactive
input observation was taken, so visual correctness/playability is not claimed.
SR availability and provider initialization are not proof of per-frame SR/FG
execution. Three Streamline hook-map warnings remain visible; initial extent and
debug-utils warnings are absent.

Next work should obtain presented pixels and real provider execution/status in
this same user world, preserving full renderer/features. Controller/settings,
temporal scene coverage, resize/toggle/vendor/platform/coexistence gates and
current release archives remain open. Installed source now includes the final
renderer query correction. No repair or rerun occurred in this validation turn.
