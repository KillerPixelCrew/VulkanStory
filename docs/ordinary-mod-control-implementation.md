# Ordinary mod control and world lifecycle

Updated 2026-10-01. Implementation only; no builds, tests, probes, packages or
game runs. Tests remain deferred until integration is finished.

The early runtime exports framework delegates/JSON through AppContext for settings
read/apply/reload and world-ready/leave signals. The ordinary mod keeps only API
references; it does not load another game integration, renderer or native session.

- Applying settings validates/normalizes JSON and saves it through the existing
  atomic settings store. Live settings updates queue to the owner-thread boundary
  before frame pacing/input, avoiding provider changes inside a draw.
- Reload reads the selected game's data-path settings and queues the same owned
  update. Startup/device choices retain their restart requirement.
- Level-finalized and leave-world events queue signals with original world identity.
  They reset temporal/controller readiness and retire FG/TAA/scene publication on
  leave without invalidating a newer attached world.
- `.vulkanstory status` reports the shared runtime status; `.vulkanstory reload`
  reloads settings or explains that the early runtime is unavailable.
- Ordinary mod disposal unsubscribes lifecycle events. Runtime shutdown clears
  queued controls and removes exported callbacks after owned teardown.

The bridge and commands are connected in source. The full renderer settings dialog
is still unfinished. Saved/live settings, custom data paths, world rejoin/leave and
provider switching remain unverified. Native/runtime packaging and full live port
acceptance remain open.

## Client command lifetime source increment — 2026-10-01

Replaced deprecated RegisterCommand with the original public ChatCommands API,
using its optional word parser and TextCommandResult responses. Settings remains
the default action; status and reload retain their runtime bridge behavior.
Unavailable runtime/dialog, failed reload and unknown action now return command
errors. The current command handler uses a weak owner reference because the
public API has no unregister operation. Disposal clears client/UI/event ownership;
the old registry entry cannot retain the mod instance or overwrite a newly
attached owner's handler during disposal.

Implementation only: no build, test, probe, package or game run. The previous
successful Mod build with its legacy API warning remains the recorded evidence;
these command changes have not been compiled or exercised. Matching Streamline
release SDK input is still pending independently of this client mod work.
