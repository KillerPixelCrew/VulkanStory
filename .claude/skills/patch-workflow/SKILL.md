---
name: patch-workflow
description: How to change game-lib, API-fork, mod-fork and shader code in Optimum so it actually ships - edit the right tree, regenerate patches, list Cecil targets, wire csproj overlays, run the checks. Use before editing anything under build/, VintagestoryApi/, VSEssentials/, VSSurvivalMod/, sources/shaders/.
---

# Patch workflow

1. Edit the source of truth (see CLAUDE.md table): `build/VintagestoryLib/**` for the client lib,
   `VintagestoryApi/**` for the API, the mod fork dirs, `sources/shaders/` for shaders.
   Never edit `patches/*.patch` or `sources/VintagestoryApi/**`.
2. New API file: add `<Compile Include="..\sources\VintagestoryApi\<path>" Link="<path>" />` to
   `optimum-api-contracts/optimum-api-contracts.csproj`, and `<Compile Remove="<path>" />` to
   `VintagestoryApi/VintagestoryAPI.csproj`, `sources/VintagestoryApi/VintagestoryAPI.csproj` and
   `.baseline/VintagestoryApi/VintagestoryAPI.csproj` (mirrors what bootstrap folds in).
3. New platform graphics member: inject a virtual with a neutral body on `ClientPlatformAbstract`
   (member list in `Optimum.Patcher/Program.cs`), put the OpenGL body in a `ClientPlatformWindows`
   override and the Vulkan body in the matching `Optimum.Render.Vulkan/Platform/VulkanClientPlatform.*.cs`
   partial, and add it to `VulkanClientPlatform.ExpectedVirtuals`. Lib call sites call the platform
   virtual; nothing in the lib names the renderer. Forked mods reach the device only through
   `OptimumForkGraphics` (contracts).
4. Lib change: every changed or added method/property/field in `ClientMain`, `ClientPlatformWindows`,
   `ChunkRenderer`, `ShaderRegistry`, `ShaderProgram*`, `ScreenManager`, ... goes into
   `Optimum.Patcher/Program.cs` (transplant tuple `new("Type", "Method", paramCount)`; injected
   members in the per-type member lists). The patcher only checks references, not omissions, so
   grep your diff for every signature.
5. Mod-fork change: rebuild ships it locally; the installed-runtime path needs the
   `Optimum.Patcher/mod-patcher.cs` manifest entry for the type/member.
6. New shader include: `sources/shaderincludes/` + add the copy to `make deploy` and every
   `scripts/package-*` script; the Vulkan test corpus (`ShaderCorpus.cs`) must overlay it too.
7. `bash scripts/extract-patches.sh` then `bash scripts/check-patches.sh` (expect 0 conflicts, 0 pending;
   a stray `patches/VintagestoryApi/*.csproj.patch` means step 2's baseline line is missing).
8. `dotnet build VintageStory.slnx -c Release`, both test suites, `make deploy`, run the game.
9. If a build of the lib fails on a member missing from the API, the fork and `sources/` have drifted:
   diff `VintagestoryApi/` against `sources/VintagestoryApi/` and fix the fork, then extract.
