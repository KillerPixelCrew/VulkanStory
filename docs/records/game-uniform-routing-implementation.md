# Scalar/vector shader uniform call-site port

Date: 2026-09-30. Implementation only; no build/test/probe/package/game run.

Ten official `ShaderProgramBase.Uniform` overloads now join the shader subset:
float/int scalars, Vec2f/Vec2i, two floats, Vec3f/Vec3i, three/four floats and
Vec4f. Each transpiler replaces exactly one matching GL scalar/vector setter,
with the same static stack signature. Original `CheckShaderIsActive`, dictionary
lookup, Vec2i float casts and Vec3i integer components remain in the original
method body. Labels/exception blocks remain attached to the replaced call.

Active wrappers resolve the existing platform adapter and current shader's
program ID, then forward to retained `VulkanDevice.SetUniform` overloads.
Dormant wrappers invoke their original GL setters. Reflection/overload discovery
occurs at patch preparation/transpilation, not uniform dispatch. Original and
incoming Harmony instructions both require exactly one expected call.

Source provenance: baseline revision `386e0d05386d0b228b439d09aeca851428f7bbf3`,
original `ShaderProgramBase` scalar/vector bodies and retained
`VulkanClientPlatform.Shaders` forwarding. This is a game-boundary adapter;
uniform storage/layout algorithms remain in the existing renderer. Preserve
inherited source notices/license scope.

The existing group fixture now validates/installs all ten overloads along with
three platform prefixes and Use/Stop routes, and checks scalar patch ownership
and removal. It has not run after this increment. It does not exercise a
successful uniform write, active-shader guard, actual current-program lifetime
or shader draw pixel. No live session activates this incomplete graphics subset.

Next validation: one bounded Release game-test/profile-build batch, checking
compilation, official call anchors and real Harmony installation/removal.
Array/matrix uniforms, texture/sampler binding, UBO/disposal, complete graphics
and startup, real menu/world frames, vendor features, SDL controls and release
remain open. No feature acceptance follows from source-only transpiler creation.

The [first bounded validation](validation-g1-uniform-routing-2026-09-30-01.md)
passed 30 game cases and a clean profile build. All ten overloads matched original
and incoming call anchors and installed via Harmony. Scalar owner/removal checks
passed; successful uniform writes and native shader pixels remain unverified.
