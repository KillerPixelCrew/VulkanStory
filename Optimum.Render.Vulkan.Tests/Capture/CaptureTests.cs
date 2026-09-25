// Source: Optimum.Render.Vulkan.Tests/HeadlessCaptureTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The headless render harness's capture path on a device that has no surface at
/// all - no window, no swapchain, <c>Initialize(IntPtr.Zero, ...)</c>.
///
/// This is the claim the harness rests on: the frames it writes come from
/// <see cref="VulkanDevice.ReadDefaultFramebuffer" />, the same polymorphic call
/// the in-game screenshot makes, which is a device-side copy of whatever target
/// is bound - never an OS window capture. So a window that was never mapped (and,
/// here, a device with no surface whatsoever) still produces frames.
///
/// Several consecutive frames are rendered with a Present between them and a
/// per-frame value baked into the pattern, so a capture that silently reused one
/// frame, or wrote the same file every time, fails. The files are decoded back
/// and checked byte for byte in GL row order; the size is odd and non-square so a
/// transposed or flipped image cannot pass, and one channel pair is asymmetric so
/// the BGRA/RGBA distinction the writer takes as an argument is real.
/// </summary>
public class HeadlessCaptureTests
{
    private const int Width = 11;
    private const int Height = 5;
    private const int Frames = 4;

    private readonly ITestOutputHelper _output;

    public HeadlessCaptureTests(ITestOutputHelper output) => _output = output;

    private const string Vertex = """
        #version 330 core
        void main() {
            gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                               -1 + ((gl_VertexID & 2) << 1), 0, 1);
        }
        """;

    // Red carries x, green carries the frame index, blue carries y: three
    // distinguishable axes, so a transpose, a flip, a stale frame and a channel
    // swap each break a different assertion.
    private const string Fragment = """
        #version 330 core
        uniform float frameIndex;
        layout(location = 0) out vec4 color;
        void main() {
            int x = int(gl_FragCoord.x);
            int y = int(gl_FragCoord.y);
            color = vec4(float(x * 23) / 255.0, (frameIndex * 40.0) / 255.0,
                         float(y * 37) / 255.0, 1.0);
        }
        """;

