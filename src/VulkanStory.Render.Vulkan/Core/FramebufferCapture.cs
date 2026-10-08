using VulkanStory.Contracts;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>GL unsigned-byte BGRA output from the retained neutral decoder.</summary>
internal static class FramebufferCapture
{
    /// <summary>Converts supported raw texture-capture pixels to tightly packed BGRA8 screenshot bytes.</summary>
    internal static byte[] Bgra8(TextureCaptureData capture)
    {
        if (capture.Width < 0 || capture.Height < 0) throw new ArgumentOutOfRangeException(nameof(capture));
        int length = checked(capture.Width * capture.Height * 4);
        var result = new byte[length];
        if (capture.Bytes is byte[] bytes && bytes.Length >= length)
        {
            for (int offset = 0; offset < length; offset += 4)
            {
                result[offset] = bytes[offset + 2]; result[offset + 1] = bytes[offset + 1];
                result[offset + 2] = bytes[offset]; result[offset + 3] = bytes[offset + 3];
            }
        }
        else if (capture.Floats is float[] floats && floats.Length >= length)
        {
            static byte Channel(float value) => float.IsNaN(value) ? (byte)0 : (byte)(Math.Clamp(value, 0f, 1f) * 255f);
            for (int offset = 0; offset < length; offset += 4)
            {
                result[offset] = Channel(floats[offset + 2]); result[offset + 1] = Channel(floats[offset + 1]);
                result[offset + 2] = Channel(floats[offset]); result[offset + 3] = Channel(floats[offset + 3]);
            }
        }
        else throw new NotSupportedException("Framebuffer capture needs four decoded color channels per texel.");
        return result;
    }
}
