# Headless startup isolation follow-up — 2026-10-01

Implementation only; no builds, tests, probes, staging or game launch.
The previous goal turn made source progress on ordinary mod-manager disablement
and first-extraction update handling.

Review of the new disabled-mod branch found that copied user settings could
disable VulkanStory in the harness. SelectWindowServices would then roll back
and allow the original visible window. The runner now removes only this product's
two mod IDs/version entries from the isolated disabledMods list. Other disabled
mods remain disabled and no original settings are written.

SelectWindowServices independently refuses visible fallback when an unexpected
disabled renderer is encountered under headless mode. The startup-window prefix
also excludes headless mode from its normal preparation-error fallback catch.
Thus a failed SDL/Vulkan preparation cannot return true from that catch and
allow original window creation. Normal client fallback behavior is preserved.

These failure/disable branches are source-only. The earlier passing run supports
successful startup/capture, not these new failure paths. Full renderer, provider,
controller and release acceptance remains open.
