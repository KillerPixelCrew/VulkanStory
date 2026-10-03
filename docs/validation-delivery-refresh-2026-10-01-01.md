# Delivery refresh with removal tool — 2026-10-01

One bounded validation batch: Game/dependency Release build, PowerShell parsing of
remove-runtime/stage-runtime/package-runtime, fresh stage and client/companion ZIP.
No tests, installation, removal preview/execution, game launch or source repair.
Artifacts: artifacts/validation/delivery-refresh-20261001-223159.

Game build: zero warnings/errors. All three scripts parse without errors. Staging
and archives passed payload SHA256 checks. Client contains375 entries; packaged
VulkanStory/tools/remove-runtime.ps1 matches the source SHA256. This verifies
syntax and delivery, not removal/rollback behavior.

Client ZIP: archives/VulkanStory-win-x64.zip,142832071bytes.
SHA256:4FCD606A690A7C0B8421FEE9E7C084E2089D7B0EE494B36DE248B653F2C50614.
Companion ZIP: archives/VulkanStory-Input-Companion.zip,26061bytes.
SHA256:9AD25FFA601593D3129DCE991A91DC65CEB17B811E6C447008AE4983512D417C.
Result/logs/syntax output and archive hashes are retained in the batch directory.

Supersedes client-refresh-20261001-221545 as the current development package.
Includes the compiled handheld shadow prefix lifecycle, packaged removal tooling
and revised player instructions alongside prior renderer/provider/SDL integration.
Unchanged Bootstrap/Mod/companion binaries retain their earlier production-build
evidence. Native bundle and GLSL compiled corpus were reused, not rebuilt here.

Package acceptance remains unverified. Current shadow toggle and controller UI
behavior, installation/update/remove/recovery, MFG coexistence, broad visual and
provider parity, sky artifact and hardware/platform coverage remain open. No files
in the installed game, user settings or original save were modified.