    [SkippableFact]
    public void OffscreenModeProducesFramesWithoutASurface()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        string directory = Path.Combine(Path.GetTempPath(), "optimum-headless-tests-" + Guid.NewGuid().ToString("N"));
        long[] plan = OptimumHeadless.PlanFrames(0, Frames, 1);
        try
        {
            using (device)
            {
                VulkanDevice seam = device!;
                int program = GpuTest.LinkProgram(seam, Vertex, Fragment, "headless-capture");

                int color = seam.CreateTexture2D(Width, Height, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int framebuffer = seam.CreateFramebuffer(Width, Height);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, color, 0);
                seam.SetDrawBuffers(framebuffer, 1);
                Assert.True(seam.CheckFramebufferComplete(framebuffer, out string status), status);

                byte[] pixels = new byte[Width * Height * 4];
                for (long frame = 0; frame < Frames; frame++)
                {
                    Assert.True(OptimumHeadless.ShouldCapture(plan, frame));

                    seam.BeginFrame();
                    seam.BindFramebuffer(framebuffer);
                    seam.SetViewport(0, 0, Width, Height);
                    seam.SetCullFace(false);
                    seam.SetBlend(false, EnumBlendMode.Standard);
                    seam.SetDepthTest(false);
                    seam.UseProgram(program);
                    seam.SetUniform(program, seam.GetUniformLocation(program, "frameIndex"), (float)frame);
                    seam.DrawFullscreenTriangle();

                    // Exactly what ClientPlatformWindows.OptimumHeadlessCaptureFrame
                    // does: read the bound target back inside the frame, then write.
                    GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try
                    {
                        seam.ReadDefaultFramebuffer(0, 0, Width, Height, handle.AddrOfPinnedObject());
                    }
                    finally
                    {
                        handle.Free();
                    }

                    // bgra: false - this reads at the device level, where texels come
                    // back in the target's own R8G8B8A8 order. The client goes through
                    // VulkanClientPlatform.ReadDefaultFramebuffer, which converts that
                    // to the GL path's B G R A (PixelOrder.SwapRedAndBlue) and so
                    // passes bgra: true; both spellings write the same file, which is
                    // what BgraAndRgbaPixelsWriteTheSameFile holds.
                    Assert.True(OptimumParityDump.WriteFrame(
                        Path.Combine(directory, OptimumHeadless.FrameFileName(frame)),
                        Width, Height, pixels, bgra: false));

                    // No readback in the presentation path: the frame is closed the
                    // way the client closes it, so the next one is genuinely new.
                    seam.Present();
                    Assert.Equal(frame >= Frames - 1, OptimumHeadless.CaptureFinished(plan, frame));
                }

                GpuTest.AssertClean(seam);
            }

            for (long frame = 0; frame < Frames; frame++)
            {
                string path = Path.Combine(directory, OptimumHeadless.FrameFileName(frame));
                Assert.True(File.Exists(path), path + " was not written");
                byte[] rgb = ReadPpm(path);
                for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int texel = y * Width + x;
                    Assert.Equal((byte)(x * 23), rgb[texel * 3]);
                    Assert.Equal((byte)(frame * 40), rgb[texel * 3 + 1]);
                    Assert.Equal((byte)(y * 37), rgb[texel * 3 + 2]);
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// The conversion <c>VulkanClientPlatform.ReadDefaultFramebuffer</c> applies on
    /// top of the device readback.
    ///
    /// The OpenGL body of that virtual is
    /// <c>glReadPixels(..., GL_BGRA, GL_UNSIGNED_BYTE, ...)</c>, and its callers
    /// depend on it: <c>Screenshot.GrabScreenshot</c> - the screenshot key and the
    /// AVI recorder - decodes into an <c>SKBitmap</c> declared
    /// <c>SKColorType.Bgra8888</c>, and the harness writes its PPMs from the same
    /// call. The device's default colour target is R8G8B8A8, so without the swap
    /// every Vulkan screenshot came out with red and blue exchanged. A greyscale
    /// pattern cannot see that, so this one is saturated red and blue, with green
    /// and alpha left where they are to catch a rotation rather than a swap.
    /// </summary>
    [Fact]
    public unsafe void TheClientSeamTurnsTheDevicesRgbaIntoTheGlPathsBgra()
    {
        byte[] texels = [255, 17, 0, 255, 0, 34, 255, 200];
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 2);
        }

        // Red in, B G R A out - and back again, because the conversion is its own
        // inverse, which is what lets one writer serve both backends.
        Assert.Equal([0, 17, 255, 255, 255, 34, 0, 200], texels);
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 2);
        }
        Assert.Equal([255, 17, 0, 255, 0, 34, 255, 200], texels);

        // Nothing to convert is not a crash.
        PixelOrder.SwapRedAndBlue(IntPtr.Zero, 4);
        fixed (byte* data = texels)
        {
            PixelOrder.SwapRedAndBlue((IntPtr)data, 0);
            PixelOrder.SwapRedAndBlue((IntPtr)data, -1);
        }
        Assert.Equal([255, 17, 0, 255, 0, 34, 255, 200], texels);
    }

    /// <summary>
    /// The BGRA half of the same writer, on bytes rather than a GPU: the OpenGL
    /// path reads GL_BGRA and the Vulkan one RGBA, and the file must come out the
    /// same either way, or every cross-backend comparison is a red/blue swap.
    /// </summary>
    [Fact]
    public void BgraAndRgbaPixelsWriteTheSameFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "optimum-headless-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] rgba = new byte[Width * Height * 4];
            byte[] bgra = new byte[Width * Height * 4];
            for (int texel = 0; texel < Width * Height; texel++)
            {
                byte r = (byte)(texel * 7);
                byte g = (byte)(texel * 13 + 1);
                byte b = (byte)(texel * 29 + 2);
                rgba[texel * 4] = r; rgba[texel * 4 + 1] = g; rgba[texel * 4 + 2] = b; rgba[texel * 4 + 3] = 255;
                bgra[texel * 4] = b; bgra[texel * 4 + 1] = g; bgra[texel * 4 + 2] = r; bgra[texel * 4 + 3] = 255;
            }

            string fromRgba = Path.Combine(directory, "rgba.ppm");
            string fromBgra = Path.Combine(directory, "bgra.ppm");
            Assert.True(OptimumParityDump.WriteFrame(fromRgba, Width, Height, rgba, bgra: false));
            Assert.True(OptimumParityDump.WriteFrame(fromBgra, Width, Height, bgra, bgra: true));
            Assert.Equal(File.ReadAllBytes(fromRgba), File.ReadAllBytes(fromBgra));

            byte[] written = ReadPpm(fromBgra);
            for (int texel = 0; texel < Width * Height; texel++)
            {
                Assert.Equal((byte)(texel * 7), written[texel * 3]);
                Assert.Equal((byte)(texel * 13 + 1), written[texel * 3 + 1]);
                Assert.Equal((byte)(texel * 29 + 2), written[texel * 3 + 2]);
            }

            // A buffer that does not describe the frame is refused, not written.
            Assert.False(OptimumParityDump.WriteFrame(Path.Combine(directory, "short.ppm"),
                Width, Height, new byte[4], bgra: false));
            Assert.False(File.Exists(Path.Combine(directory, "short.ppm")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

    private static byte[] ReadPpm(string path) => ReadPpm(path, Width, Height);

    /// <summary>
    /// Decodes a P6 PPM and holds it to the size it is supposed to be: the header
    /// is where a capture that used the wrong extent - the render size instead of
    /// the display size, say - shows up first.
    /// </summary>
    private static byte[] ReadPpm(string path, int width, int height)
    {
        Assert.True(File.Exists(path), path + " was not written");
        byte[] file = File.ReadAllBytes(path);
        int offset = 0;
        string[] header = new string[4];
        for (int i = 0; i < 4; i++)
        {
            int start = offset;
            while (file[offset] != (byte)' ' && file[offset] != (byte)'\n') offset++;
            header[i] = Encoding.ASCII.GetString(file, start, offset - start);
            offset++;
        }
        Assert.Equal("P6", header[0]);
        Assert.Equal(width.ToString(CultureInfo.InvariantCulture), header[1]);
        Assert.Equal(height.ToString(CultureInfo.InvariantCulture), header[2]);
        Assert.Equal("255", header[3]);
        Assert.Equal(offset + width * height * 3, file.Length);
        return file.AsSpan(offset).ToArray();
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/ParityDumpTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Optimum.Render.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The Vulkan half of the per-attachment parity dump (OPTIMUM_PARITY_DUMP):
/// known patterns rendered into the formats the framebuffer list actually uses -
/// RGBA8, RGBA16F, R32F and depth - are read back through
/// <see cref="VulkanDevice.ReadTextureForParity" />, written by the
/// shared <see cref="OptimumParityDump" /> writer, and decoded from the files.
///
/// Every value is checked against the fragment that produced it, in GL row
/// order: file row k must be gl_FragCoord.y = k + 0.5. The size is odd and
/// non-square so a transposed or flipped image cannot pass, and the float
/// patterns carry HDR and negative values a clamp-to-byte decode would destroy.
/// </summary>
public class ParityDumpTests
{
    private const int Width = 13;
    private const int Height = 7;

    private readonly ITestOutputHelper _output;

    public ParityDumpTests(ITestOutputHelper output) => _output = output;

    private const string Vertex = """
        #version 330 core
        void main() {
            gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                               -1 + ((gl_VertexID & 2) << 1), 0, 1);
        }
        """;

    private const string Fragment = """
        #version 330 core
        layout(location = 0) out vec4 color;
        layout(location = 1) out vec4 hdr;
        layout(location = 2) out vec4 linearDepth;
        void main() {
            int x = int(gl_FragCoord.x);
            int y = int(gl_FragCoord.y);
            color = vec4(float(x * 17) / 255.0, float(y * 31) / 255.0,
                         float((x + 3 * y) % 256) / 255.0, float(250 - x - 2 * y) / 255.0);
            hdr = vec4(100.0 + float(x) * 1.5, -0.25 * float(y), float(x * y), 0.5 + float(x));
            linearDepth = vec4(1000.0 + float(x) * 0.125 + float(y) * 64.0, 0.0, 0.0, 1.0);
            gl_FragDepth = (float(x + y * 13) + 0.5) / 91.0;
        }
        """;

    [SkippableFact]
    public void RenderedPatternsDumpAndDecodeBackInGlRowOrder()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        string directory = Path.Combine(Path.GetTempPath(), "optimum-parity-dump-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new Dictionary<string, int>();
            using (device)
            {
                VulkanDevice seam = device!;
                int program = GpuTest.LinkProgram(seam, Vertex, Fragment, "parity-dump");

                int rgba8 = seam.CreateTexture2D(Width, Height, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int rgba16f = seam.CreateTexture2D(Width, Height, EnumTextureInternalFormat.Rgba16f,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int r32f = seam.CreateTexture2DRaw(Width, Height, 0x822E, IntPtr.Zero, 4);
                int depth = seam.CreateTexture2D(Width, Height, EnumTextureInternalFormat.DepthComponent32,
                    EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);

                int framebuffer = seam.CreateFramebuffer(Width, Height);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, rgba8, 0);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment1, rgba16f, 0);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment2, r32f, 0);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
                seam.SetDrawBuffers(framebuffer, 7);
                Assert.True(seam.CheckFramebufferComplete(framebuffer, out string status), status);

                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.SetViewport(0, 0, Width, Height);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.SetDepthTest(true);
                seam.SetDepthMask(true);
                seam.SetDepthFunc(0x0207); // GL_ALWAYS
                seam.UseProgram(program);
                seam.DrawFullscreenTriangle();

                // Readback inside the frame, after the draw and before Present -
                // where the client calls it.
                var attachments = new (string Label, int Texture)[]
                {
                    ("color0", rgba8), ("color1", rgba16f), ("color2", r32f), ("depth", depth),
                };
                foreach (var (label, texture) in attachments)
                {
                    OptimumTextureReadback? readback = seam.ReadTextureForParity(texture);
                    Assert.NotNull(readback);
                    files[label] = OptimumParityDump.Write(directory, 0, "Primary", label, readback!);
                }
                seam.Present();
                GpuTest.AssertClean(seam);
            }

            Assert.Equal(2, files["color0"]);
            Assert.Equal(2, files["color1"]);
            Assert.Equal(1, files["color2"]);
            Assert.Equal(1, files["depth"]);

            string[] expectedNames =
            {
                "0-Primary-color0-rgba8.ppm", "0-Primary-color0-rgba8.pgm",
                "0-Primary-color1-rgba16f.pfm", "0-Primary-color1-rgba16f.alpha.pfm",
                "0-Primary-color2-r32f.pfm", "0-Primary-depth-depth.pfm",
            };
            string[] actualNames = Directory.GetFiles(directory);
            for (int i = 0; i < actualNames.Length; i++) actualNames[i] = Path.GetFileName(actualNames[i]);
            Array.Sort(actualNames, StringComparer.Ordinal);
            string[] sortedExpected = (string[])expectedNames.Clone();
            Array.Sort(sortedExpected, StringComparer.Ordinal);
            Assert.Equal(sortedExpected, actualNames);

            // RGBA8: PPM of RGB, PGM of alpha, exact bytes.
            byte[] rgb = ReadRaster(Path.Combine(directory, "0-Primary-color0-rgba8.ppm"), "P6", out _);
            byte[] alpha = ReadRaster(Path.Combine(directory, "0-Primary-color0-rgba8.pgm"), "P5", out _);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int texel = y * Width + x;
                Assert.Equal((byte)(x * 17), rgb[texel * 3]);
                Assert.Equal((byte)(y * 31), rgb[texel * 3 + 1]);
                Assert.Equal((byte)((x + 3 * y) % 256), rgb[texel * 3 + 2]);
                Assert.Equal((byte)(250 - x - 2 * y), alpha[texel]);
            }

            // RGBA16F: PF of RGB plus Pf of alpha, float32 little-endian, HDR and negatives intact.
            float[] hdr = ReadFloats(Path.Combine(directory, "0-Primary-color1-rgba16f.pfm"), "PF");
            float[] hdrAlpha = ReadFloats(Path.Combine(directory, "0-Primary-color1-rgba16f.alpha.pfm"), "Pf");
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int texel = y * Width + x;
                Assert.Equal(100f + x * 1.5f, hdr[texel * 3]);
                Assert.Equal(-0.25f * y, hdr[texel * 3 + 1]);
                Assert.Equal((float)(x * y), hdr[texel * 3 + 2]);
                Assert.Equal(0.5f + x, hdrAlpha[texel]);
            }

            // R32F: one channel, exact.
            float[] linear = ReadFloats(Path.Combine(directory, "0-Primary-color2-r32f.pfm"), "Pf");
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                Assert.Equal(1000f + x * 0.125f + y * 64f, linear[y * Width + x]);
            }

            // Depth: one channel, the fragment's gl_FragDepth.
            float[] depthValues = ReadFloats(Path.Combine(directory, "0-Primary-depth-depth.pfm"), "Pf");
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                float expected = (x + y * 13 + 0.5f) / 91f;
                Assert.InRange(depthValues[y * Width + x], expected - 1e-6f, expected + 1e-6f);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

    /// <summary>Header: magic, width, height, maxval-or-scale, each on its own line.</summary>
    private static byte[] ReadRaster(string path, string magic, out string scale)
    {
        byte[] file = File.ReadAllBytes(path);
        int offset = 0;
        string[] header = new string[4];
        for (int i = 0; i < 4; i++)
        {
            int start = offset;
            while (file[offset] != (byte)' ' && file[offset] != (byte)'\n') offset++;
            header[i] = Encoding.ASCII.GetString(file, start, offset - start);
            offset++;
        }
        Assert.Equal(magic, header[0]);
        Assert.Equal(Width.ToString(CultureInfo.InvariantCulture), header[1]);
        Assert.Equal(Height.ToString(CultureInfo.InvariantCulture), header[2]);
        scale = header[3];
        int channels = magic is "P6" or "PF" ? 3 : 1;
        int bytesPerValue = magic.StartsWith("P", StringComparison.Ordinal) && magic[1] is 'F' or 'f' ? 4 : 1;
        Assert.Equal(offset + Width * Height * channels * bytesPerValue, file.Length);
        return file.AsSpan(offset).ToArray();
    }

    private static float[] ReadFloats(string path, string magic)
    {
        byte[] raster = ReadRaster(path, magic, out string scale);
        // Negative scale: little-endian, the encoding the dump promises.
        Assert.Equal("-1.0", scale);
        var values = new float[raster.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(raster.AsSpan(i * 4, 4));
        }
        return values;
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/TextureDumpTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

/// <summary>
/// Covers the format-aware conversion in <see cref="TextureDump.Write" /> - in
/// particular that a 16-bit float attachment (the shape a TAA motion vector
/// target takes) is accepted and converted rather than read as 8-bit RGBA and
/// either overrun or garbled.
/// </summary>
public class TextureDumpTests
{
    [Fact]
    public void WritesRgba16FloatTextureAsPpm()
    {
        const int width = 4;
        const int height = 3;

        string directory = Path.Combine(Path.GetTempPath(), "optimum-texture-dump-tests-" + Guid.NewGuid());
        string? previousDir = Environment.GetEnvironmentVariable("OPTIMUM_DUMP_DIR");
        string? previousTrace = Environment.GetEnvironmentVariable("OPTIMUM_RENDER_TRACE");
        try
        {
            Environment.SetEnvironmentVariable("OPTIMUM_DUMP_DIR", directory);
            Environment.SetEnvironmentVariable("OPTIMUM_RENDER_TRACE", null);

            var texels = new Half[width * height * 4];
            for (int i = 0; i < texels.Length; i++)
            {
                // Cycle through channel values so every component participates.
                texels[i] = (Half)((i % 4) switch
                {
                    0 => 1f,
                    1 => 0.5f,
                    2 => 0f,
                    _ => 1f, // alpha, ignored by the PPM
                });
            }
            byte[] data = MemoryMarshal.AsBytes<Half>(texels).ToArray();

            bool written = TextureDump.Write(
                textureId: 1234,
                width: width,
                height: height,
                bgra: false,
                format: Format.R16G16B16A16Sfloat,
                data: data);

            Assert.True(written);

            string path = Directory.GetFiles(directory, "*-texture-1234-*.ppm").SingleOrDefault()
                ?? throw new Xunit.Sdk.XunitException("No dump file was written.");

            byte[] file = File.ReadAllBytes(path);
            string header = $"P6\n{width} {height}\n255\n";
            string actualHeader = System.Text.Encoding.ASCII.GetString(file, 0, header.Length);
            Assert.Equal(header, actualHeader);

            int expectedPixelBytes = width * height * 3;
            Assert.Equal(header.Length + expectedPixelBytes, file.Length);

            // First texel is (1, 0.5, 0) -> full red, half green, zero blue.
            int pixelStart = header.Length;
            Assert.Equal(255, file[pixelStart]);
            Assert.InRange(file[pixelStart + 1], 126, 128);
            Assert.Equal(0, file[pixelStart + 2]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPTIMUM_DUMP_DIR", previousDir);
            Environment.SetEnvironmentVariable("OPTIMUM_RENDER_TRACE", previousTrace);
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// R16f (the OIT revealage attachment's format) is two bytes per texel. It
    /// used to fall through to the 4-byte default, so the readback was sized
    /// twice as large as the image and every row was decoded from the wrong
    /// offset. Known values in, known greys out.
    /// </summary>
    [Fact]
    public void WritesR16FloatTextureAsPpm()
    {
        const int width = 4;
        const int height = 2;

        Assert.Equal(2, TextureDump.BytesPerTexel(Format.R16Sfloat));

        string directory = Path.Combine(Path.GetTempPath(), "optimum-texture-dump-tests-" + Guid.NewGuid());
        string? previousDir = Environment.GetEnvironmentVariable("OPTIMUM_DUMP_DIR");
        string? previousTrace = Environment.GetEnvironmentVariable("OPTIMUM_RENDER_TRACE");
        try
        {
            Environment.SetEnvironmentVariable("OPTIMUM_DUMP_DIR", directory);
            Environment.SetEnvironmentVariable("OPTIMUM_RENDER_TRACE", null);

            // Motion-like mapping: value / 64 * 127 + 128, clamped to [0,255].
            float[] values = { 0f, 16f, -32f, 64f, -64f, 8f, -8f, 32f };
            byte[] expected = new byte[values.Length];
            var texels = new Half[width * height];
            for (int i = 0; i < texels.Length; i++)
            {
                texels[i] = (Half)values[i];
                expected[i] = (byte)Math.Clamp(values[i] / 64f * 127f + 128f, 0f, 255f);
            }
            byte[] data = MemoryMarshal.AsBytes<Half>(texels).ToArray();
            Assert.Equal(width * height * 2, data.Length);

            bool written = TextureDump.Write(
                textureId: 4321,
                width: width,
                height: height,
                bgra: false,
                format: Format.R16Sfloat,
                data: data);

            Assert.True(written);

            string path = Directory.GetFiles(directory, "*-texture-4321-*.ppm").SingleOrDefault()
                ?? throw new Xunit.Sdk.XunitException("No dump file was written.");

            byte[] file = File.ReadAllBytes(path);
            string header = $"P6\n{width} {height}\n255\n";
            Assert.Equal(header, System.Text.Encoding.ASCII.GetString(file, 0, header.Length));
            Assert.Equal(header.Length + width * height * 3, file.Length);

            for (int i = 0; i < texels.Length; i++)
            {
                int pixel = header.Length + i * 3;
                // Greyscale: all three channels carry the same converted value.
                Assert.Equal(expected[i], file[pixel]);
                Assert.Equal(expected[i], file[pixel + 1]);
                Assert.Equal(expected[i], file[pixel + 2]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPTIMUM_DUMP_DIR", previousDir);
            Environment.SetEnvironmentVariable("OPTIMUM_RENDER_TRACE", previousTrace);
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }
}
}
