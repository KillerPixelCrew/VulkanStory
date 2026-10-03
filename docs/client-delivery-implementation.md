# Client drop-in and disablement — 2026-10-01

Implementation only: no builds, tests, staging, packages, deployment or game run.
The preceding turn made progress with a successful isolated integration batch.

## Disablement

Source inspection found SelectWindowServices checked only the renderer JSON's
Enabled field. It now checks ClientSettings.DisabledMods at the first window
request, after the game's normal settings/data-path initialization. The same
bare mod ID and exact id@version keys as original ModLoader are honored.
Identity comes from the actual client modinfo embedded in Game, avoiding assembly
version formatting and duplicated version constants. If disabled, startup rolls
back routing before creating an SDL window/device and resumes original startup.
Renderer settings are not loaded on this branch. Disabling during an active
session takes effect at restart; no live device ownership switch is attempted.

## Player delivery

Staging now includes a concise client README and the existing ownership-aware
updater under VulkanStory/tools. The README covers extraction beside the original
executable, unchanged shortcut, integrated/optional remote input companion,
settings commands, mod-manager disablement, loader.ini recovery, removal scope,
actual bootstrap log path and remaining MFG coexistence acceptance.

A first ZIP extraction has package.json but no install.json receipt. The updater
previously rejected these payload files as unowned, breaking the documented
update path. It now adopts the existing package inventory only after checking
product/profile, required core entries, permitted client paths and every listed
client file's hash. Loader.ini edits are preserved. The inventory itself is
included in ownership, then the normal external backup/write/receipt transaction
is used. Foreign/changed payloads still fail; nothing is deleted automatically.

## Status

Source changes are unbuilt/unexecuted. Disabled launch, ZIP adoption/update and
removal still need bounded acceptance evidence. Existing archive and installed
payload remain older. This completes source wiring for these Windows delivery
boundaries, not release acceptance or the full renderer/SDL/provider objective.
