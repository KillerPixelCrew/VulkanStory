# Original-game shader routing subset

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

Four retained operations now route through `GameGraphicsAdapter.Shaders`:
`CompileShader`, `CreateShaderProgram`, `GetUniformLocation`, `UseShaderProgram`.
`GameShaderDefinitions` maintains renderer stage identity and captures program
source/includes. Compile preserves conditional `HANDHELDSHADOWS` injection;
link assigns the renderer's program ID only on success and reports failure/load
through explicit `GameShaderCallbacks`. Session attachment must supply settings
and logging callbacks. Program selection lives in mod-owned `StatedProgram`,
ready for the retained generic draw bridge; no draw currently consumes it.

Baseline revision: `386e0d05386d0b228b439d09aeca851428f7bbf3`,
`Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Shaders.cs`.
Callbacks, neutral definition conversion and mod-owned state replace inherited
platform/injected config access. Algorithms/source selection/link IDs remain
retained; preserve inherited provenance/license scope.

`ShaderConsumerPatches` pins four typed signatures with its own Harmony owner,
dormant original pass-through, checked renderer association and owner-specific
removal. Like the texture group, this incomplete subset cannot satisfy the
mandatory complete `graphics-api` group. No live session attaches the adapter.

An unrun fixture resolves/installs the four targets, calls an active program
selection with no adapter to prove rejection before original GL, and checks
prefix removal. It cannot establish successful shader linking or draw pixels.

Next validation: one bounded Release game-test/profile-build batch. Compilation,
official signatures and actual installation remain unverified. Uniform setters,
UBOs/samplers, program disposal and native draw integration, complete startup
transaction, menu/world shaders, all providers/SDL controls/release remain open.

The [first bounded validation](validation-g1-shader-routing-2026-09-30-01.md)
compiled but failed the shader fixture's fourth target: `UseShaderProgram` is
an injected old platform boundary absent from official 1.22.7. Twenty-nine
regressions passed; no shader prefix installed and the profile build was skipped.
Repair requires actual official shader Use/Stop call-site integration in a later
implementation turn; program selection remains required.

## Official Use/Stop repair — 2026-09-30

The missing injected platform target is replaced in source by transpilers on
the original `ShaderProgramBase.Use()` and `Stop()`. The compile/link/location
prefixes remain on their three real platform methods. Each selection target
requires exactly one `OpenTK.Graphics.OpenGL.GL.UseProgram(int)` call in original
IL, and the transpiler applies the same check to incoming Harmony instructions.
It replaces only that static int-to-void call, preserving labels/exception blocks
and the surrounding original program tracking, guards and cleanup. Dormant
dispatch calls original GL; active dispatch resolves the existing platform's
mod-owned graphics adapter and updates `StatedProgram`.

The game project references official `OpenTK.Graphics` with copying disabled.
The fixture now installs both real selection transpilers, checks their owner
metadata/removal, and exercises missing-owner rejection via the real uniform
lookup prefix. This repair has not compiled or run. Other raw GL calls in shader
Use/Stop (uniforms, textures/samplers and UBOs) still require their own routes;
the selection repair alone cannot support live activation or successful shader Use.
Next validation is one bounded game-test/profile-build batch. No validation
command ran during this implementation turn; the previous failure stays recorded.

The [repair validation](validation-g1-shader-routing-2026-09-30-02.md) stopped
at compilation: the new wrapper needs the `Vintagestory.Client.ScreenManager`
namespace. No tests ran and profile build was skipped. Make the namespace fix
in a later implementation turn; official Use/Stop IL/patch acceptance stays open.

## ScreenManager namespace repair — 2026-09-30

The selection wrapper now explicitly names `Vintagestory.Client.ScreenManager`,
matching the original source namespace. Dispatch, original/incoming call-count
guards, dormant behavior and the three platform prefixes are unchanged.
Source-only repair; no builds/tests/probes ran. Next validation is one bounded
game-test/profile-build batch. Earlier failures remain recorded until the
repaired official selection group compiles and installs successfully.

The [third bounded batch](validation-g1-shader-routing-2026-09-30-03.md) passed
30 game cases and a clean profile build. Three platform signatures and both
official Use/Stop selection anchors matched; actual transpiler install/remove
and missing-adapter uniform lookup passed. Both recorded failures are resolved.
Successful shader runtime and remaining raw GL routes remain unverified.
