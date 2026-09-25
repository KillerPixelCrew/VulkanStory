using System;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public sealed class UpscalerDeviceRequirementsTests(ITestOutputHelper output)
{
    private sealed class UnavailableProvider : IDeviceRequirementContributor
    {
        public string Name => "test-upscaler";
        public bool InstanceCalled;
        public bool DeviceCalled;

        public void ContributeInstanceExtensions(InstanceRequirements requirements)
        {
            InstanceCalled = true;
            Assert.False(requirements.Request("VK_OPTIMUM_missing_instance_extension", Name));
        }

        public void ContributeDeviceRequirements(DeviceRequirements requirements)
        {
            DeviceCalled = true;
            Assert.False(requirements.Request("VK_OPTIMUM_missing_device_extension", requestedBy: Name));
        }
    }

    [SkippableFact]
    public void MissingOptionalProviderRequirementsDoNotPreventVulkanDeviceCreation()
    {
        var provider = new UnavailableProvider();
        using VulkanDevice device = GpuTest.CreateDevice(output, target =>
        {
            Action<VulkanContextOptions>? previous = target.ConfigureContextOptions;
            target.ConfigureContextOptions = options =>
            {
                previous?.Invoke(options);
                options.RequirementContributors.Add(provider);
            };
        });

        Assert.True(provider.InstanceCalled);
        Assert.True(provider.DeviceCalled);
        GpuTest.AssertClean(device);
    }
}
