// Migrated parity format/file writers, baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
using VulkanStory.Contracts;
namespace VulkanStory.Game;

/// <summary>Writes requested color, depth and floating-point attachment dumps with explicit dimensions for renderer parity comparisons.</summary>
public static class HeadlessParityDump
{
    /// <summary>The absolute dump directory, or null when the dump is off.</summary>
    public static readonly string? Directory = ResolveDirectory();

    /// <summary>True when <c>VULKANSTORY_PARITY_DUMP</c> names an absolute directory.</summary>
    public static readonly bool Enabled = Directory != null;

    /// <summary>The in-world frame to dump, counted from 0.</summary>
    public static readonly long Frame = ResolveFrame();

    /// <summary>
    /// True when <c>VULKANSTORY_AO_OUTPUTS</c> opts in to the ambient occlusion debug outputs
    /// (docs/vulkan.md#ambient-occlusion C.13, section D): the pre-denoise working term, the
    /// packed edges, working-depth level 0 and the denoised output are written by this dump
    /// (slots 40-43) and beside every frame the headless harness captures. Compute-only
    /// textures, so no framebuffer slot holds them and the dump asks the platform for them.
    /// </summary>
    public static readonly bool AmbientOcclusionOutputs = ResolveAmbientOcclusionOutputs();

    private static bool ResolveAmbientOcclusionOutputs()
    {
        string? value = Environment.GetEnvironmentVariable("VULKANSTORY_AO_OUTPUTS");
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        return value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one file-name format both backends use:
    /// <c>&lt;slotIndex&gt;-&lt;slotName&gt;-&lt;color&lt;i&gt;|depth&gt;-&lt;format&gt;.&lt;ext&gt;</c>.
    /// </summary>
    public const string FileNameFormat = "{0}-{1}-{2}-{3}.{4}";

    private static string? ResolveDirectory()
    {
        string? value = Environment.GetEnvironmentVariable("VULKANSTORY_PARITY_DUMP");
        if (string.IsNullOrWhiteSpace(value) || !System.IO.Path.IsPathRooted(value)) return null;
        return System.IO.Path.GetFullPath(value);
    }

    private static long ResolveFrame()
    {
        string? value = Environment.GetEnvironmentVariable("VULKANSTORY_PARITY_FRAME");
        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long frame) && frame >= 0 ? frame : 0;
    }

