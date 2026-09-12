using System.IO;

using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The two things that only exist once the three foundation streams of the
/// frame-generation design (docs/ROADMAP.md, "DLSS frame generation: the design")
/// are in one tree. Each stream was green on its own branch; neither of these
/// facts could be stated there, because each is about the meeting point.
///
/// <para><b>1. The re-derived acquire bound has to cover the second present.</b>
/// Step 0 added a second <c>vkAcquireNextImageKHR</c> and a second
/// <c>vkQueuePresentKHR</c> per rendered frame; step 4 replaced the free list's
/// old <c>imageCount + 1</c> with <c>max(imageCount, F x P) + 1</c> and threaded
/// <c>PresentPressure</c> to the swapchain. Separately each is right and each is
/// tested. Together they are only right if the device actually passes <c>P = 2</c>
/// - and unconditionally, because <c>GeneratedPresentEnabled</c> is a runtime
/// property (the env var reads it once at construction, the GPU test flips it
/// between runs) while the semaphores are created once per swapchain. A device
/// that sized for one present and then had the switch flipped would exhaust the
/// free list inside <c>AcquireSemaphoreFreeList.Take</c> mid-frame. Sizing for the
/// worst case always costs a handful of binary semaphores.</para>
///
/// <para><b>2. The snapshot is still allocated only when something wants it.</b>
/// Step 2's HUD-less target is one display-resolution RGBA8 image behind one gate,
/// <c>OptimumSceneNoHudRequested</c>. Step 4 made the ring three deep, and a
/// per-frame-slot resource would have become three images by that flip alone. This
/// one is not per slot and not transient: it is created in the framebuffer setup,
/// inside the gate, and it has to survive past the pass that writes it - with
/// frame generation, past the frame - so a transient could be aliased away
/// underneath it. Both halves are pinned here: the gate, and the allocation being
/// a plain texture rather than a transient.</para>
///
/// <para>The behavioural proof of the first is the GPU test
/// <c>GeneratedPresentTests.TwoPresentsPerFrameCarryTheSameImageAndSurviveTheFreeList</c>,
/// which now runs at the shipped ring depth against the live
/// <c>PresentPressure</c>; of the second,
/// <c>SceneNoHudSnapshotTests</c>. These are the source shapes that would
/// otherwise be undone quietly by a later edit.</para>
/// </summary>
public class DlssgFoundationMergeCoverageTests
{
    private const string DevicePath = "Optimum.Render.Vulkan/VulkanDevice.cs";
    private const string RetirementPath = "Optimum.Render.Vulkan/Present/SwapchainRetirement.cs";
    private const string SwapchainPath = "Optimum.Render.Vulkan/Present/Swapchain.cs";
    private const string FrameBuffersPath =
        "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs";
    private const string LibPath =
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>
    /// The swapchain is built for the generated present, at the ring depth the
    /// device chose, with no condition on the switch.
    /// </summary>
    [Fact]
    public void TheSwapchainIsSizedForTwoPresentsPerFrameAtTheChosenRingDepth()
    {
        string device = Read(DevicePath);

        Assert.Contains(
            "PresentPressure.ForFrames(\n                        _frames.FramesInFlight, PresentPressure.GeneratedPlusRealPerFrame)",
            device.Replace("\r\n", "\n"));

        // Not conditional on the runtime switch. If either of these ever appears,
        // the free list is one flip away from throwing inside a frame.
        Assert.DoesNotContain("GeneratedPresentEnabled ? PresentPressure", device);
        Assert.DoesNotContain(
            "PresentPressure.ForFrames(_frames.FramesInFlight)", device);
    }

    /// <summary>
    /// The bound itself, and the one place it is applied. F x P, with the image
    /// count kept only as a floor.
    /// </summary>
    [Fact]
    public void TheAcquireBoundIsFramesTimesPresentsWithTheImageCountOnlyAsAFloor()
    {
        string retirement = Read(RetirementPath);
        string swapchain = Read(SwapchainPath);

        Assert.Contains("public const int GeneratedPlusRealPerFrame = 2;", retirement);
        Assert.Contains(
            "public int PeakHeldAcquireSemaphores => Math.Max(1, FramesInFlight) * Math.Max(1, PresentsPerFrame);",
            retirement);
        Assert.Contains(
            "public static int CapacityFor(uint imageCount, PresentPressure pressure) =>\n" +
            "        Math.Max((int)imageCount, pressure.PeakHeldAcquireSemaphores) + 1;",
            retirement.Replace("\r\n", "\n"));

        // Every slot, the first and every rebuild, is created from the pressure the
        // device handed the swapchain - not from a fresh default.
        Assert.Contains(
            "_acquireSemaphores = CreateSemaphores(AcquireSemaphoreFreeList.CapacityFor(count, pressure));",
            swapchain);
        Assert.Contains("presentMode, _pressure);", swapchain);
    }

    /// <summary>
    /// A frame that wants neither an upscaler nor frame generation allocates no
    /// snapshot and copies nothing, at any ring depth.
    /// </summary>
    [Fact]
    public void TheSceneNoHudSnapshotIsBehindItsGateAndIsNotPerFrameSlot()
    {
        string frameBuffers = Read(FrameBuffersPath);
        string lib = Read(LibPath);

        // One gate, one allocation, inside it.
        Assert.Contains("if (OptimumSceneNoHudRequested)", frameBuffers);
        Assert.Contains(
            "list[OptimumSceneNoHudIndex] = CreateOptimumSceneNoHudTarget(displayWidth, displayHeight);",
            frameBuffers);

        // Not a transient: the transient allocator may alias an image with any
        // other slot once the pass that wrote it ended, and this one outlives it.
        string target = frameBuffers[frameBuffers.IndexOf("private FrameBufferRef CreateOptimumSceneNoHudTarget")..];
        target = target[..target.IndexOf("\n    }")];
        Assert.Contains("device.CreateTexture2D(", target);
        Assert.DoesNotContain("CreateTransientTexture2D", target);

        // Nothing sizes it per frame in flight: one image, not one per ring slot.
        Assert.DoesNotContain("FramesInFlight", target);

        // The capture itself is a no-op when the slot was never published, which is
        // what "a normal frame pays nothing" means on the lib side.
        Assert.Contains("if (optimumSceneNoHudIndex < 0 || frameBuffers == null) return;", lib);
        // No initializer - Cecil injects the field but not the constructor, so it is 0 at
        // runtime; SetOptimumSceneNoHudIndex(-1) is what actually unpublishes the slot.
        Assert.Contains("private int optimumSceneNoHudIndex;", lib);
        Assert.Contains("SetOptimumSceneNoHudIndex(-1);", lib);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
