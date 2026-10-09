# Production compile batch — 2026-10-01

Validation turn: one bounded batch, four production projects, no reruns.
No tests, native bridge builds, shader compilation, staging, installation or game
launch occurred. No implementation source was changed in this turn.

Commands used `dotnet build <project> -c Release --nologo` with
`VintageStoryPath=C:\Users\N1GHT\AppData\Roaming\Vintagestory`.

| Project | Exit | Complete reported result |
| --- | --- | --- |
| VulkanStory.Bootstrap | 0 | Build succeeded; 0 errors, 0 warnings |
| VulkanStory.Game | 1 | Build stopped in SDL dependency; 2 errors, 0 warnings |
| VulkanStory.Mod | 1 | 1 error and 1 warning |
| VulkanStory.Input.Companion | 0 | Build succeeded, including shared Input; 0 errors, 0 warnings |

Raw logs and machine-readable results:
`artifacts/validation/production-compile-20261001-030856/`.
All four logs were read in full. Repeated diagnostics in each build summary are
the same errors, not additional failures.

## Diagnosis from existing logs/source

SDL `SdlNativeLibrary.cs:57` reports CS0117 for both `DllLoadDir` and
`DefaultDirectories`. These names are native Windows concepts, not members of
the managed `DllImportSearchPath` enum. The same source names occur in renderer
`Core/NativeRuntimePaths.cs:75`, downstream of the failed SDL build. The installed
.NET 10 reference documentation supplies the managed search flags instead.
Both call sites require correction in an implementation turn.

Mod `VulkanStoryModSystem.cs:48` reports CS0104: `Func<string, string?>` is
ambiguous between System.Func and the game's API delegate. The runtime bridge
uses the framework delegate, so the type must be qualified. The remaining
CS0618 warning is the legacy client RegisterCommand API; the game's recommended
replacement is its ChatCommand sub-API.

The Game/renderer source added after the earlier verified checkpoint has not
been fully compiled by this batch: dependency compilation stopped first. No
runtime operation, Harmony installation, rendered frame or vendor execution
is established by either successful project compilation. Milestones stay open.

Next action: fix the concrete source issues in a later implementation turn.

## Following implementation correction — 2026-10-01

The SDL and renderer loaders now use the managed enum members
UseDllDirectoryForDependencies and SafeDirectories, matching the installed .NET
10 reference documentation and preserving the intended sibling dependency
lookup. Framework Func references are explicitly System.Func in Game/Mod files
that import the conflicting Common API namespace, including the client bridge
and main-menu settings screen. No builds or tests ran in this implementation turn.
The table above remains the complete recorded compile result; the fixes are
unverified and downstream Game compilation remains open. The RegisterCommand
deprecation warning is unchanged.
