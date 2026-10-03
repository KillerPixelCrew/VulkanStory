# Candidate deployment and startup — 2026-10-01

One bounded deployment/launch batch, run once. No builds, tests, source fixes,
repackages, reruns or deletion occurred.

| Step | Recorded result |
| --- | --- |
| Owned update | Passed; full candidate installed into the official existing game directory, with backup/receipt |
| Launch | Original Vintagestory.exe launched directly from its original directory; no alternate launcher or copied installation |
| Window | PID 10724 opened a Vintage Story window, handle 5048382 |
| Shutdown | CloseMainWindow requested; process exited normally, code 0 |
| Early native/managed load | Proxy forwarded hostfxr 10.0.12; exact 1.22.7 profile accepted; observation patches installed |
| Full renderer activation | Failed during profile construction: missing mechanical renderer type |

Batch files, deployment backup and complete captured trace/stdout/stderr:
`artifacts/validation/runtime-startup-20261001-075052/`.
Installed ownership receipt: official game directory's `VulkanStory/install.json`.
The old B0 receipt and unchanged proxy remain; no cleanup/removal was attempted.
Loader configuration was preserved. The target had no version.dll, so this does
not prove MFG proxy coexistence. No game assemblies were replaced.

## Failure diagnosis

Bootstrap trace reports:

`TypeLoadException: Could not resolve type Vintagestory.GameContent.Mechanics.AngledGearBlockRenderer in VSSurvivalMod`.

InstanceMotionConsumerPatches profiles that singular spelling. The authoritative
original snapshot file AngledGearBlockRenderer.cs actually declares the public
class **AngledGearsBlockRenderer**, plural, in the expected Mechanics namespace.
The filename is not its CLR type identity. Correct the profile spelling in a
following implementation turn; retain the gear's instance/motion coverage.

Bootstrap marked activation bypassed; stdout confirms NVIDIA OpenGL 4.3/GLSL 4.30
on the RTX 4070 Laptop GPU. The sampled window and clean exit establish vanilla
startup/fallback only, not SDL/Vulkan rendering. The executable was launched
directly; this batch did not click the desktop shortcut itself.

The installed candidate remains present and will bypass activation on this
profile error until corrected. Full routing, SDL/Vulkan menu/world frames and
all provider evaluations remain unverified. No source repair or second launch
occurred in this validation turn.

## Following source correction — 2026-10-01

Corrected AngledGearsBlockRenderer's plural CLR name in the instance-motion
profile. Inspected the other six profiled mechanical class declarations; their
names match the original source. Allocation/stride counts, device scopes and
motion writers remain unchanged. Method lookup failures now identify the exact
class/member and match count instead of a generic LINQ exception. Bootstrap
bypass traces retain the exception's full inner/stack detail; the entry text no
longer calls the full runtime an observation-only prototype.

Implementation only: no builds, tests, repackages, deployment or game runs.
The installed candidate still contains the failed build until a subsequent batch
compiles and deploys these changes. Activation remains unverified.