    /// <summary>Formats a backend-neutral attachment filename so captures from different rendering paths pair by name.</summary>
    /// <param name="slotIndex">Retained target/dump slot number.</param>
    /// <param name="slotName">Target name used by both comparison paths.</param>
    /// <param name="attachment">Color/depth attachment label.</param>
    /// <param name="format">Backend-neutral format name.</param>
    /// <param name="extension">Dump file extension without a leading dot.</param>
    /// <returns>Invariant attachment filename matching FileNameFormat.</returns>
    public static string FileName(int slotIndex, string slotName, string attachment, string format, string extension)
    {
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, FileNameFormat,
            slotIndex, slotName, attachment, format, extension);
    }

    /// <summary>
    /// The backend-neutral format name. Aliases that denote the same storage
    /// collapse to one name (GL reports GL_RGB8 for a texture requested as
    /// GL_RGB; the Vulkan device promotes it to RGBA8 storage but reports the
    /// requested token), so the two dumps pair by file name.
    /// </summary>
    /// <param name="glInternalFormat">Retained original internal-format token.</param>
    /// <returns>Canonical storage name, or a gl-prefixed hexadecimal token for unknown formats.</returns>
    public static string FormatName(int glInternalFormat)
    {
        switch (glInternalFormat)
        {
            case 0x8058: case 0x1908: return "rgba8";      // GL_RGBA8, GL_RGBA
            case 0x8051: case 0x1907: return "rgb8";       // GL_RGB8, GL_RGB
            case 0x8229: case 0x1903: return "r8";         // GL_R8, GL_RED
            case 0x881A: return "rgba16f";
            case 0x881B: return "rgb16f";
            case 0x822D: return "r16f";
            case 0x822E: return "r32f";
            case 0x8814: return "rgba32f";
            case 0x8815: return "rgb32f";
            case 0x8C3A: return "r11g11b10f";
            case 0x805B: return "rgba16";
            case 0x1902: case 0x81A5: case 0x81A6: case 0x81A7: case 0x8CAC: return "depth";
            default: return "gl" + glInternalFormat.ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static bool IsDepthFormat(int glInternalFormat) => FormatName(glInternalFormat) == "depth";

    /// <summary>True for the 8-bit unsigned-normalised formats, which dump as PPM/PGM.</summary>
    public static bool IsUnorm8Format(int glInternalFormat)
    {
        string name = FormatName(glInternalFormat);
        return name == "rgba8" || name == "rgb8" || name == "r8";
    }

    /// <summary>Stored channels: 4, 3 or 1.</summary>
    /// <param name="glInternalFormat">Retained original internal-format token.</param>
    /// <returns>Stored dump channel count: one, three or four.</returns>
    public static int ChannelsOf(int glInternalFormat)
    {
        string name = FormatName(glInternalFormat);
        if (name == "depth" || name == "r8" || name == "r16f" || name == "r32f") return 1;
        if (name.StartsWith("rgba", StringComparison.Ordinal)) return 4;
        if (name.StartsWith("rgb", StringComparison.Ordinal) || name == "r11g11b10f") return 3;
        return 4;
    }

    /// <summary>
    /// Writes one attachment's files and returns how many were written (0 when the
    /// readback is malformed).
    /// </summary>
    /// <param name="directory">Output directory for requested attachment files.</param>
    /// <param name="slotIndex">Retained framebuffer/dump slot number.</param>
    /// <param name="slotName">Target name used to pair captures.</param>
    /// <param name="attachment">Color/depth attachment label.</param>
    /// <param name="readback">Owned readback dimensions, format and byte/float storage.</param>
    /// <returns>Number of files written; zero for unsupported or malformed input.</returns>
    public static int Write(string directory, int slotIndex, string slotName, string attachment,
        TextureCaptureData readback)
    {
        if (directory == null || readback == null || readback.Width <= 0 || readback.Height <= 0) return 0;
        int texels = readback.Width * readback.Height;
        string format = FormatName(readback.GlInternalFormat);
        int channels = ChannelsOf(readback.GlInternalFormat);
        System.IO.Directory.CreateDirectory(directory);

        if (readback.Bytes != null)
        {
            if (readback.Bytes.Length < texels * 4) return 0;
            if (channels == 1)
            {
                WriteNetpbm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "pgm")),
                    readback.Width, readback.Height, readback.Bytes, 0, 1);
                return 1;
            }
            WriteNetpbm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "ppm")),
                readback.Width, readback.Height, readback.Bytes, 0, 3);
            if (channels < 4) return 1;
            WriteNetpbm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "pgm")),
                readback.Width, readback.Height, readback.Bytes, 3, 1);
            return 2;
        }

        if (readback.Floats == null) return 0;
        int stride = readback.Floats.Length >= texels * 4 ? 4 : 1;
        if (readback.Floats.Length < texels * stride) return 0;
        if (stride == 1 || channels == 1)
        {
            WritePfm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "pfm")),
                readback.Width, readback.Height, readback.Floats, stride, 0, 1);
            return 1;
        }
        WritePfm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "pfm")),
            readback.Width, readback.Height, readback.Floats, 4, 0, 3);
        if (channels < 4) return 1;
        WritePfm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "alpha.pfm")),
            readback.Width, readback.Height, readback.Floats, 4, 3, 1);
        return 2;
    }

    /// <summary>
    /// Writes one whole frame as a binary PPM (P6) - the encoding
    /// <c>scripts/dev/ssim.py</c> reads, so two captures pair by file name.
    ///
    /// <paramref name="pixels" /> is four bytes per texel in GL row order
    /// (bottom-up, the first row in the file is GL row 0), exactly as
    /// <c>ReadDefaultFramebuffer</c> hands it back. <paramref name="bgra" /> says
    /// which order those four are in: the OpenGL path reads <c>GL_BGRA</c>, the
    /// Vulkan device's default colour target is <c>R8G8B8A8_UNORM</c> and its
    /// readback hands the texels back untouched, so the two backends differ here
    /// and the written file must not.
    ///
    /// Returns false when the arguments do not describe a frame; it never throws
    /// for that reason alone.
    /// </summary>
    /// <param name="path">Output pathname.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="pixels">Tightly packed four-channel pixels in the retained capture row order.</param>
    /// <param name="bgra">True for BGRA source order; false for RGBA. The file contains RGB without alpha.</param>
    /// <returns>True after writing; false when dimensions/storage are malformed.</returns>
    /// <remarks>File errors propagate.</remarks>
    public static bool WriteFrame(string path, int width, int height, byte[] pixels, bool bgra) =>
        HeadlessHarnessOptions.WriteFrame(path, width, height, pixels, bgra);

    private static void WriteNetpbm(string path, int width, int height, byte[] rgba, int firstChannel, int channels, int step = 1) =>
        HeadlessHarnessOptions.WriteNetpbm(path, width, height, rgba, firstChannel, channels, step);

    private static void WritePfm(string path, int width, int height, float[] data, int stride,
        int firstChannel, int channels)
    {
        using var file = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            (channels == 3 ? "PF\n" : "Pf\n") + width + " " + height + "\n-1.0\n");
        file.Write(header, 0, header.Length);
        byte[] row = new byte[width * channels * 4];
        for (int y = 0; y < height; y++)
        {
            int source = y * width * stride;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                        row.AsSpan((x * channels + c) * 4, 4), data[source + x * stride + firstChannel + c]);
                }
            }
            file.Write(row, 0, row.Length);
        }
    }
}
