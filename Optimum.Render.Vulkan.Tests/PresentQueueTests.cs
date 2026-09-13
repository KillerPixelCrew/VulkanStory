using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The present thread's queue (ROADMAP "The paced present: the design"): a second
/// queue from the graphics family with a lock of its own, created only when frame
/// generation asks for it, and a capability that says why it is missing when it is.
///
/// <para>Not requested, the device is the one it always was: one create info,
/// queueCount 1. Requested on a family with a single queue, frame generation
/// stands down on the capability reason instead of sharing the graphics queue.</para>
///
/// <para>Queues are externally synchronised per queue. The race test submits on
/// both queues from two threads at once, each under its own queue's lock and
/// never the other's, so a driver or layer that treats the two as one resource
/// shows up as a validation error or a SYNC- hazard, and a deadlock fails on
/// the bounded wait rather than hanging the suite.</para>
/// </summary>
public class PresentQueueTests
{
    private readonly ITestOutputHelper _output;

    public PresentQueueTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false, 16u, (int)PresentQueueStatus.NotRequested)]
    [InlineData(false, 1u, (int)PresentQueueStatus.NotRequested)]
    [InlineData(true, 16u, (int)PresentQueueStatus.Available)]
    [InlineData(true, 2u, (int)PresentQueueStatus.Available)]
    [InlineData(true, 1u, (int)PresentQueueStatus.SingleQueueFamily)]
    public void APresentQueueNeedsARequestAndASecondQueueInTheFamily(bool requested, uint familyQueues, int expected)
    {
        Assert.Equal((PresentQueueStatus)expected, VulkanContext.DecidePresentQueue(requested, familyQueues));
    }

    [SkippableFact]
    public void NotRequestedTheDeviceHasOneQueueAsBefore()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            VulkanCapabilities capabilities = context!.Capabilities;
            _output.WriteLine("graphics family queues: " + capabilities.GraphicsFamilyQueueCount);
            Assert.Equal(1u, context.RequestedQueueCount);
            Assert.Equal(PresentQueueStatus.NotRequested, capabilities.PresentQueue);
            Assert.Equal(0, context.PresentQueue.Handle);
            Assert.NotEqual(0, context.GraphicsQueue.Handle);
            Assert.True(capabilities.GraphicsFamilyQueueCount >= 1);
            Assert.Equal("present queue not requested", capabilities.PresentQueueSummary);
            // The present lock exists regardless and is never the graphics queue's.
            Assert.NotSame(context.QueueLock, context.PresentQueueLock);

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    [SkippableFact]
    public void RequestedOnAFamilyWithSeveralQueuesThePresentQueueIsItsOwnQueue()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        options.RequestPresentQueue = true;
        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? why),
            "No usable Vulkan device: " + why);

        using (context)
        {
            VulkanCapabilities capabilities = context!.Capabilities;
            _output.WriteLine(capabilities.DeviceName + ": graphics family queues " +
                capabilities.GraphicsFamilyQueueCount + ", " + capabilities.PresentQueueSummary);
            Skip.If(capabilities.GraphicsFamilyQueueCount < 2,
                "this device's graphics family has one queue; the refusal case covers it");

            Assert.Equal(PresentQueueStatus.Available, capabilities.PresentQueue);
            Assert.Equal(2u, context.RequestedQueueCount);
            Assert.NotEqual(0, context.PresentQueue.Handle);
            Assert.NotEqual(context.GraphicsQueue.Handle, context.PresentQueue.Handle);
            Assert.NotSame(context.QueueLock, context.PresentQueueLock);
            Assert.Equal("present queue on", capabilities.PresentQueueSummary);

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    [SkippableFact]
    public void RequestedOnAOneQueueFamilyThePresentQueueIsRefusedWithTheReason()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        options.RequestPresentQueue = true;
        options.GraphicsFamilyQueueCountForTests = 1;
        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? why),
            "No usable Vulkan device: " + why);

        using (context)
        {
            VulkanCapabilities capabilities = context!.Capabilities;
            _output.WriteLine(capabilities.PresentQueueSummary);

            // The device still comes up: a refusal is frame generation's, not the renderer's.
            Assert.Equal(PresentQueueStatus.SingleQueueFamily, capabilities.PresentQueue);
            Assert.Equal(1u, capabilities.GraphicsFamilyQueueCount);
            Assert.Equal(1u, context.RequestedQueueCount);
            Assert.Equal(0, context.PresentQueue.Handle);
            Assert.NotEqual(0, context.GraphicsQueue.Handle);
            Assert.Contains("refused", capabilities.PresentQueueSummary);
            Assert.Contains("1 queue", capabilities.PresentQueueSummary);

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    private const int RaceIterations = 200;

    /// <summary>A deadlock or a lost device fails here instead of hanging the suite.</summary>
    private static readonly TimeSpan RaceBound = TimeSpan.FromSeconds(60);

    [SkippableFact]
    public unsafe void SubmittingOnBothQueuesFromTwoThreadsIsClean()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        options.RequestPresentQueue = true;
        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? why),
            "No usable Vulkan device: " + why);

        using (context)
        {
            Skip.If(context!.Capabilities.PresentQueue != PresentQueueStatus.Available,
                context.Capabilities.PresentQueueSummary);

            // Both threads are released together so the submits really overlap.
            using var start = new Barrier(2);
            var graphics = Task.Factory.StartNew(
                () => SubmitLoop(context, context.GraphicsQueue, context.QueueLock, start),
                TaskCreationOptions.LongRunning);
            var present = Task.Factory.StartNew(
                () => SubmitLoop(context, context.PresentQueue, context.PresentQueueLock, start),
                TaskCreationOptions.LongRunning);

            Assert.True(Task.WaitAll(new Task[] { graphics, present }, RaceBound),
                "the two submit loops did not finish within " + RaceBound);
            _output.WriteLine("graphics queue submits " + graphics.Result + ", present queue submits " + present.Result);
            Assert.Equal(RaceIterations, graphics.Result);
            Assert.Equal(RaceIterations, present.Result);

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// One thread's loop: its own command pool (pools are externally synchronised
    /// too), a command buffer that clears nothing but is real work for the queue,
    /// submitted under the given queue's lock and waited on outside it.
    /// </summary>
    private static unsafe int SubmitLoop(VulkanContext context, Queue queue, object queueLock, Barrier start)
    {
        Vk api = context.Api;
        Device device = context.Device;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = context.GraphicsQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        VulkanResult.Check(api.CreateCommandPool(device, &poolInfo, null, out CommandPool pool), "vkCreateCommandPool");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        VulkanResult.Check(api.CreateFence(device, &fenceInfo, null, out Fence fence), "vkCreateFence");

        int submitted = 0;
        try
        {
            var allocate = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer commandBuffer;
            VulkanResult.Check(api.AllocateCommandBuffers(device, &allocate, &commandBuffer), "vkAllocateCommandBuffers");

            if (!start.SignalAndWait(RaceBound)) throw new TimeoutException("the other submit loop never started");

            for (int i = 0; i < RaceIterations; i++)
            {
                VulkanResult.Check(api.ResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer");
                var begin = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                VulkanResult.Check(api.BeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer");
                VulkanResult.Check(api.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                lock (queueLock)
                {
                    VulkanResult.Check(api.QueueSubmit(queue, 1, &submit, fence), "vkQueueSubmit");
                }

                // Bounded: a lost device or a hung queue fails the test.
                Result waited = api.WaitForFences(device, 1, &fence, true, 10_000_000_000UL);
                if (waited != Result.Success) throw new TimeoutException("vkWaitForFences returned " + waited);
                VulkanResult.Check(api.ResetFences(device, 1, &fence), "vkResetFences");
                submitted++;
            }
        }
        finally
        {
            // Idle this thread's queue under its lock before its pool goes away.
            lock (queueLock) api.QueueWaitIdle(queue);
            api.DestroyFence(device, fence, null);
            api.DestroyCommandPool(device, pool, null);
        }
        return submitted;
    }
}
