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
    private Space headSpace;
    private VulkanContext? context;
    private readonly View[] views = new View[2];
    private readonly ViewConfigurationView[] configuration = new ViewConfigurationView[2];
    private readonly Swapchain[] swapchains = new Swapchain[2];
    private readonly SwapchainImageVulkanKHR[][] images = new SwapchainImageVulkanKHR[2][];
    private readonly uint[] acquired = new uint[2];
    private readonly bool[] imageHeld = new bool[2];
    private readonly bool[] imageReady = new bool[2];
    private readonly bool[] copied = new bool[2];
    private bool frameOpen, terminal;
    private long displayTime;
    private long previousDisplayTime;
    private bool projectionFrame;
    private SessionState state;
    private long referenceChangeTime = long.MaxValue;
    internal int ReferenceRevision { get; private set; }
    internal bool Running { get; private set; }
    internal bool ShouldRender { get; private set; }
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal V.Format ColorFormat { get; private set; }
    internal V.Format TransferFormat => ColorFormat == V.Format.B8G8R8A8Srgb ? V.Format.B8G8R8A8Unorm : V.Format.R8G8B8A8Unorm;
    internal float? FrameDeltaSeconds { get; private set; }
    internal string Status { get; private set; } = "OpenXR is starting.";
    internal string RuntimeName { get; private set; } = "";
    internal string FrameStatus { get; private set; } = "Waiting for session readiness.";

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
        // TODO(VR-01): enable optional XR_EXT_debug_utils names/messages when
        // renderer diagnostics are requested, so a headset failure identifies its XR object.
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

    internal void Attach(VulkanContext device)
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
        reference.ReferenceSpaceType = ReferenceSpaceType.View;
        Check(api.CreateReferenceSpace(session, &reference, &local), "xrCreateReferenceSpace (head-locked menu)");
        headSpace = local;
        CreateSwapchains();
        Status = RuntimeName + ": session created; waiting for headset readiness.";
    }

    private void CreateSwapchains()
    {
        // Both views share the game's raster targets. Choose a common extent from
        // the runtime's recommendations, bounded by both views and the actual GPU.
        // Desktop resize/minimize must never recreate headset swapchains.
        uint limit = context!.Capabilities.MaxImageDimension2D;
        int width = checked((int)Math.Min(limit, Math.Min(
            Math.Min(configuration[0].MaxImageRectWidth, configuration[1].MaxImageRectWidth),
            Math.Max(configuration[0].RecommendedImageRectWidth, configuration[1].RecommendedImageRectWidth))));
        int height = checked((int)Math.Min(limit, Math.Min(
            Math.Min(configuration[0].MaxImageRectHeight, configuration[1].MaxImageRectHeight),
            Math.Max(configuration[0].RecommendedImageRectHeight, configuration[1].RecommendedImageRectHeight))));
        if (width <= 0 || height <= 0) throw new NotSupportedException("OpenXR reported an empty recommended eye extent.");
        uint count;
        Check(api.EnumerateSwapchainFormats(session, 0, &count, null), "xrEnumerateSwapchainFormats");
        var formats = new long[count];
        fixed (long* values = formats)
            Check(api.EnumerateSwapchainFormats(session, count, &count, values), "xrEnumerateSwapchainFormats");
        // The game's final colour is gamma encoded. A raw copy into an sRGB XR image
        // preserves that encoding, instead of applying a second gamma conversion.
        foreach (V.Format candidate in new[] { V.Format.R8G8B8A8Srgb, V.Format.B8G8R8A8Srgb })
        {
            if (!formats.Contains((long)candidate)) continue;
            V.Format transfer = candidate == V.Format.B8G8R8A8Srgb ? V.Format.B8G8R8A8Unorm : V.Format.R8G8B8A8Unorm;
            context.Api.GetPhysicalDeviceFormatProperties(context.PhysicalDevice, transfer, out V.FormatProperties properties);
            const V.FormatFeatureFlags required = V.FormatFeatureFlags.BlitDstBit | V.FormatFeatureFlags.TransferSrcBit |
                V.FormatFeatureFlags.TransferDstBit | V.FormatFeatureFlags.SampledImageBit | V.FormatFeatureFlags.ColorAttachmentBit;
            if ((properties.OptimalTilingFeatures & required) == required) { ColorFormat = candidate; break; }
        }
        if (ColorFormat == V.Format.Undefined)
            throw new NotSupportedException("OpenXR and the GPU provide no compatible RGBA/BGRA sRGB eye/transfer format pair.");
        // TODO(VR-01): add linear/HDR outputs with an explicit colour-space conversion,
        // rather than treating a UNORM/HDR swapchain as gamma-encoded desktop bytes.
        try
        {
            for (int eye = 0; eye < 2; eye++)
            {
                var info = new SwapchainCreateInfo { Type = StructureType.SwapchainCreateInfo,
                    UsageFlags = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.TransferDstBit,
                    Format = (long)ColorFormat, SampleCount = 1,
                    Width = (uint)width, Height = (uint)height, FaceCount = 1, ArraySize = 1, MipCount = 1 };
                Swapchain chain;
                Check(api.CreateSwapchain(session, &info, &chain), "xrCreateSwapchain");
                swapchains[eye] = chain;
                Check(api.EnumerateSwapchainImages(chain, 0, &count, null), "xrEnumerateSwapchainImages");
                if (count == 0) throw new InvalidOperationException("OpenXR created a swapchain without images.");
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
                previousDisplayTime = 0;
            }
            else if (state == SessionState.Stopping)
            {
                Check(api.EndSession(session), "xrEndSession");
                Running = false;
                previousDisplayTime = 0;
            }
            else if (state is SessionState.Exiting or SessionState.LossPending)
            {
                Running = false; terminal = true;
            }
            Status = RuntimeName + ": " + state + "; stereo " + Width + "x" + Height + " per eye.";
        }
    }

    internal void BeginFrame()
    {
        if (frameOpen) throw new InvalidOperationException("The previous OpenXR frame has not ended.");
        ShouldRender = false;
        FrameDeltaSeconds = null;
        if (!Running || terminal) return;
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
        long elapsed = previousDisplayTime != 0 && displayTime > previousDisplayTime
            ? displayTime - previousDisplayTime : frame.PredictedDisplayPeriod;
        previousDisplayTime = displayTime;
        if (elapsed > 0) FrameDeltaSeconds = (float)Math.Min(elapsed / 1e9, .1);
        projectionFrame = true;
        if (displayTime >= referenceChangeTime) { ReferenceRevision++; referenceChangeTime = long.MaxValue; }
        Array.Clear(copied);
        if (frame.ShouldRender == 0) { FrameStatus = "Runtime requests no rendering."; return; }
        for (int eye = 0; eye < 2; eye++) views[eye] = new View { Type = StructureType.View };
        var locate = new ViewLocateInfo { Type = StructureType.ViewLocateInfo,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo, DisplayTime = displayTime, Space = space };
        var viewState = new ViewState { Type = StructureType.ViewState };
        uint count;
        fixed (View* output = views)
            result = api.LocateView(session, &locate, &viewState, 2, &count, output);
        if (Lost(result)) return;
        Check(result, "xrLocateViews");
        const ViewStateFlags valid = ViewStateFlags.OrientationValidBit | ViewStateFlags.PositionValidBit;
        if (count != 2 || (viewState.ViewStateFlags & valid) != valid)
        { FrameStatus = "Eye tracking is not valid; submitting no scene layer."; return; }
        for (int eye = 0; eye < 2; eye++)
        {
            var acquire = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
            uint index;
            lock (context.QueueLock) result = api.AcquireSwapchainImage(swapchains[eye], &acquire, &index);
            if (Lost(result)) return;
            Check(result, "xrAcquireSwapchainImage");
            acquired[eye] = index; imageHeld[eye] = true;
            if (index >= images[eye].Length) throw new InvalidOperationException("OpenXR acquired an invalid image index.");
            if (!WaitForImage(eye)) return;
        }
        ShouldRender = true;
        FrameStatus = "Eye images acquired and ready.";
    }

    internal OpenXrEyeView Eye(int eye)
    {
        View view = views[eye];
        return new(new Vector3(view.Pose.Position.X, view.Pose.Position.Y, view.Pose.Position.Z),
            new Quaternion(view.Pose.Orientation.X, view.Pose.Orientation.Y, view.Pose.Orientation.Z, view.Pose.Orientation.W),
            view.Fov.AngleLeft, view.Fov.AngleRight, view.Fov.AngleUp, view.Fov.AngleDown);
    }

    private bool WaitForImage(int eye)
    {
        var wait = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = long.MaxValue };
        Result result = api.WaitSwapchainImage(swapchains[eye], &wait);
        if (Lost(result)) return false;
        // XR_TIMEOUT_EXPIRED is positive; XR_SUCCEEDED alone does not grant image access.
        if (result == Result.TimeoutExpired) throw new TimeoutException("The infinite OpenXR image wait expired; image ownership is retained.");
        Check(result, "xrWaitSwapchainImage");
        imageReady[eye] = true;
        return true;
    }

    internal V.Image Image(int eye) => imageReady[eye] ? new(images[eye][acquired[eye]].Image) :
        throw new InvalidOperationException("OpenXR eye image has not completed its acquire/wait sequence.");
    internal void MarkCopied(int eye) => copied[eye] = true;
    internal void UseHeadLockedMenu() => projectionFrame = false;
    internal void SuppressFrame()
    {
        ShouldRender = false;
        FrameStatus = "Scene transition/loading hold; submitting no scene layer.";
    }

    // Called after the renderer has submitted both copies to the binding's graphics
    // queue. xrEndFrame shares that queue's external-synchronization lock.
    internal void EndFrame()
    {
        if (!frameOpen) return;
        if (terminal) { ClearFrame(); return; }
        for (int eye = 0; eye < 2; eye++)
        {
            if (!imageHeld[eye]) continue;
            if (!imageReady[eye] && !WaitForImage(eye)) { ClearFrame(); return; }
            var release = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
            Result released;
            lock (context!.QueueLock) released = api.ReleaseSwapchainImage(swapchains[eye], &release);
            if (!Lost(released)) Check(released, "xrReleaseSwapchainImage");
            imageHeld[eye] = false;
            imageReady[eye] = false;
            if (terminal) { ClearFrame(); return; }
        }
        var projectionViews = stackalloc CompositionLayerProjectionView[2];
        for (int eye = 0; eye < 2; eye++)
            projectionViews[eye] = new CompositionLayerProjectionView { Type = StructureType.CompositionLayerProjectionView,
                Pose = views[eye].Pose, Fov = views[eye].Fov,
                SubImage = new SwapchainSubImage { Swapchain = swapchains[eye],
                    ImageRect = new Rect2Di { Extent = new Extent2Di { Width = Width, Height = Height } } } };
        var projection = new CompositionLayerProjection { Type = StructureType.CompositionLayerProjection,
            Space = space, ViewCount = 2, Views = projectionViews };
        // Flat screens have no 3D eye camera. A head-locked quad gives both eyes
        // one consistent menu surface instead of pretending two identical UI images
        // are correctly projected world views with different asymmetric FOVs.
        var menu = new CompositionLayerQuad { Type = StructureType.CompositionLayerQuad,
            Space = headSpace, EyeVisibility = EyeVisibility.Both,
            SubImage = projectionViews[0].SubImage,
            Pose = new Posef { Orientation = new Quaternionf { W = 1 }, Position = new Vector3f { Z = -2 } },
            Size = new Extent2Df { Width = 2.8f, Height = 2.8f * Height / Width } };
        // TODO(VR-01): separate the world HUD/inventory into XR panels and tune
        // angular UI size; orthographic overlays inside 3D eye images are still a later UI step.
        CompositionLayerBaseHeader* layer = projectionFrame ? (CompositionLayerBaseHeader*)&projection : (CompositionLayerBaseHeader*)&menu;
        var end = new FrameEndInfo { Type = StructureType.FrameEndInfo, DisplayTime = displayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
            LayerCount = ShouldRender && copied[0] && (!projectionFrame || copied[1]) ? 1u : 0u, Layers = &layer };
        Result ended;
        lock (context!.QueueLock) ended = api.EndFrame(session, &end);
        if (!Lost(ended)) Check(ended, "xrEndFrame");
        if (!terminal && end.LayerCount != 0)
            FrameStatus = projectionFrame ? "Stereo scene submitted." : "Head-locked menu submitted.";
        ClearFrame();
    }

    private void ClearFrame()
    {
        frameOpen = false; ShouldRender = false;
        Array.Clear(imageHeld); Array.Clear(imageReady); Array.Clear(copied);
    }

    private void DestroySwapchains()
    {
        for (int eye = 0; eye < 2; eye++)
        {
            if (swapchains[eye].Handle != 0) CheckDestroy(api.DestroySwapchain(swapchains[eye]), "xrDestroySwapchain");
            swapchains[eye] = default; images[eye] = [];
        }
        Width = Height = 0;
    }

    public void Dispose()
    {
        // The Vulkan device owner drains GPU work before disposing XR; its borrowed
        // images must not be destroyed while our submitted copy commands reference them.
        if (frameOpen) { ShouldRender = false; EndFrame(); }
        DestroySwapchains();
        if (headSpace.Handle != 0) { CheckDestroy(api.DestroySpace(headSpace), "xrDestroySpace (head)"); headSpace = default; }
        if (space.Handle != 0) { CheckDestroy(api.DestroySpace(space), "xrDestroySpace"); space = default; }
        if (session.Handle != 0) { CheckDestroy(api.DestroySession(session), "xrDestroySession"); session = default; }
        vulkan?.Dispose(); vulkan = null;
        if (instance.Handle != 0) { CheckDestroy(api.DestroyInstance(instance), "xrDestroyInstance"); instance = default; }
        api.Dispose();
    }

    private static void Check(Result result, string operation)
    {
        if ((int)result < 0) throw new InvalidOperationException(operation + " failed: " + result);
    }

    private void CheckDestroy(Result result, string operation)
    {
        if (terminal && (result is Result.ErrorInstanceLost or Result.ErrorSessionLost)) return;
        Check(result, operation);
    }

    private bool Lost(Result result)
    {
        if (result is not (Result.ErrorInstanceLost or Result.ErrorSessionLost)) return false;
        terminal = true; Running = ShouldRender = false;
        previousDisplayTime = 0;
        // TODO(VR-01): reconnect through a fresh XR instance/session only after
        // rechecking the Vulkan device binding; never attach a new runtime blindly.
        Status = RuntimeName + ": " + result + "; desktop presentation remains active.";
        return true;
    }
}
