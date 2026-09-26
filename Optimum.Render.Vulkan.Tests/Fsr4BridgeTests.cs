using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class Fsr4BridgeTests
{
    [Fact]
    public void DispatchParametersMatchTheNativeAbi()
    {
        Assert.Equal(96, Marshal.SizeOf<Fsr4Frame>());
        Assert.Equal(80, Marshal.OffsetOf<Fsr4Frame>(nameof(Fsr4Frame.ReadyValue)).ToInt32());
    }

    [SkippableFact]
    public void Dx12ProviderUsesTheSelectedVulkanAdapterAndImportsItsResources()
    {
        Skip.If(!Fsr4Runtime.TryLoad(out nint bridge, out string reason), reason);
        VulkanContext? vulkan = null;
        Fsr4Runtime? runtime = null;
        Fsr4SharedFrames? shared = null;
        try
        {
            var messages = new List<string>();
            VulkanContextOptions options = GpuTest.ContextOptions(messages);
            options.RequirementContributors.Add(new XessFgInteropRequirements());
            Assert.True(VulkanContext.TryCreate(options, out vulkan, out reason), reason);
            Assert.NotNull(vulkan);
            vulkan!.Api.GetPhysicalDeviceProperties(vulkan.PhysicalDevice,
                out PhysicalDeviceProperties properties);
            int probe = Fsr4Runtime.Probe(bridge, (nint)vulkan.PhysicalDevice.Handle);
            if (properties.VendorID != 0x1002)
            {
                Assert.NotEqual(0, probe);
                return;
            }
            Assert.Equal(0, probe);
            var plan = new UpscalerPlan(32, 32, 48, 48, "quality");
            Skip.If(!Fsr4Runtime.TryCreate(bridge, (nint)vulkan.PhysicalDevice.Handle,
                plan, out runtime, out reason), reason);
            Assert.True(Fsr4SharedFrames.TryCreate(vulkan, runtime!, plan,
                out shared, out reason), reason);
            Assert.NotNull(shared);
            Assert.NotEqual(0UL, shared!.SharedSemaphore.Handle);
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
        finally
        {
            shared?.Dispose();
            runtime?.Dispose();
            vulkan?.Dispose();
            NativeLibrary.Free(bridge);
        }
    }
}
