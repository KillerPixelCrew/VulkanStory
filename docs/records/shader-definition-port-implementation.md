# Game-independent shader definitions

Date: 2026-09-30. Implementation only; no builds, tests, probes, packages or game runs.

`ShaderStageDefinition`/`ShaderProgramDefinition` carry source, prefix, stage token, pass name and include-set data without game object references. `GameShaderDefinitions` owns a conditional weak table that preserves each original stage's identity during source refresh and reload. Identical text from distinct game stage objects remains distinct. Include names are captured from the original program as a data snapshot so the retained translator's frame-uniform classification stays available.

The staged `VulkanDevice.Programs` facade now accepts these definitions. Its existing source-size/NUL checks, staging, native-manifest/rewriter fallback, linking and uniform logic remain in place. The main device's staging dictionary and stage tag likewise use neutral types. Game `IShader`, `IShaderProgram` and the `ShaderProgramBase` include access move entirely to game integration for this program boundary. Other full-device game/provider dependencies remain to be ported.

Five source fixtures check same-object reload identity/text refresh, separate identities for equal source, and original vertex/fragment/geometry/compute tokens. Integer inline data avoids the already-diagnosed game-enum discovery problem. Supported tokens remain the original GL/cache values; tessellation routing is not added.

This increment is unvalidated. The next bounded batch should run game tests and build/check the official profile tool. The complete device facade is still excluded, so this cannot establish its revised method calls, real shader reload, source/include overrides, native-manifest selection or game rendering. Actual shader capture/compile/link Harmony calls, the full renderer facade and vendor/menu/world integration remain open.

The [first bounded validation](validation-g1-shader-definition-2026-09-30-01.md) passed all 27 game cases, a clean profile build and official bindings. Identity/text/token handling now has evidence; include-set execution, staged facade compilation and real game shader routing remain open.
