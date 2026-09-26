using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed unsafe class XessFgBridgeTests
{
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [SkippableFact]
    public void HeadlessVulkanAdapterCreatesMatchingDx12XellAndXeFgContexts()
    {
        Skip.If(Environment.GetEnvironmentVariable("OPTIMUM_XESS_FG_PROBE") != "1",
            "Run the XeSS-FG bridge probe in its own testhost with OPTIMUM_XESS_FG_PROBE=1.");
        uint previous = SetErrorMode(0x0002);
        VulkanContext? vulkan = null;
        XessFgRuntime? intel = null;
        VulkanSharedImage? shared = null;
        VulkanSharedImage? depth = null;
        XessSharedFence? fence = null;
        try
        {
            Assert.Equal(216, Marshal.SizeOf<XessPresentationFrame>());
            var messages = new List<string>();
            VulkanContextOptions options = GpuTest.ContextOptions(messages);
            options.RequirementContributors.Add(new XessFgInteropRequirements());
            Assert.True(VulkanContext.TryCreate(options,
                out vulkan, out string? reason), reason);
            Assert.NotNull(vulkan);
            Assert.True(XessFgRuntime.TryCreate(vulkan!, out intel, out reason), reason);
            Assert.NotNull(intel);
            Assert.Equal(0, intel!.SetLatencyMode(0, enabled: true));
            Assert.True(VulkanSharedImage.TryCreate(vulkan!, intel, 16, 16,
                Format.B8G8R8A8Unorm, out shared, out reason), reason);
            Assert.NotNull(shared);
            Assert.NotEqual((nint)0, shared!.D3D12Resource);
            Assert.True(VulkanSharedImage.TryCreate(vulkan!, intel, 16, 16,
                Format.D32Sfloat, out depth, out reason), reason);
            Assert.NotNull(depth);
            vulkan!.Api.GetPhysicalDeviceFormatProperties(vulkan.PhysicalDevice,
                Format.D32Sfloat, out FormatProperties depthProperties);
            Assert.True((depthProperties.OptimalTilingFeatures &
                (FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit)) ==
                (FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit),
                "D32 depth cannot be flipped by Vulkan blit on this adapter");
            Assert.True(XessSharedFence.TryCreate(vulkan!, intel, out fence,
                out reason), reason);
            Assert.NotNull(fence);
            Assert.Equal(0, intel.SignalSharedFence(1));
            var semaphore = fence!.Semaphore;
            ulong firstValue = 1;
            var firstWait = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &semaphore,
                PValues = &firstValue,
            };
            Assert.Equal(Result.Success,
                vulkan!.Api.WaitSemaphores(vulkan.Device, &firstWait, 5_000_000_000));
            var signal = new SemaphoreSignalInfo
            {
                SType = StructureType.SemaphoreSignalInfo,
                Semaphore = semaphore,
                Value = 2,
            };
            Assert.Equal(Result.Success, vulkan.Api.SignalSemaphore(vulkan.Device, &signal));
            Assert.Equal(0, intel.WaitSharedFence(2));
            Assert.Equal(0, intel.SignalSharedFence(3));
            ulong finalValue = 3;
            var finalWait = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &semaphore,
                PValues = &finalValue,
            };
            Assert.Equal(Result.Success,
                vulkan.Api.WaitSemaphores(vulkan.Device, &finalWait, 5_000_000_000));

            // Exercise a real Vulkan queue submission and ownership handoff,
            // without opening a window or calling the Intel proxy Present.
            using (var ring = new FrameRing(vulkan, framesInFlight: 2,
                       uniformRingSize: 65536, stagingPerSlot: 65536))
            using (var textures = new TextureManager(vulkan, ring.Uploads))
            {
                int sourceId = textures.Create(16, 16, Format.R8G8B8A8Unorm);
                int depthId = textures.Create(16, 16, Format.D32Sfloat);
                VulkanTexture source = textures.Get(sourceId)!;
                VulkanTexture sourceDepth = textures.Get(depthId)!;
                var barriers = new BarrierBatcher(vulkan.Api);
                ring.BeginFrame();
                ulong rendered = ring.EndFrame();
                CommandBuffer commands = ring.BeginPresentCommands();
                textures.Require(barriers, commands, source, ResourceUsage.TransferSrc);
                textures.Require(barriers, commands, sourceDepth, ResourceUsage.TransferSrc);
                barriers.Flush(commands);
                shared.RecordFlippedCopy(commands, source);
                depth.RecordFlippedCopy(commands, sourceDepth);
                ring.SubmitExternalPresent(rendered, fence.Semaphore, 3, 4);
                Assert.Equal(0, intel.WaitSharedFence(4));
                Assert.Equal(0, intel.SignalSharedFence(5));
                ulong copiedValue = 5;
                var copiedWait = new SemaphoreWaitInfo
                {
                    SType = StructureType.SemaphoreWaitInfo,
                    SemaphoreCount = 1,
                    PSemaphores = &semaphore,
                    PValues = &copiedValue,
                };
                Assert.Equal(Result.Success,
                    vulkan.Api.WaitSemaphores(vulkan.Device, &copiedWait, 5_000_000_000));
                vulkan.WaitDeviceIdle();
            }
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
        finally
        {
            fence?.Dispose();
            depth?.Dispose();
            shared?.Dispose();
            intel?.Dispose();
            vulkan?.Dispose();
            SetErrorMode(previous);
        }
    }
}
