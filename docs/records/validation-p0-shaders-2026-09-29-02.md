# P0 shader and bindless validation — second batch, 2026-09-29

Status: **focused backend tests, shader tool, one native shader, and bindless SDL presentation passed.** This was one bounded validation batch after including the retained bindless table and shared layout. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: Release backend tests; Release build of `tools/VulkanStory.Shaders.Compiler`; `--build` of a fixture containing the real `ui-compose.glsl` and its copied `include/` directory, under a 60-second timeout; Release preflight build; then `--sdl-present` under a 30-second timeout. The [summary](../../artifacts/validation/p0-shaders-20260929-02/summary.json), [test log](../../artifacts/validation/p0-shaders-20260929-02/test.log), [TRX](../../artifacts/validation/p0-shaders-20260929-02/backend.trx), [shader-tool build log](../../artifacts/validation/p0-shaders-20260929-02/build.log), [shader stdout](../../artifacts/validation/p0-shaders-20260929-02/shader.stdout.log), [shader stderr](../../artifacts/validation/p0-shaders-20260929-02/shader.stderr.log), [preflight build log](../../artifacts/validation/p0-shaders-20260929-02/preflight-build.log), [preflight stdout](../../artifacts/validation/p0-shaders-20260929-02/present.stdout.log), and [preflight stderr](../../artifacts/validation/p0-shaders-20260929-02/present.stderr.log) are retained.

| Gate | Recorded result |
| --- | --- |
| Backend tests | Built in Release; TRX records 32/32 executed and passed, zero failed or skipped. Includes the shaderc rewrite/cache cases and two bindless slot-lifetime cases. Compilation emitted one CS8619 nullable warning at `NativeShaderBuilder.cs:106`. |
| Shader compiler tool build | Exit 0, zero warnings/errors in this incremental build. |
| `ui-compose` native build | Exit 0 without timeout or stderr. Built one program, one variant, and two SPIR-V files (`ui-compose.vert.spv`, `ui-compose.frag.spv`) plus `shaders.manifest.json` in [output](../../artifacts/validation/p0-shaders-20260929-02/output/shaders-vk/shaders.manifest.json). |
| Preflight tool build | Exit 0, zero warnings/errors in this incremental build. |
| `--sdl-present` | Exit 0 without timeout or stderr. The hidden SDL3 Vulkan path constructed the bindless table and shared pipeline layout, submitted the render-target clear/blit frame, presented, and released resources. |

The CS8619 warning is from `Directory.GetFiles(...).Select(Path.GetFileNameWithoutExtension).ToList()` yielding `List<string?>` in the analyzer while `DiscoverPrograms` returns `List<string>`. Its source can be made explicit in a later implementation turn; the current batch passed. The single native program is representative, not a full corpus check. This batch does not prove a shader pipeline draw, bindless descriptor sampling by a shader, visible pixels, game menu/world integration, provider shaders, or packaging.
