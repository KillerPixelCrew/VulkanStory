using System;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Xunit;
using Xunit.Abstractions;

using Image = Silk.NET.Vulkan.Image;

namespace Optimum.Render.Vulkan.Tests;

[Collection("Hidden Vulkan Window")]
public sealed class Fsr3ProxyTests(ITestOutputHelper output)
{
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [SkippableFact]
    public unsafe void HiddenSurfaceCreatesFidelityFxProxyAndEnumeratesImages()
    {
        Skip.If(Environment.GetEnvironmentVariable("OPTIMUM_STREAMLINE") != "0",
            "The standalone AMD-first probe runs in a separate process with Streamline disabled.");
        string directory = Path.GetDirectoryName(typeof(Fsr3Native).Assembly.Location)!;
        Skip.IfNot(File.Exists(Path.Combine(directory, "amd_fidelityfx_vk.dll")) &&
            File.Exists(Path.Combine(directory, "OptimumFsr3.dll")), "FidelityFX Vulkan runtime unavailable.");
        Window* window = null;
        VulkanContext? context = null;
        SurfaceKHR surface = default;
        Fsr3SwapchainRuntime? proxy = null;
        uint previousErrorMode = SetErrorMode(0x0002);
        try
        {
            Skip.IfNot(GLFW.Init() && GLFW.VulkanSupported(), "Vulkan window system unavailable.");
            GLFW.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            GLFW.WindowHint(WindowHintBool.Visible, false);
            window = GLFW.CreateWindow(128, 96, "FidelityFX hidden proxy probe", null, null);
            Skip.If(window == null, "Hidden window unavailable.");

            var options = new VulkanContextOptions
            {
                RequiredInstanceExtensions = WindowSurface.RequiredInstanceExtensions(),
                EnableValidation = false,
                PrepareFrameGenerationQueues = true,
            };
            Assert.True(VulkanContext.TryCreate(options, out context, out string? reason), reason);
            Assert.NotNull(context);
            Assert.True(WindowSurface.TryCreate(context!, (nint)window, out surface, out reason), reason);
            Assert.True(context!.Api.TryGetInstanceExtension(context.Instance, out KhrSurface surfaceApi));
            using (surfaceApi)
            {
                Assert.Equal(Result.Success, surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                    context.PhysicalDevice, surface, out SurfaceCapabilitiesKHR caps));
                uint formatCount = 0;
                Assert.Equal(Result.Success, surfaceApi.GetPhysicalDeviceSurfaceFormats(
                    context.PhysicalDevice, surface, ref formatCount, null));
                var formats = new SurfaceFormatKHR[formatCount];
                fixed (SurfaceFormatKHR* formatsPtr = formats)
                    Assert.Equal(Result.Success, surfaceApi.GetPhysicalDeviceSurfaceFormats(
                        context.PhysicalDevice, surface, ref formatCount, formatsPtr));
                Assert.NotEmpty(formats);
                SurfaceFormatKHR format = formats[0];
                foreach (SurfaceFormatKHR candidate in formats)
                    if (candidate.Format == Format.B8G8R8A8Unorm) { format = candidate; break; }
                uint imageCount = caps.MinImageCount + 1;
                if (caps.MaxImageCount != 0) imageCount = Math.Min(imageCount, caps.MaxImageCount);
                var info = new SwapchainCreateInfoKHR
                {
                    SType = StructureType.SwapchainCreateInfoKhr,
                    Surface = surface,
                    MinImageCount = imageCount,
                    ImageFormat = format.Format,
                    ImageColorSpace = format.ColorSpace,
                    ImageExtent = caps.CurrentExtent.Width == uint.MaxValue ?
                        new Extent2D(128, 96) : caps.CurrentExtent,
                    ImageArrayLayers = 1,
                    ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
                    ImageSharingMode = SharingMode.Exclusive,
                    PreTransform = caps.CurrentTransform,
                    CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                    PresentMode = PresentModeKHR.FifoKhr,
                    Clipped = true,
                };
                Assert.True(Fsr3SwapchainRuntime.TryCreate(context, &info, out proxy, out reason), reason);
                Assert.NotNull(proxy);
                Assert.NotEqual(0UL, proxy!.Handle.Handle);
                uint count = 0;
                Assert.Equal(Result.Success, proxy.GetImages(context.Device, proxy.Handle, ref count, null));
                Assert.True(count >= caps.MinImageCount);
                Image[] images = new Image[count];
                fixed (Image* imagesPtr = images)
                    Assert.Equal(Result.Success, proxy.GetImages(context.Device, proxy.Handle, ref count, imagesPtr));
                Assert.All(images, image => Assert.NotEqual(0UL, image.Handle));
                var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
                Assert.Equal(Result.Success, context.Api.CreateFence(
                    context.Device, &fenceInfo, null, out Fence acquired));
                try
                {
                    uint imageIndex = 0;
                    Assert.Equal(Result.Success, proxy.Acquire(context.Device, proxy.Handle,
                        default, acquired, ref imageIndex));
                    Assert.Equal(Result.Success, context.Api.WaitForFences(context.Device,
                        1, &acquired, true, ulong.MaxValue));
                    Assert.InRange(imageIndex, 0u, count - 1);
                    SwapchainKHR handle = proxy.Handle;
                    var present = new PresentInfoKHR
                    {
                        SType = StructureType.PresentInfoKhr,
                        SwapchainCount = 1,
                        PSwapchains = &handle,
                        PImageIndices = &imageIndex,
                    };
                    Result presented = proxy.Present(context.GraphicsQueue, &present);
                    Assert.True(presented is Result.Success or Result.SuboptimalKhr, presented.ToString());
                    Assert.Equal(Result.Success, context.WaitDeviceIdle());
                    proxy.DestroyChain(proxy.Handle);
                    Assert.Equal(0UL, proxy.Handle.Handle);
                    Assert.Equal(Result.Success, proxy.Recreate(&info, out SwapchainKHR rebuilt));
                    Assert.NotEqual(0UL, rebuilt.Handle);
                    uint rebuiltCount = 0;
                    Assert.Equal(Result.Success, proxy.GetImages(context.Device, rebuilt,
                        ref rebuiltCount, null));
                    Assert.True(rebuiltCount >= caps.MinImageCount);
                }
                finally
                {
                    context.Api.DestroyFence(context.Device, acquired, null);
                }
                output.WriteLine("FidelityFX proxy exposed " + count + " swapchain images.");
            }
        }
        finally
        {
            proxy?.Dispose();
            if (context != null && surface.Handle != 0) WindowSurface.Destroy(context, surface);
            context?.Dispose();
            if (window != null) GLFW.DestroyWindow(window);
            GLFW.Terminate();
            SetErrorMode(previousErrorMode);
        }
    }
}
