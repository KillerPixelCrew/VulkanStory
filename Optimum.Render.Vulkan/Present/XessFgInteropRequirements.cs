using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>Reserves Win32 image and fence imports when the Vulkan device is created.</summary>
internal sealed class XessFgInteropRequirements : IDeviceRequirementContributor
{
    public string Name => "XeSS-FG Vulkan/DX12 image sharing";
    public bool Available { get; private set; }

    public void ContributeInstanceExtensions(InstanceRequirements requirements) { }

    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        Available = OperatingSystem.IsWindows() &&
            requirements.Request("VK_KHR_external_memory_win32", requestedBy: Name) &&
            requirements.Request("VK_KHR_external_semaphore_win32", requestedBy: Name);
    }
}
