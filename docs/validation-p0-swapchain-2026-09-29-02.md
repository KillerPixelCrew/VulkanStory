# P0 swapchain validation — second batch, 2026-09-29

Status: **backend compile failed at a descriptor layout helper; preflight was not built or run.** This was one bounded validation batch after the preceding descriptor source change. No source was changed and no command was rerun in this turn.

The batch started a Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, with a planned preflight build and timed `--sdl-swapchain` run contingent on compilation. The [summary](../artifacts/validation/p0-swapchain-20260929-02/summary.json) and [backend console log](../artifacts/validation/p0-swapchain-20260929-02/backend.console.txt) are retained. The backend step exited 1; no TRX exists. Both later steps were skipped.

The compiler reported CS0103 at `Core/DescriptorCache.cs:424`: `SharedPipelineLayout` is unavailable. That call needs only the descriptor-type decision for the program-record binding. The staged `SharedPipelineLayout.cs` defines it, but also depends on the still-excluded bindless texture table. Source inspection found no other caller of `SharedPipelineLayout.StorageSetDescriptorType`. The next implementation turn should move the small decision to a game-neutral descriptor helper shared by the cache and layout, or include the full bindless group when ready, before another bounded validation.

No SDL window, Vulkan surface, swapchain, acquired image, present call, or game process ran in this batch. Earlier SDL surface and headless context records retain their narrower scope. P0 and the full port remain open.

Follow-up implementation: the storage-set descriptor-type decision moved to `Core/DescriptorBindingTypes.cs`. `DescriptorCache` calls it directly, while the staged `SharedPipelineLayout` delegates to the same helper. One focused descriptor-type test was added. These later source edits have not been built or run in this validation record.
