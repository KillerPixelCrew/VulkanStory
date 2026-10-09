# SR release compilation — 2026-10-04

DIRECT validation turn in D:/Coding/VulkanStory-Rewrite (no Git repository).
One bounded batch built Game/Mod Release and the FSR3 bridge once. All exited 0.
Game/dependencies: zero errors, one existing CS8600 harness warning. Mod: clean.
FSR3: eight native warnings (seven function-pointer casts, one enum conditional).

Artifacts: [sr-release-20261004-005553](../../artifacts/validation/sr-release-20261004-005553).
Complete logs and results.json are retained. Official game references use the
existing local props; FSR3 headers came from D:/Coding/VulkanStory/_ref/fsr-vulkan,
Vulkan headers from C:/VulkanSDK/1.4.357.0. No donor binaries/assemblies copied.

| Output | SHA256 |
| --- | --- |
| Game Release DLL | `01C7DBCCCCEB9CF11D767DCE257D314F952F72170259DFF8B9896B98E6C4DA59` |
| Render.Vulkan Release DLL | `521DBED11D0F82D2E8AA129107BB9FEE954A16A2DE7E205B1C98CEA7BE53FE7C` |
| Batch bridges/VulkanStoryFsr3.dll | `37B55D065D183843BF91518D1398702E363C2A62E81EF42059B5739F33347034` |

This compiles [SR release retention](sr-release-retention-2026-10-04.md), including
the matching native FSR3 retention behavior. No tests, failure injection, GPU/game
run, package stage, deployment or second batch occurred. Compilation does not
certify native failure semantics, provider switching or visual/FG behavior.
Visible approval remains pending. Prepared visible launchers/stage remain
unchanged and predate this repair. Installed game/settings/save unchanged.
SDK-03, DLSS output gain, Options interaction and the full goal remain open.
