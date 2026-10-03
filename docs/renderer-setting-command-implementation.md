# API-only renderer setting commands — 2026-10-01

Implementation only: no builds, tests, probes, packages, snapshots or launches.

The ordinary mod now supports `.vulkanstory set <setting> <value>` using only its
existing framework JSON/delegate control bridge. It reads the current settings,
updates an existing key case-insensitively, validates primitive type/finite numeric
input and categorical provider/quality/AO/latency choices, then submits the complete
settings through ApplySettings. Unknown keys/choices/types return a command error.
Backend normalization remains the source of numeric ranges/restart behavior.
No Game/Harmony/renderer/native assembly reference is added to the Mod project.

Examples:
- `.vulkanstory set Upscaler xess`
- `.vulkanstory set UpscalerQuality balanced`
- `.vulkanstory set FrameGeneration xess`
- `.vulkanstory set FrameGenerationMultiplier 2`
- `.vulkanstory status`

Status now returns the existing actual-frame presentation text, including evaluation,
FG preparation and available SDK counts/capabilities, instead of only runtime active.
Settings/reload commands remain available. Saved changes are queued to the owner
thread; device/window options still require restart. These are usable product
controls, also usable by the migrated headless chat script.

Source review found ReadSettings returned only the currently applied record, so
sequential edits before the next frame could overwrite prior queued edits. The
bridge now publishes the latest normalized requested record after successful save
and reload. Each command merges into that request; actual presentation status stays
separate. Existing owner-thread application and atomic immutable records remain.

New Mod/Game source is unbuilt. A subsequent bounded batch must build Mod as well
as Game/Bootstrap and stage the new ordinary DLL into the isolated harness directory.
No installed update is needed. Command dispatch, sequential changes, provider/quality
transitions and their runtime/visual results remain unverified. Full port/release
scope, other hardware/platform and SDK hook limitations remain open.
