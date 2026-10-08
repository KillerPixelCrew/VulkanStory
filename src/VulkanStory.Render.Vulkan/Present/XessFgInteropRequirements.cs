using System;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Reserves Win32 image and fence imports when the Vulkan device is created.</summary>
internal sealed class XessFgInteropRequirements : IDeviceRequirementContributor
{
    /// <inheritdoc/>
    public string Name => "XeSS-FG Vulkan/DX12 image sharing";
    /// <summary>Whether both required Win32 external-memory and external-semaphore extensions were enabled.</summary>
    public bool Available { get; private set; }

    /// <inheritdoc/>
    public void ContributeInstanceExtensions(InstanceRequirements requirements) { }

    /// <inheritdoc/>
    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        Available = OperatingSystem.IsWindows() &&
            requirements.Request("VK_KHR_external_memory_win32", requestedBy: Name) &&
            requirements.Request("VK_KHR_external_semaphore_win32", requestedBy: Name);
    }
}
