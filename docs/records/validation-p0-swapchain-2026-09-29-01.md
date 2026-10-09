# P0 swapchain validation — 2026-09-29

Status: **backend compile failed; swapchain preflight was not built or run.** This was one bounded validation batch. No source was changed and no command was rerun.

The batch started a Release `dotnet test` of `VulkanStory.Render.Vulkan.Tests`, with a planned preflight build and timed `--sdl-swapchain` run contingent on that compile. The [summary](../../artifacts/validation/p0-swapchain-20260929-01/summary.json) and [backend console log](../../artifacts/validation/p0-swapchain-20260929-01/backend.console.txt) are retained. The backend step exited 1; no TRX exists. The preflight build and run were skipped.

The compiler reported CS0246 at `Frame/FrameTimeline.cs:377`: `DescriptorSetContents` is unavailable. `FrameTimeline.ResourceAge.NamesShortLived` also reads `SamplerBindingValue` and `BufferBindingValue`. Source inspection found all three in the excluded `Core/DescriptorCache.cs`, which contains the retained descriptor cache and no direct Vintage Story type reference. The next implementation turn can include that source or extract its immutable key types into a game-neutral file. A later validation turn must run a new bounded batch before claiming swapchain compilation or creation.

No SDL window, Vulkan surface, swapchain, acquired image, present call, game process, or provider runtime was exercised in this batch. Earlier SDL surface and headless context records remain valid for their narrower scopes; P0 and the full port remain open.

Follow-up implementation: `Core/DescriptorCache.cs` and `Core/DescriptorArena.cs` were added to the backend project's explicit compile list, and two retained CPU resource-lifetime cases were added. These later source edits have not been built or run in this validation record.
