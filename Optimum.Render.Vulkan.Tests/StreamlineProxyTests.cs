using System;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

[Collection("Hidden Vulkan Window")]
public class StreamlineProxyTests(ITestOutputHelper output)
{
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [SkippableFact]
    public unsafe void HiddenWindowUsesStreamlineSwapchainAndReflex()
    {
        // Streamline must see the first Vulkan instance and device created by
        // this process. Keep this probe in a dedicated testhost, and do not
        // create a preliminary VulkanContext just to inspect the adapter.
        Skip.If(Environment.GetEnvironmentVariable("OPTIMUM_STREAMLINE_PROBE") != "1",
            "Run this Streamline probe by itself with OPTIMUM_STREAMLINE_PROBE=1.");
        string directory = Path.GetDirectoryName(typeof(StreamlineRuntime).Assembly.Location)!;
        Skip.IfNot(File.Exists(Path.Combine(directory, "OptimumStreamline.dll")) &&
            File.Exists(Path.Combine(directory, "sl.interposer.dll")), "Streamline release runtime unavailable.");
        Skip.If(Environment.GetEnvironmentVariable("OPTIMUM_STREAMLINE") == "0", "Streamline explicitly disabled.");

        Window* window = null;
        VulkanDevice? device = null;
        uint previousErrorMode = SetErrorMode(0x0002);
        try
        {
            Skip.IfNot(GLFW.Init() && GLFW.VulkanSupported(), "Vulkan window system unavailable.");
            GLFW.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            GLFW.WindowHint(WindowHintBool.Visible, false);
            window = GLFW.CreateWindow(128, 96, "Streamline hidden probe", null, null);
            Skip.If(window == null, "Hidden window unavailable.");
            device = GpuTest.NewDevice();
            var previous = device.ConfigureContextOptions;
            device.ConfigureContextOptions = options =>
            {
                previous?.Invoke(options);
                options.EnableValidation = false;
                options.ValidationFeatures = "";
            };
            Assert.True(device.Initialize((nint)window, 128, 96, out string reason), reason);
            Skip.If(device.ContextForTests.Capabilities.VendorId != 0x10DE,
                "Streamline DLSS-G needs an NVIDIA GPU.");
            Assert.NotNull(device.ContextForTests.Streamline);
            Assert.True(device.StreamlineFrameGenerationReady);
            for (int i = 0; i < 3; i++)
            {
                device.BeginLatencyFrame();
                device.BeginFrame();
                device.BindDefaultFramebuffer();
                device.ClearColor(0, 0.1f, 0.2f, 0.3f, 1);
                device.Present();
                Assert.True(device.LastPresentTimingsForTests.Presented);
            }
            // The menu changes the presentation policy while the client stays
            // alive. Rebuild both ways through the same hidden proxy surface.
            foreach (string provider in new[] { "dlss", "fsr3", "off" })
            {
                device.SetFrameGenerationPresentation(provider);
                Assert.False(device.StreamlineFrameGenerationReady);
                device.BeginLatencyFrame();
                device.BeginFrame();
                device.BindDefaultFramebuffer();
                device.ClearColor(0, 0.1f, 0.2f, 0.3f, 1);
                device.Present();
                Assert.True(device.LastPresentTimingsForTests.Presented);
                if (provider == "fsr3")
                {
                    Assert.True(device.Fsr3ProxyReady, device.Fsr3ProxyFailure);
                    Assert.False(device.StreamlineFrameGenerationReady);
                }
                else
                {
                    Assert.False(device.Fsr3ProxyReady);
                    Assert.True(device.StreamlineFrameGenerationReady);
                }
            }
        }
        finally
        {
            device?.Dispose();
            if (window != null) GLFW.DestroyWindow(window);
            GLFW.Terminate();
            SetErrorMode(previousErrorMode);
        }
    }
}
