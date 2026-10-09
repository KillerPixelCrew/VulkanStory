# Scheduled PNG and mod-isolation result — 2026-10-01

One bounded Bootstrap/Game build -> fresh stage -> isolated asynchronous FSR
capture batch, run once. No tests, native rebuild, installed deployment, source
repairs or second run. Artifacts:
`artifacts/validation/headless-isolation-20261001-190433/`.

Builds passed, zero warnings/errors; stage passed. PID 41504 captured three PPM/PNG
pairs and attachment/AO files, automatically shut down and recorded device drain.
Result confirms hidden=true/focused=false. No new renderer/cleanup exception.
Known SDK hooks remain. User game/settings untouched.

## Captures

Inspected frame-000240.png: upright world terrain/vegetation/sky and HUD. The larger
mountain geometry and distant terrain absent in earlier diagnostic PNGs are visible
in this later scheduled capture. This supports delayed loading/capture timing as
an explanation, but is not synchronized full parity or proof for all scenes.
Final sample: 241 successful FSR SR frames, 239 FG-prepared frames, 693 real/930
SDK presents. PNG sidecars are usable and retain their paired PPM files.

## Mod isolation failed

The installed standard mod was excluded, but the staged copy was not discovered.
Logs list only game/creative/survival; diagnostic worldReady remains false and the
ordinary mod's FPS HUD/callbacks are absent. Copied clientsettings stores ModPaths
as Mods and the original absolute user-data Mods directory. --dataPath alone does
not add the new isolated Mods directory when those paths were persisted.

The staged modinfo/DLL exist in run/data/Mods/vulkanstory. Official ClientProgramArgs
supports --addModPath; next implementation must explicitly add run/data/Mods and
require its world-ready lifecycle evidence before accepting headless mod integration.
Artifact-count success is not mod success. No source fix or rerun this turn.

No installed changes, commands/transitions, broad input/platform/coexistence/release
acceptance. Mod isolation milestone stays open despite completed renderer captures.

## Mod search/lifecycle source correction — 2026-10-01

Implementation only. The launcher now supplies --addModPath for run/data/Mods,
explicitly adding the isolated staged module despite copied absolute ModPaths.
Normal mod-loader discovery remains in use; installed-folder exclusion is unchanged.

Completion now requires both the ordinary module's queued world-ready callback and
its loaded assembly location matching the isolated staged DLL. Result JSON records
both, so artifact-count success cannot hide a missing ModSystem. Multiple locations
or an installed copy do not satisfy staged identity. Existing renderer capture,
visibility and normal shutdown remain intact.

No build, tests, probes, snapshot, staging or game run. Source correction remains
unbuilt; the preceding mod-isolation failure is not marked fixed at runtime.
Installed user game/settings/process untouched.
