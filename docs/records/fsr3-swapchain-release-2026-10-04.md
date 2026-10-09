# FSR3 swapchain release ownership — 2026-10-04

DIRECT implementation turn in `D:/Coding/VulkanStory-Rewrite` (no Git repository).
SDK-03/REN-05 remain open. This repairs source defects; it is not GPU evidence.

## Applied change

Native FSR3 chain destruction previously logged failed disable/wait results and
continued into destruction. Context destruction ignored failed FG disable and
deleted its owner even if the SDK context destruction failed. The managed
swapchain wrapper ignored that returned error and cleared its context pointer.

The bridge now exports `VulkanStoryFsr3SwapchainPrepareDestroy` and
`VulkanStoryFsr3SwapchainDestroyChainChecked` (both return int). Preparation
disables the matching active FG owner and waits for SDK presents. Any error
returns before dependent destruction. Context destruction retains the native
owner on disable, wait or destroy failure. FG disable returns immediately after
failed disable/wait/UI-unregister and rejects a missing or mismatched live
swapchain owner instead of certifying drain. Ownership links clear only after
success. The old void chain-destruction export remains a compatibility wrapper;
it logs a failure and retains ownership.

The managed loader requires both new exports; old bridges cannot satisfy this
contract and are reported unavailable. Existing layouts and export signatures
are preserved. No proposed ABI-2 payload was copied. A matching fresh FSR3 bridge
is required for the next runtime/package input.

Managed slot teardown prepares the provider before releasing views/fences/
semaphores. The runtime checks native chain/context destruction results, retains
its context and records failure. Later use/release rejects a known failed runtime.
Slots and the swapchain mark disposal complete only after success and reject
retries after partial failure. Dispatch disposal precedes surface destruction.
Swapchain teardown checks device-idle success. Its lifetime guard reaches retained
slots, the current slot and dispatch; device cleanup checks it before its
already-disposed return and before dependent teardown begins.

SwapchainRetirement removes each entry only after successful disposal. A failed
owner and all unprocessed entries stay queued; successfully released entries
cannot be retried. Later collection/teardown rejects the first recorded failure.
Provider transitions process retained slots after checked device-idle even when
no current slot exists. Streamline's new preparation hook intentionally does
nothing: global disable remains with the transition owner, so this hook cannot
disable a live successor during old-slot retirement. Existing Streamline native
destruction behavior still needs separate investigation/repair.

## Evidence and remaining boundary

Source inspection followed native return paths, managed export signatures and
all production dispatch implementers, slot release order, transition/retirement
callers and device guard ordering. No builds, tests, probes, native/GPU/game runs,
packages or deployment occurred. No tests were added or changed. The previous
managed build predates these changes; its DLLs and the October-1 ZIP do not contain
this increment. Installed game/settings/save were untouched.

Outstanding work includes Streamline successor retirement, other provider/session/
device failure paths (including SR destruction returns and first-failure teardown
ordering), actual disable-failure execution, build/bridge pairing and vendor FG
output/pacing acceptance. This failure policy retains resources until process
exit; it does not promise recoverability after a partially failed destruction.

## Source identity (SHA256)

| File | SHA256 |
| --- | --- |
| `native/fsr3/bridge.cpp` | `520C30A088317A5666AC04CA3564796E885F736FF1DA1456B304C4B9B6A184C6` |
| `src/VulkanStory.Render.Vulkan/Upscale/Fsr3Native.cs` | `FDA3E960FB5DBC3E5F07897239DD4DE5917721CEFB4E613FA3BB0A9A10E4EB51` |
| `src/VulkanStory.Render.Vulkan/Present/Fsr3SwapchainRuntime.cs` | `F0C3FEDDE1541256CD780B0D6D17D9F2932EC0BDB2232129F4DE4C5A23C0F232` |
| `src/VulkanStory.Render.Vulkan/Present/SwapchainDispatch.cs` | `0CC9F7CF962DEFD411D9ED49B7D27889562BB537E350AC7EB82E253AC2C72A27` |
| `src/VulkanStory.Render.Vulkan/Present/SwapchainRetirement.cs` | `7E1AB9E261735B04D2275D84D458524CE46E562F69527B560FF54270B0560E19` |
| `src/VulkanStory.Render.Vulkan/Present/Swapchain.cs` | `55BA9702CC050DC774443DA81F3850227FB1C0641E3896267ED52D99F1FAB914` |
| `src/VulkanStory.Render.Vulkan/VulkanDevice.cs` | `6D922D4EBAC46023ED78D6DADDC1554322F9CEAC2196388FDFF8AF559CC2E873` |
