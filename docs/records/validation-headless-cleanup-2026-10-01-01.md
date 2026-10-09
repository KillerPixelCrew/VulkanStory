# Unused DLSS-G cleanup headless result — 2026-10-01

One bounded matching bridge/Bootstrap/Game build -> bundle/stage -> isolated FSR 3
capture batch, run once. No tests, installed deployment, source repair or second run.
Artifacts: `artifacts/validation/headless-cleanup-20261001-184218/`.

Native and managed builds passed; managed builds have zero warnings/errors. Fresh
bundle and stage passed. PID 12992 loaded the isolated foggy village story snapshot,
captured all three frames/attachment/AO outputs, and shut down automatically.
Result: success, hidden=true, focused=false. Device drain recorded. Installed game,
user process and original settings/save untouched.

The preceding repeated slDLSSGSetOptions shutdown warning is absent. Camera warning
and NGX release error remain absent. Three known supplied SDK hook warnings remain.
This supports unused-provider cleanup in this FSR-owned session; it does not prove
all DLSS activation/switch/resize resource-lifetime branches.

Final periodic sample: 230 successful SR frames, 228 prepared FG frames, 666 real
presents and 891 SDK-reported presents (225 additional outputs). The sample FPS
is transient, not a benchmark; no pacing/performance acceptance inferred. Capture
completion and SDK output are separate from full visual parity.

Current matching bundle is native-bundle in this batch and includes the new
DisableFrameGenerationForRelease export. Future staged harness runs must use it.
No current installed delivery or release archive was generated. Remaining provider
switches/quality/resize, broader scene/input/settings, FSR 4 hardware, platform/
proxy coexistence and release gates stay open. No source repair or rerun here.
