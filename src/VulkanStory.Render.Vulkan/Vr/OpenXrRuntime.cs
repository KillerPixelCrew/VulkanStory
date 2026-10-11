using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using VulkanStory.Render.Vulkan.Core;
using V = Silk.NET.Vulkan;
using Swapchain = Silk.NET.OpenXR.Swapchain;

namespace VulkanStory.Render.Vulkan.Vr;

internal readonly record struct OpenXrEyeView(Vector3 Position, Quaternion Orientation,
    float AngleLeft, float AngleRight, float AngleUp, float AngleDown);

// The renderer owns XR and Vulkan together. The game adapter supplies only eye cameras
// and finished colour; game types and Harmony never enter the runtime.
internal sealed unsafe class OpenXrRuntime : IDisposable
{
    private readonly XR api;
    private Instance instance;
    private KhrVulkanEnable2? vulkan;
    private ulong system;
    private Session session;
    private Space space;
    private VulkanContext? context;
    private readonly View[] views = new View[2];
    private readonly ViewConfigurationView[] configuration = new ViewConfigurationView[2];
    private readonly Swapchain[] swapchains = new Swapchain[2];
    private readonly SwapchainImageVulkanKHR[][] images = new SwapchainImageVulkanKHR[2][];
    private readonly uint[] acquired = new uint[2];
    private readonly bool[] imageHeld = new bool[2];
    private readonly bool[] copied = new bool[2];
    private bool frameOpen, terminal;
    private long displayTime;
    private SessionState state;
    private long referenceChangeTime = long.MaxValue;
    internal int ReferenceRevision { get; private set; }
    internal bool Running { get; private set; }
    internal bool ShouldRender { get; private set; }
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal string Status { get; private set; } = "OpenXR is starting.";
    internal string RuntimeName { get; private set; } = "";

    private OpenXrRuntime(XR api) => this.api = api;

    internal static OpenXrRuntime Create()
    {
        var runtime = new OpenXrRuntime(XR.GetApi());
        try { runtime.CreateInstance(); return runtime; }
        catch { runtime.Dispose(); throw; }
    }

