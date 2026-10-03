using System;
using System.Globalization;
using System.IO;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// A trace of what the device was actually asked to draw, for the cases where
/// the frame is legal - the validation layer says nothing - but wrong.
///
/// Off unless VULKANSTORY_RENDER_TRACE names a file, so it costs one static bool
/// check in a release build and never appears in a normal session. It is a
/// debugging aid rather than diagnostics the game consumes: the client's own
/// error channel carries validation messages already.
/// </summary>
internal static partial class RenderTrace
{
    private static readonly object Gate = new();
    private static readonly string? Path = Environment.GetEnvironmentVariable("VULKANSTORY_RENDER_TRACE");

    public static bool Enabled => Path != null;

    public static void Write(string line)
    {
        if (Path == null) return;
        lock (Gate)
        {
            File.AppendAllText(Path, line + "\n");
        }
    }

    /// <summary>
    /// Records a texture upload along with a checksum of its first rows, which
    /// is what distinguishes "the image never got the pixels" from "the image is
    /// correct but never sampled".
    /// </summary>
    public static unsafe void TextureCreated(
        int id, int width, int height, Format format, IntPtr pixels, int bytesPerPixel)
    {
        if (Path == null) return;

        long sum = 0;
        int nonZero = 0;
        if (pixels != IntPtr.Zero && bytesPerPixel > 0)
        {
            int sampled = Math.Min(width * height * bytesPerPixel, 64 * 1024);
            byte* bytes = (byte*)pixels;
            for (int i = 0; i < sampled; i++)
            {
                sum += bytes[i];
                if (bytes[i] != 0) nonZero++;
            }
        }

        Write(string.Format(CultureInfo.InvariantCulture,
            "tex create id={0} {1}x{2} format={3} bpp={4} bytesum={5} nonzero={6}",
            id, width, height, format, bytesPerPixel, sum, nonZero));
    }

    public static void Draw(int meshId, int programId, int indexCount, bool depthTest, bool blend, float depthRangeHint)
    {
        if (Path == null) return;

        Write(string.Format(CultureInfo.InvariantCulture,
            "draw mesh={0} program={1} indices={2} depthTest={3} blend={4} z={5}",
            meshId, programId, indexCount, depthTest, blend, depthRangeHint));
    }
}
