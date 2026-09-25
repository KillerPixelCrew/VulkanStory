using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public sealed class UpscalerDeviceRequirementsTests(ITestOutputHelper output)
{
    private sealed unsafe class AvailableProvider(string name, string instanceExtension,
        string deviceExtension, StructureType featureType) : IDeviceRequirementContributor
    {
        public string Name => name;
        public bool InstanceAccepted;
        public bool DeviceAccepted;
        public int FinalizeCalls;

        public void ContributeInstanceExtensions(InstanceRequirements requirements) =>
            InstanceAccepted = requirements.Request(instanceExtension, Name);

        public void ContributeDeviceRequirements(DeviceRequirements requirements)
        {
            DeviceAccepted = requirements.Request(deviceExtension, requestedBy: Name);
            if (!DeviceAccepted) return;
            if (featureType == StructureType.PhysicalDeviceFaultFeaturesExt)
                requirements.ChainFeature(new PhysicalDeviceFaultFeaturesEXT { SType = featureType });
            else if (featureType == StructureType.PhysicalDeviceColorWriteEnableFeaturesExt)
                requirements.ChainFeature(new PhysicalDeviceColorWriteEnableFeaturesEXT { SType = featureType });
            else
                throw new InvalidOperationException("Unsupported test feature type: " + featureType);
        }

        public void FinalizeDeviceFeatures(DeviceRequirements requirements, void** features)
        {
            Assert.True(*features != null);
            Assert.True(InstanceAccepted && DeviceAccepted);
            FinalizeCalls++;
        }
    }

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

    [Fact]
    public unsafe void TwoAvailableContributorsKeepBothExtensionsAndFeatureNodes()
    {
        const string firstInstance = "VK_OPTIMUM_test_instance_a";
        const string secondInstance = "VK_OPTIMUM_test_instance_b";
        const string firstDevice = "VK_OPTIMUM_test_device_a";
        const string secondDevice = "VK_OPTIMUM_test_device_b";
        var first = new AvailableProvider("first", firstInstance, firstDevice,
            StructureType.PhysicalDeviceFaultFeaturesExt);
        var second = new AvailableProvider("second", secondInstance, secondDevice,
            StructureType.PhysicalDeviceColorWriteEnableFeaturesExt);
        IDeviceRequirementContributor[] contributors = [first, second];
        var enabledInstance = new List<string>();
        var instance = new InstanceRequirements(
            new HashSet<string>([firstInstance, secondInstance], StringComparer.Ordinal), enabledInstance);
        foreach (var contributor in contributors) contributor.ContributeInstanceExtensions(instance);
        Assert.Equal([firstInstance, secondInstance], enabledInstance);

        var enabledDevice = new List<string>();
        using var device = new DeviceRequirements(null!, default, default,
            new Dictionary<string, uint> { [firstDevice] = 1, [secondDevice] = 1 }, enabledDevice);
        foreach (var contributor in contributors) contributor.ContributeDeviceRequirements(device);
        Assert.Equal([firstDevice, secondDevice], enabledDevice);

        // VulkanContext appends the contributor chain to its core feature root.
        var root = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = device.Chain,
        };
        void* chain = &root;
        foreach (var contributor in contributors) contributor.FinalizeDeviceFeatures(device, &chain);
        Assert.Equal(1, first.FinalizeCalls);
        Assert.Equal(1, second.FinalizeCalls);
        var types = new List<StructureType>();
        for (BaseOutStructure* node = (BaseOutStructure*)chain; node != null; node = node->PNext)
            types.Add(node->SType);
        Assert.Equal([
            StructureType.PhysicalDeviceFeatures2,
            StructureType.PhysicalDeviceColorWriteEnableFeaturesExt,
            StructureType.PhysicalDeviceFaultFeaturesExt,
        ], types);
    }
}