    private void CreateInstance()
    {
        const string extension = "XR_KHR_vulkan_enable2";
        if (!api.IsInstanceExtensionPresent(null, extension))
            throw new NotSupportedException("The active OpenXR runtime does not expose XR_KHR_vulkan_enable2.");
        nint name = SilkMarshal.StringToPtr(extension);
        try
        {
            byte* extensionName = (byte*)name;
            var info = new InstanceCreateInfo { Type = StructureType.InstanceCreateInfo,
                EnabledExtensionCount = 1, EnabledExtensionNames = &extensionName };
            Encoding.UTF8.GetBytes("VulkanStory", new Span<byte>(info.ApplicationInfo.ApplicationName, 128));
            Encoding.UTF8.GetBytes("VulkanStory", new Span<byte>(info.ApplicationInfo.EngineName, 128));
            info.ApplicationInfo.ApiVersion = 1ul << 48; // OpenXR 1.0, including the Vulkan enable2 extension.
            Instance created;
            Check(api.CreateInstance(&info, &created), "xrCreateInstance");
            instance = created;
            api.CurrentInstance = instance;
        }
        finally { SilkMarshal.Free(name); }
        var properties = new InstanceProperties { Type = StructureType.InstanceProperties };
        Check(api.GetInstanceProperties(instance, &properties), "xrGetInstanceProperties");
        RuntimeName = Marshal.PtrToStringUTF8((nint)properties.RuntimeName) ?? "OpenXR";
        if (!api.TryGetInstanceExtension<KhrVulkanEnable2>(null, instance, out var binding))
            throw new NotSupportedException("OpenXR Vulkan entry points are unavailable.");
        vulkan = binding;
        var systemInfo = new SystemGetInfo { Type = StructureType.SystemGetInfo,
            FormFactor = FormFactor.HeadMountedDisplay };
        ulong selected;
        Check(api.GetSystem(instance, &systemInfo, &selected), "xrGetSystem (connect the headset)");
        system = selected;
        var requirements = new GraphicsRequirementsVulkanKHR { Type = StructureType.GraphicsRequirementsVulkanKhr };
        Check(vulkan.GetVulkanGraphicsRequirements2(instance, system, &requirements), "xrGetVulkanGraphicsRequirements2KHR");
        ulong version = (1ul << 48) | (3ul << 32);
        if (version < requirements.MinApiVersionSupported)
            throw new NotSupportedException("The active OpenXR runtime does not support the renderer's Vulkan 1.3 device.");
        uint count;
        Check(api.EnumerateViewConfigurationView(instance, system, ViewConfigurationType.PrimaryStereo,
            0, &count, null), "xrEnumerateViewConfigurationViews");
        if (count != 2) throw new NotSupportedException("OpenXR primary stereo must supply exactly two eyes.");
        for (int eye = 0; eye < 2; eye++)
            configuration[eye] = new ViewConfigurationView { Type = StructureType.ViewConfigurationView };
        fixed (ViewConfigurationView* output = configuration)
            Check(api.EnumerateViewConfigurationView(instance, system, ViewConfigurationType.PrimaryStereo,
                2, &count, output), "xrEnumerateViewConfigurationViews");
        Check(api.EnumerateEnvironmentBlendModes(instance, system, ViewConfigurationType.PrimaryStereo,
            0, &count, null), "xrEnumerateEnvironmentBlendModes");
        var modes = new EnvironmentBlendMode[count];
        fixed (EnvironmentBlendMode* output = modes)
            Check(api.EnumerateEnvironmentBlendModes(instance, system, ViewConfigurationType.PrimaryStereo,
                count, &count, output), "xrEnumerateEnvironmentBlendModes");
        if (!modes.Contains(EnvironmentBlendMode.Opaque))
            throw new NotSupportedException("The active OpenXR headset does not provide opaque stereo presentation.");
        Status = RuntimeName + ": waiting for the Vulkan session.";
    }

    internal V.Result CreateVulkanInstance(V.Vk vk, V.InstanceCreateInfo* createInfo, out V.Instance created)
    {
        var info = new VulkanInstanceCreateInfoKHR { Type = StructureType.VulkanInstanceCreateInfoKhr,
            SystemId = system, VulkanCreateInfo = createInfo,
            PfnGetInstanceProcAddr = new PfnVoidFunction((delegate* unmanaged[Cdecl]<void>)vk.Context.GetProcAddress("vkGetInstanceProcAddr")) };
        VkHandle handle = default;
        uint result;
        Check(vulkan!.CreateVulkanInstance(instance, &info, &handle, &result), "xrCreateVulkanInstanceKHR");
        created = new V.Instance(handle.Handle);
        return (V.Result)result;
    }

    internal V.PhysicalDevice SelectPhysicalDevice(V.Instance vkInstance)
    {
        var info = new VulkanGraphicsDeviceGetInfoKHR { Type = StructureType.VulkanGraphicsDeviceGetInfoKhr,
            SystemId = system, VulkanInstance = new VkHandle(vkInstance.Handle) };
        VkHandle handle;
        Check(vulkan!.GetVulkanGraphicsDevice2(instance, &info, &handle), "xrGetVulkanGraphicsDevice2KHR");
        return new V.PhysicalDevice(handle.Handle);
    }

    internal V.Result CreateVulkanDevice(V.Vk vk, V.PhysicalDevice physical, V.DeviceCreateInfo* createInfo, out V.Device created)
    {
        var info = new VulkanDeviceCreateInfoKHR { Type = StructureType.VulkanDeviceCreateInfoKhr,
            SystemId = system, VulkanPhysicalDevice = new VkHandle(physical.Handle), VulkanCreateInfo = createInfo,
            PfnGetInstanceProcAddr = new PfnVoidFunction((delegate* unmanaged[Cdecl]<void>)vk.Context.GetProcAddress("vkGetInstanceProcAddr")) };
        VkHandle handle = default;
        uint result;
        Check(vulkan!.CreateVulkanDevice(instance, &info, &handle, &result), "xrCreateVulkanDeviceKHR");
        created = new V.Device(handle.Handle);
        return (V.Result)result;
    }

