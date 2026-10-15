using System;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Win32 image and fence imports for auxiliary DXGI presentation.</summary>
internal sealed class AuxiliaryPresentationRequirements : IDeviceRequirementContributor
{
    public string Name => "auxiliary Vulkan presentation";
    public void ContributeInstanceExtensions(InstanceRequirements requirements) { }
    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        if (!OperatingSystem.IsWindows()) return;
        requirements.Request("VK_KHR_external_memory_win32", requestedBy: Name);
        requirements.Request("VK_KHR_external_semaphore_win32", requestedBy: Name);
    }
}
