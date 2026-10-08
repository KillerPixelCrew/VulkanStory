using VulkanStory.Platform.Sdl;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Exercises the retained device owner rather than constructing its managers separately.</summary>
public static class VulkanDevicePreflight
{
    /// <summary>Runs the explicit device creation and destruction preflight.</summary>
    /// <remarks>This method creates native resources and may submit GPU work; it is an opt-in validation entry point.</remarks>
    /// <returns>Null on successful completion, otherwise the failure detail.</returns>
    public static string? Check()
    {
        try
        {
            using SdlWindowHost window = SdlWindowHost.Create(
                "VulkanStory device preflight", 128, 96, hidden: true);
            // Reverse using disposal keeps the native window alive through GPU teardown.
            using var device = new VulkanDevice
            {
                EnableStreamline = false,
                NativeShadersEnabled = false,
                SynchronousPipelines = true,
            };
            if (!device.Initialize(window, 128, 96, out string reason)) return reason;
            byte[][] colors = [[255, 0, 0, 255], [0, 255, 0, 255],
                [0, 0, 255, 255], [255, 255, 0, 255]];
            for (int frame = 0; frame < colors.Length; frame++)
            {
                byte[] expected = colors[frame];
                device.BeginFrame();
                device.ClearNativeColor(PassDeclaration.DefaultFramebuffer, 0, expected[0] / 255f,
                    expected[1] / 255f, expected[2] / 255f, 1f);
                var capture = device.ReadTextureForParity(device.DefaultColorTextureId);
                if (capture?.Bytes is not { } pixels || capture.Width != 128 || capture.Height != 96)
                    return "device frame " + frame + " returned no matching RGBA capture";
                int offset = (48 * capture.Width + 64) * 4;
                for (int channel = 0; channel < 4; channel++)
                    if (Math.Abs(pixels[offset + channel] - expected[channel]) > 2)
                        return "device frame " + frame + " capture channel " + channel +
                            " was " + pixels[offset + channel] + ", expected " + expected[channel];
                device.Present();
                string errors = device.GetError();
                if (!string.IsNullOrEmpty(errors)) return "device frame " + frame + ": " + errors;
                Console.WriteLine("device frame=" + frame + " center=" +
                    string.Join(',', expected) + " readback/present passed");
            }
            return null;
        }
        catch (Exception error)
        {
            return "retained device preflight failed: " + error;
        }
    }
}