    internal void Attach(VulkanContext device, int width, int height)
    {
        context = device;
        var graphics = new GraphicsBindingVulkanKHR { Type = StructureType.GraphicsBindingVulkanKhr,
            Instance = new VkHandle(device.Instance.Handle), PhysicalDevice = new VkHandle(device.PhysicalDevice.Handle),
            Device = new VkHandle(device.Device.Handle), QueueFamilyIndex = device.GraphicsQueueFamily, QueueIndex = 0 };
        var info = new SessionCreateInfo { Type = StructureType.SessionCreateInfo, Next = &graphics, SystemId = system };
        Session created;
        Check(api.CreateSession(instance, &info, &created), "xrCreateSession");
        session = created;
        var reference = new ReferenceSpaceCreateInfo { Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = ReferenceSpaceType.Local,
            PoseInReferenceSpace = new Posef { Orientation = new Quaternionf { W = 1 } } };
        Space local;
        Check(api.CreateReferenceSpace(session, &reference, &local), "xrCreateReferenceSpace");
        space = local;
        EnsureSwapchains(width, height);
        Status = RuntimeName + ": session created; waiting for headset readiness.";
    }

    private void EnsureSwapchains(int width, int height)
    {
        if (Width == width && Height == height) return;
        if (frameOpen) throw new InvalidOperationException("Cannot resize OpenXR swapchains inside an XR frame.");
        for (int eye = 0; eye < 2; eye++)
            if (width <= 0 || height <= 0 || width > configuration[eye].MaxImageRectWidth || height > configuration[eye].MaxImageRectHeight)
                throw new NotSupportedException("The game window resolution exceeds the OpenXR eye-image limits.");
        uint count;
        Check(api.EnumerateSwapchainFormats(session, 0, &count, null), "xrEnumerateSwapchainFormats");
        var formats = new long[count];
        fixed (long* values = formats)
            Check(api.EnumerateSwapchainFormats(session, count, &count, values), "xrEnumerateSwapchainFormats");
        // The game's final colour is gamma encoded. A raw copy into an sRGB XR image
        // preserves that encoding, instead of applying a second gamma conversion.
        if (!formats.Contains((long)V.Format.R8G8B8A8Srgb))
            throw new NotSupportedException("The active OpenXR runtime must provide R8G8B8A8_SRGB eye swapchains.");
        if (Width != 0)
            lock (context!.QueueLock) context.Api.DeviceWaitIdle(context.Device);
        DestroySwapchains();
        try
        {
            for (int eye = 0; eye < 2; eye++)
            {
                var info = new SwapchainCreateInfo { Type = StructureType.SwapchainCreateInfo,
                    UsageFlags = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.TransferDstBit,
                    Format = (long)V.Format.R8G8B8A8Srgb, SampleCount = 1,
                    Width = (uint)width, Height = (uint)height, FaceCount = 1, ArraySize = 1, MipCount = 1 };
                Swapchain chain;
                Check(api.CreateSwapchain(session, &info, &chain), "xrCreateSwapchain");
                swapchains[eye] = chain;
                Check(api.EnumerateSwapchainImages(chain, 0, &count, null), "xrEnumerateSwapchainImages");
                images[eye] = new SwapchainImageVulkanKHR[count];
                for (int index = 0; index < count; index++)
                    images[eye][index] = new SwapchainImageVulkanKHR { Type = StructureType.SwapchainImageVulkanKhr };
                fixed (SwapchainImageVulkanKHR* output = images[eye])
                    Check(api.EnumerateSwapchainImages(chain, count, &count, (SwapchainImageBaseHeader*)output), "xrEnumerateSwapchainImages");
            }
            Width = width; Height = height;
        }
        catch { DestroySwapchains(); throw; }
    }

