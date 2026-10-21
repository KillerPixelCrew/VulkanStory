using VulkanStory.Render.Vulkan.Core;
using V = Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Vr;

internal abstract unsafe class XrRuntime : IDisposable
{
    internal abstract int ReferenceRevision { get; }
    internal bool Running { get; private protected set; }
    internal bool ShouldRender { get; private protected set; }
    internal int Width { get; private protected set; }
    internal int Height { get; private protected set; }
    internal V.Format ColorFormat { get; private protected set; }
    internal V.Format TransferFormat => ColorFormat == V.Format.B8G8R8A8Srgb ? V.Format.B8G8R8A8Unorm : V.Format.R8G8B8A8Unorm;
    internal float? FrameDeltaSeconds { get; private protected set; }
    internal string Status { get; private protected set; } = "OpenXR is starting.";
    internal string RuntimeName { get; private protected set; } = "";
    internal string FrameStatus { get; private protected set; } = "Waiting for session readiness.";

    internal virtual V.Result CreateVulkanInstance(V.Vk vk, V.InstanceCreateInfo* createInfo, out V.Instance created) =>
        vk.CreateInstance(createInfo, null, out created);

    // Null leaves adapter enumeration, preference and usability checks to the context.
    internal virtual V.PhysicalDevice? SelectPhysicalDevice(V.Instance vkInstance) => null;

    internal virtual V.Result CreateVulkanDevice(V.Vk vk, V.PhysicalDevice physical, V.DeviceCreateInfo* createInfo, out V.Device created) =>
        vk.CreateDevice(physical, createInfo, null, out created);

    internal abstract void Attach(VulkanContext device);
    internal abstract void PollEvents();
    internal abstract void BeginFrame();
    internal abstract OpenXrEyeView Eye(int eye);
    internal abstract V.Image Image(int eye);
    internal abstract void MarkCopied(int eye);
    internal abstract void UseHeadLockedMenu();
    internal abstract void SuppressFrame();
    internal abstract void EndFrame();
    public abstract void Dispose();
}
