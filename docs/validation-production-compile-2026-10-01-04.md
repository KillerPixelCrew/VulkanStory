# Production compile batch — 2026-10-01, fourth batch

Validation turn after the preceding boundary corrections. One Game build only;
no reruns, source fixes, tests, native/shader builds, packages, installations or
game launches occurred.

Command: `dotnet build src/VulkanStory.Game/VulkanStory.Game.csproj -c Release
--nologo`, with official
`VintageStoryPath=C:\Users\N1GHT\AppData\Roaming\Vintagestory`.

**Result: exit 0, 0 errors, 0 warnings.** The complete log was inspected.

The build produced Contracts, Input, Platform.Sdl, Render.Vulkan and Game. This
is the first successful compilation of the complete current Game integration,
including scene/post/motion routing, session-owned providers, SDL/controller
integration, settings and the composed version profile.

Complete log and result JSON:
`artifacts/validation/production-compile-20261001-031815/`.

The preceding batches compiled Bootstrap, ordinary Mod and Input.Companion;
Mod still had one legacy RegisterCommand deprecation warning. Those projects
were not rebuilt here. No earlier test count changes.

Compilation does not execute the profile's reflection/IL guards or establish
Harmony installation, SDL startup, menu/world frames, native provider exports,
enabled SDK operation, or complete licensed payload delivery. All such acceptance
gates remain open. Next work is native bridges/shaders/runtime payload production
and startup integration, without adding test suites.
