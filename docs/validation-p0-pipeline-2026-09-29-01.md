# P0 graphics pipeline validation — first batch, 2026-09-29

Status: **focused backend tests, preflight build, and one hidden shader draw/present passed.** This was one bounded validation batch after including program resources and the pipeline/cache group. No source was changed and no command was rerun.

The batch ran from `D:\Coding\VulkanStory-Rewrite`: Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, Release build of `tools/VulkanStory.Preflight`, then the built tool with `--sdl-draw` in a process limited to 30 seconds. The [summary](../artifacts/validation/p0-pipeline-20260929-01/summary.json), [test log](../artifacts/validation/p0-pipeline-20260929-01/test.log), [TRX](../artifacts/validation/p0-pipeline-20260929-01/backend.trx), [build log](../artifacts/validation/p0-pipeline-20260929-01/build.log), [draw stdout](../artifacts/validation/p0-pipeline-20260929-01/draw.stdout.log), and [draw stderr](../artifacts/validation/p0-pipeline-20260929-01/draw.stderr.log) are retained.

| Gate | Recorded result |
| --- | --- |
| Backend tests | Built in Release; TRX records 38/38 executed and passed, zero failed or skipped. The new cases cover vertex-default formats, fullscreen empty layouts, and cache-growth timing. |
| Preflight build | Succeeded with 0 warnings and 0 errors. The prior shader discovery CS8619 warning did not recur. |
| `--sdl-draw` | Exited 0 without timeout or stderr. The hidden SDL3 Vulkan path translated GLSL 330 vertex/fragment stages, created shader modules and a retained graphics pipeline, recorded dynamic state and a fullscreen `vkCmdDraw`, submitted the render-target frame, blitted it to an acquired swapchain image, presented, and released resources. |

This is local Windows/Vulkan evidence for one pipeline draw command through the retained backend. The hidden window was not read back or visually inspected, so the batch does not prove pixel colors. It also does not exercise mesh buffers, bound sampler descriptors, game shader variants, menu/world routing, provider passes, SDL input handling, normal-shortcut integration, or release packaging. Those milestones remain open.
