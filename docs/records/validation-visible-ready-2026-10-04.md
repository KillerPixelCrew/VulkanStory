# Visible checkpoint prepared — 2026-10-04

DIRECT validation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
One bounded Game/Mod build, fresh stage and PowerShell syntax batch ran once.
All passed. Game: one existing CS8600 harness warning, zero errors. Mod: zero
warnings/errors. Both harness/verifier scripts parsed without errors.

Artifacts: [visible-ready-20261004-005137](../../artifacts/validation/visible-ready-20261004-005137).
prepare.ps1 ends after staging; no client was launched. Current managed outputs
and October-4 matching FSR3/Streamline bridges were staged with October-1 other
native/notices/full shaders. Existing installed game/settings/save were unchanged.
No tests, native build, deployment or second batch ran.

| Staged identity | SHA256 |
| --- | --- |
| VulkanStory.Game.dll | `B3B6A390E8CB76C32EBCFD542F58713388E51F8FC3C438BC73DA682940F25CE7` |
| VulkanStory.Mod.dll | `6320C2EEB6260F3D6682147123A199E8F4A4BDBE25CF2F603A3A8F93D19B4446` |
| VulkanStory/package.json | `8912EDB2C61FDDDEFD3570A45AE3D36D6574976BABE4EEEB52B550648F52E11F` |

## Prepared launch commands, not executed

- launch-visible-gain.ps1: one isolated visible DLSS SR/FG world child, unchanged
  strict gain scenario, one capture at tick 200, automatic completion close,
  300-second deadline. Script SHA256:
  `C124CA0686BC450352F633C907957909DC79E577222FC2AE669EE21847260347`.
- launch-visible-options.ps1: one isolated visible world child with SR/FG off,
  one capture then KeepOpen for manual pause/main-menu Options inspection,
  600-second deadline. Normal user close ends it; deadline termination fails
  validation and targets only the owned child. Script SHA256:
  `447B6AF3990B91A70F79A9A2BC8A6641F740B69676F70662D0A9FB288C2C72B8`.

Both use the exact stage above, distinct fresh output directories and independent
foggy-village snapshots. They do not deploy or control an existing game process.
The scripts are reviewable launch artifacts; neither has run. Script syntax
checks in this batch cover the harness/verifier, not execution of these launchers.

Visible launch still needs the user's instruction under docs/ROADMAP.md:
"Visible launches/deployment require the user's instruction." Approval has not
been inferred from automatic goal continuations. Actual SDL visibility, focused
input, DLSS gain, interpolated image/pacing quality and both Options hosts remain
unverified. Full goal remains active.
