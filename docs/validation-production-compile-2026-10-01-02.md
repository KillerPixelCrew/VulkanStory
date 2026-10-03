# Production compile batch — 2026-10-01, second batch

Validation turn after the source corrections from the preceding implementation
turn. One bounded batch: Game and Mod only. No reruns, tests, native builds,
shader builds, packaging, installation or game launch.

Commands: `dotnet build <project> -c Release --nologo` with official
`VintageStoryPath=C:\Users\N1GHT\AppData\Roaming\Vintagestory`.

| Project | Exit | Recorded result |
| --- | --- | --- |
| Game | 1 | 26 errors, 0 warnings; Input, Contracts, SDL and Vulkan backend built successfully before Game compilation failed |
| Mod | 0 | 0 errors, 1 warning: deprecated RegisterCommand API |

Complete logs and result JSON are in
`artifacts/validation/production-compile-20261001-031137/`. Duplicate diagnostics
in the build summary represent the same 26 errors. All unique diagnostics and
both complete build summaries were inspected; no source changes were made.

## Diagnosis from logs and source

- 21 errors concern NativePipeline, NativeUniform, NativeTexture and
  NativeSamplerSlot. Their declarations in Core/NativeRenderDeclarations.cs
  actually use namespace VulkanStory.Render.Vulkan, whereas integration partials
  import only Core or explicitly qualify these types under Core. Fix adapter
  imports/qualification while retaining the existing renderer declarations.
- Two BitmapRef errors in QueriesCaptureConsumerPatches and two EnumMouseButton
  errors in the controller host/interface are missing Common API imports. The
  original snapshot places both types under Vintagestory.API.Common.
- SkyConsumerPatches directly names the internal SystemRenderSkyColor in its
  draw helper and target lookup. The official assembly's visibility must be
  preserved. Resolve its patch target by reflected type identity and accept a
  public ClientSystem/object boundary instead of naming the internal class.

The prior invalid DLL enum names and client-mod Func ambiguity no longer block
compilation. This establishes compilation only for the dependency assemblies and
ordinary client mod, not completed Game integration or runtime operation.
Bootstrap and Input.Companion results remain those of the preceding batch.
No native provider exports, enabled SDK evaluation, SDL game startup, visible
menus/worlds or player package have been accepted.

Next action: fix these concrete Game source issues in an implementation turn.

## Following source correction — 2026-10-01

Seven graphics adapter partials now import VulkanStory.Render.Vulkan, and AO's
explicit declarations name that namespace instead of Core. Controller host and
query/capture files import the Common API for EnumMouseButton and BitmapRef.
Sky's target is resolved by its exact type name from the official ClientMain
assembly. Its helper accepts ClientSystem; profile validation checks the reflected
class still derives from ClientSystem. The original field/draw guards and sky
rendering behavior are retained. The official game class remains internal.

These source edits address all three reported error groups but have not been
compiled. No build/tests/probes/packages/game runs occurred during the following
implementation turn. The batch table remains the authoritative recorded result.