    internal void PollEvents()
    {
        if (terminal || session.Handle == 0) return;
        while (true)
        {
            var buffer = new EventDataBuffer { Type = StructureType.EventDataBuffer };
            Result result = api.PollEvent(instance, &buffer);
            if (result == Result.EventUnavailable) return;
            if (Lost(result)) return;
            Check(result, "xrPollEvent");
            if (buffer.Type == StructureType.EventDataInstanceLossPending)
            {
                terminal = true; Running = false;
                Status = RuntimeName + ": runtime disconnected; restart VR after reconnecting.";
                return;
            }
            if (buffer.Type == StructureType.EventDataReferenceSpaceChangePending)
            {
                var referenceChange = (EventDataReferenceSpaceChangePending*)&buffer;
                if (referenceChange->Session.Handle == session.Handle && referenceChange->ReferenceSpaceType == ReferenceSpaceType.Local)
                    referenceChangeTime = referenceChange->ChangeTime;
            }
            if (buffer.Type != StructureType.EventDataSessionStateChanged) continue;
            var change = (EventDataSessionStateChanged*)&buffer;
            if (change->Session.Handle != session.Handle) continue;
            state = change->State;
            if (state == SessionState.Ready && !Running)
            {
                var begin = new SessionBeginInfo { Type = StructureType.SessionBeginInfo,
                    PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo };
                Check(api.BeginSession(session, &begin), "xrBeginSession");
                Running = true;
            }
            else if (state == SessionState.Stopping)
            {
                Check(api.EndSession(session), "xrEndSession");
                Running = false;
            }
            else if (state is SessionState.Exiting or SessionState.LossPending)
            {
                Running = false; terminal = true;
            }
            Status = RuntimeName + ": " + state + "; stereo " + Width + "x" + Height + " per eye.";
        }
    }

