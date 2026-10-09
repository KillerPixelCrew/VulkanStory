# Deferred retirement ownership

Implementation turn, 2026-10-04. Workspace: `D:/Coding/VulkanStory-Rewrite`
(non-Git). This is a direct, bounded REN-05 increment, not application of the
saved nine-file provider-lifetime proposal.

`RetireQueue.Collect` previously removed all ready entries before disposing any
of them. `DisposeAll` likewise cleared its entire batch first. If one disposal
threw, the queue lost both that owner and every unprocessed entry in the batch.

The queue now keeps each entry until its disposal returns successfully. Ready
entries still follow retirement order and disposal remains outside the producer
lock. Successfully disposed entries are removed by entry identity; duplicate
resource registrations cannot remove a different queued entry. PendingCount
includes the entry currently being disposed. Concurrent Retire calls remain
accepted, including after failure, to preserve ownership.

The first disposal exception is retained. Later Collect/DisposeAll calls reject
cleanup without retrying that resource or releasing the tail. FrameRing checks
this failure before draining or disposing; VulkanDevice checks before its
already-disposed early return and before starting teardown. This prevents a
known failed deferred owner from being bypassed on a later cleanup attempt.
The original failure is propagated on its first occurrence and retained as the
inner exception on subsequent attempts. Consumer operations remain render-thread
only, as before.

Applied source:

- `src/VulkanStory.Render.Vulkan/Frame/FrameTimeline.cs`
- `src/VulkanStory.Render.Vulkan/Core/FrameRing.cs`
- `src/VulkanStory.Render.Vulkan/VulkanDevice.cs`

Source inspection covered queue selection/removal, failure retention, producer
locking, normal-frame Collect, drain/disposal callers and device guard ordering.
No builds, tests, probes, native calls, game runs, packages or deployments ran.
No tests were added. The candidate ZIP and installed game were not changed.

REN-05 and SDK-03 remain open. This does not repair FSR3 native disable/destroy,
swapchain preparation, Streamline successor ownership, ignored teardown wait
results, or arbitrary failures elsewhere in device/session disposal. Failed
resources intentionally remain retained until process exit. Compilation and
runtime failure-path acceptance require a later bounded validation turn.
