# Current Windows client candidate — 2026-10-01

One bounded build/syntax/stage/archive batch completed once. Artifacts:
`artifacts/validation/client-candidate-20261001-202409/`.

Bootstrap, Game/full dependencies, Mod and Input.Companion compiled with zero
warnings/errors. PowerShell parser reported no syntax errors in stage-runtime,
package-runtime, deploy-runtime and headless-capture. Stage and archive scripts
completed using the retained matching native bundle/shader corpus. The native
Streamline bridge is the previously compiled corrected version, not rebuilt here.

## Deliverables

- `archives/VulkanStory-win-x64.zip`: 142,821,486 bytes, 374 entries.
- `archives/VulkanStory-Input-Companion.zip`: 26,061 bytes.
- Archive hashes are preserved in archives/archives.json.

The client ZIP inventory/entries include the current Game integration, both
ordinary mods, player README, ownership-aware updater, pinned official game
profile, full native/shader payload and PromptFont license/attribution/provenance.
The font/metadata are embedded in Game. Package acceptance is explicitly
`unverified`. This supersedes the older client archive as the current development
candidate; it is not a release-acceptance claim.

The new disabled-mod selection/headless failure boundaries and runtime identity
changes compile. Update adoption and actual disablement/failure behavior are not
established by a syntax check or archive creation. The preceding successful
hidden runtime batch predates these changes.

No tests, installed deployment, updater execution, game launch, source repair
or second batch. Installed payload remains older. Full renderer/provider/SDL
parity, glyph/custom-pass/AVI/analog execution, install/update/remove acceptance,
MFG coexistence and inherited platform/hardware gaps remain open.