    internal void BeginFrame(int width, int height)
    {
        ShouldRender = false;
        if (!Running || terminal) return;
        EnsureSwapchains(width, height);
        var wait = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        var frame = new FrameState { Type = StructureType.FrameState };
        Result result = api.WaitFrame(session, &wait, &frame);
        if (Lost(result)) return;
        Check(result, "xrWaitFrame");
        var begin = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        lock (context!.QueueLock) result = api.BeginFrame(session, &begin);
        if (Lost(result)) return;
        Check(result, "xrBeginFrame");
        frameOpen = true;
        displayTime = frame.PredictedDisplayTime;
        if (displayTime >= referenceChangeTime) { ReferenceRevision++; referenceChangeTime = long.MaxValue; }
        Array.Clear(copied);
        if (frame.ShouldRender == 0) return;
        for (int eye = 0; eye < 2; eye++) views[eye] = new View { Type = StructureType.View };
        var locate = new ViewLocateInfo { Type = StructureType.ViewLocateInfo,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo, DisplayTime = displayTime, Space = space };
        var viewState = new ViewState { Type = StructureType.ViewState };
        uint count;
        fixed (View* output = views)
            Check(api.LocateView(session, &locate, &viewState, 2, &count, output), "xrLocateViews");
        const ViewStateFlags valid = ViewStateFlags.OrientationValidBit | ViewStateFlags.PositionValidBit;
        if (count != 2 || (viewState.ViewStateFlags & valid) != valid) return;
        for (int eye = 0; eye < 2; eye++)
        {
            var acquire = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
            uint index;
            lock (context.QueueLock) Check(api.AcquireSwapchainImage(swapchains[eye], &acquire, &index), "xrAcquireSwapchainImage");
            acquired[eye] = index; imageHeld[eye] = true;
            var imageWait = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = long.MaxValue };
            Check(api.WaitSwapchainImage(swapchains[eye], &imageWait), "xrWaitSwapchainImage");
        }
        ShouldRender = true;
    }

    internal OpenXrEyeView Eye(int eye)
    {
        View view = views[eye];
        return new(new Vector3(view.Pose.Position.X, view.Pose.Position.Y, view.Pose.Position.Z),
            new Quaternion(view.Pose.Orientation.X, view.Pose.Orientation.Y, view.Pose.Orientation.Z, view.Pose.Orientation.W),
            view.Fov.AngleLeft, view.Fov.AngleRight, view.Fov.AngleUp, view.Fov.AngleDown);
    }

    internal V.Image Image(int eye) => new(images[eye][acquired[eye]].Image);
    internal void MarkCopied(int eye) => copied[eye] = true;
    internal void SuppressFrame() => ShouldRender = false;

    // Called after the renderer has submitted both copies to the binding's graphics
    // queue. xrEndFrame shares that queue's external-synchronization lock.
    internal void EndFrame()
    {
        if (!frameOpen) return;
        for (int eye = 0; eye < 2; eye++)
        {
            if (!imageHeld[eye]) continue;
            var release = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
            Result released;
            lock (context!.QueueLock) released = api.ReleaseSwapchainImage(swapchains[eye], &release);
            if (!Lost(released)) Check(released, "xrReleaseSwapchainImage");
            imageHeld[eye] = false;
        }
        var projectionViews = stackalloc CompositionLayerProjectionView[2];
        for (int eye = 0; eye < 2; eye++)
            projectionViews[eye] = new CompositionLayerProjectionView { Type = StructureType.CompositionLayerProjectionView,
                Pose = views[eye].Pose, Fov = views[eye].Fov,
                SubImage = new SwapchainSubImage { Swapchain = swapchains[eye],
                    ImageRect = new Rect2Di { Extent = new Extent2Di { Width = Width, Height = Height } } } };
        var projection = new CompositionLayerProjection { Type = StructureType.CompositionLayerProjection,
            Space = space, ViewCount = 2, Views = projectionViews };
        CompositionLayerBaseHeader* layer = (CompositionLayerBaseHeader*)&projection;
        var end = new FrameEndInfo { Type = StructureType.FrameEndInfo, DisplayTime = displayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
            LayerCount = ShouldRender && copied[0] && copied[1] ? 1u : 0u, Layers = &layer };
        Result ended;
        lock (context!.QueueLock) ended = api.EndFrame(session, &end);
        if (!Lost(ended)) Check(ended, "xrEndFrame");
        frameOpen = false; ShouldRender = false;
    }

    private void DestroySwapchains()
    {
        for (int eye = 0; eye < 2; eye++)
        {
            if (swapchains[eye].Handle != 0) Check(api.DestroySwapchain(swapchains[eye]), "xrDestroySwapchain");
            swapchains[eye] = default; images[eye] = [];
        }
        Width = Height = 0;
    }

    public void Dispose()
    {
        if (context != null)
            lock (context.QueueLock) context.Api.DeviceWaitIdle(context.Device);
        if (frameOpen) { ShouldRender = false; EndFrame(); }
        DestroySwapchains();
        if (space.Handle != 0) { Check(api.DestroySpace(space), "xrDestroySpace"); space = default; }
        if (session.Handle != 0) { Check(api.DestroySession(session), "xrDestroySession"); session = default; }
        vulkan?.Dispose(); vulkan = null;
        if (instance.Handle != 0) { Check(api.DestroyInstance(instance), "xrDestroyInstance"); instance = default; }
        api.Dispose();
    }

    private static void Check(Result result, string operation)
    {
        if ((int)result < 0) throw new InvalidOperationException(operation + " failed: " + result);
    }

    private bool Lost(Result result)
    {
        if (result is not (Result.ErrorInstanceLost or Result.ErrorSessionLost)) return false;
        terminal = true; Running = ShouldRender = false;
        Status = RuntimeName + ": " + result + "; desktop presentation remains active.";
        return true;
    }
}
