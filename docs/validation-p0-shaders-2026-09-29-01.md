# P0 shader validation — first batch, 2026-09-29

Status: **compile failed before tests or native shader building ran.** This was one bounded validation batch. No source was changed and no phase was rerun.

The planned batch from `D:\Coding\VulkanStory-Rewrite` was Release backend tests, Release build of `tools/VulkanStory.Shaders.Compiler`, then a 60-second `--build` of a fixture containing the real `shaders/native/ui-compose.glsl` and its include directory. `--build` was selected because the retained `--single` command requires an existing manifest. The batch stopped at the first failed phase. The complete [test console log](../artifacts/validation/p0-shaders-20260929-01/test.log) and [summary](../artifacts/validation/p0-shaders-20260929-01/summary.json) are retained; compilation stopped before a TRX or fixture was produced.

| Gate | Recorded result |
| --- | --- |
| Backend `dotnet test` | Exit 1 during compilation. `ProgramInterfaceLayout.cs:50` reports CS0246: `TextureKind` was not found. Zero tests executed. |
| Shader compiler tool build | Skipped after compile failure. |
| Timed native `ui-compose` build | Skipped after compile failure; no shaderc/native corpus evidence from this batch. |

`TextureKind` and `BindlessKinds.TryFromGlslType` are defined in the still-excluded `Core/BindlessTextureTable.cs`, while `ProgramInterfaceLayout` uses both to classify sampler declarations. The next implementation turn should bring the bindless kind declaration and lookup into the compiled backend as a coherent dependency, or include the bindless group if its other dependencies are ready. A new bounded validation is required after that source change.
