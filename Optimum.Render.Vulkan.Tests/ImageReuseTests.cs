using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class ImageReuseTests(ITestOutputHelper output)
{
    private const string Triangle = """
        #version 330 core
        void main() {
            gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0, 1);
        }
        """;

    private VulkanDevice Open(bool aliasing = false)
    {
        var device = GpuTest.NewDevice();
        device.TransientAliasingOverride = aliasing;
        if (!device.Initialize(IntPtr.Zero, 0, 0, out string reason))
        {
            device.Dispose();
            Skip.If(true, "Vulkan unavailable: " + reason);
        }
        output.WriteLine(device.RendererString);
        return device;
    }

    private static int Attach(VulkanDevice device, int texture, int width, int height)
    {
        int target = device.CreateFramebuffer(width, height);
        device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        device.SetDrawBuffers(target, 1);
        return target;
    }

    private static void Draw(VulkanDevice device, int framebuffer, int program, int width, int height)
    {
        device.BindFramebuffer(framebuffer);
        device.SetViewport(0, 0, width, height);
        device.SetDepthTest(false);
        device.SetCullFace(false);
        device.SetBlend(false, EnumBlendMode.Standard);
        device.UseProgram(program);
        device.DrawFullscreenTriangle();
    }

    private static unsafe byte[] Read(VulkanDevice device, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        fixed (byte* pointer = pixels)
            device.ReadDefaultFramebuffer(0, 0, width, height, (IntPtr)pointer);
        return pixels;
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReusedPostTargetsContainTheCurrentFrame(bool aliasing, bool frameGraph)
    {
        using var device = Open(aliasing);
        device.FrameGraphForTests.Enabled = frameGraph;
        int fill = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            uniform float frameBlue;
            out vec4 color;
            void main() { color = vec4(floor(gl_FragCoord.xy) / 16.0, frameBlue, 1); }
            """, "reuse-fill");
        int rotate = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            uniform sampler2D source;
            out vec4 color;
            void main() {
                vec3 pixel = texelFetch(source, ivec2(gl_FragCoord.xy), 0).rgb;
                color = vec4(pixel.g, pixel.b, pixel.r * 0.5 + 0.25, 1);
            }
            """, "reuse-transform");
        device.SetSamplerUnit(rotate, "source", 0);
        int blue = device.GetUniformLocation(fill, "frameBlue");
        int[] textures = new int[4], targets = new int[4];
        for (int i = 0; i < 4; i++)
        {
            textures[i] = i < 3
                ? device.CreateTransientTexture2D(16, 16, EnumTextureInternalFormat.Rgba8, 2 + i)
                : device.CreateTexture2D(16, 16, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            targets[i] = Attach(device, textures[i], 16, 16);
        }
        byte[] pixels = Array.Empty<byte>();
        for (int frame = 0; frame < 5; frame++)
        {
            device.BeginFrame();
            for (int i = 0; i < 3; i++) device.BindTransientForFrame(textures[i], i, i + 1);
            Assert.Equal(aliasing ? 1 : 0, device.Transients.AliasedLeaseCount);
            device.SetUniform(fill, blue, (frame + 1) / 16f);
            for (int pass = 0; pass < 4; pass++)
            {
                device.BindTexture(0, pass == 0 ? 0 : textures[pass - 1]);
                Draw(device, targets[pass], pass == 0 ? fill : rotate, 16, 16);
            }
            device.BindTexture(0, 0);
            if (frame == 4) pixels = Read(device, 16, 16);
            device.Present();
        }
        // Three channel rotations leave each original channel halved plus 0.25.
        // Allow two UNORM8 rounding steps; expected values come from the scene inputs.
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int index = (y * 16 + x) * 4;
                Assert.InRange((int)pixels[index], (int)Math.Round((x / 32.0 + .25) * 255) - 2, (int)Math.Round((x / 32.0 + .25) * 255) + 2);
                Assert.InRange((int)pixels[index + 1], (int)Math.Round((y / 32.0 + .25) * 255) - 2, (int)Math.Round((y / 32.0 + .25) * 255) + 2);
                Assert.InRange((int)pixels[index + 2], 102, 106);
                Assert.Equal(255, pixels[index + 3]);
            }
        if (aliasing) Assert.Equal(2, device.Transients.PhysicalImageCount);
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void SamplingTheTargetUsesAFreshSnapshotForEveryDraw()
    {
        using var device = Open();
        int swap = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            uniform sampler2D source;
            out vec4 color;
            void main() { color = texelFetch(source, ivec2(1 - int(gl_FragCoord.x), 0), 0); }
            """, "feedback-swap");
        byte[] original = { 255, 0, 0, 255, 0, 255, 0, 255 };
        int texture;
        fixed (byte* pixels = original)
            texture = device.CreateTexture2D(2, 1, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
        int target = Attach(device, texture, 2, 1);
        device.SetSamplerUnit(swap, "source", 0);
        for (int frame = 0; frame < 6; frame++)
        {
            device.BeginFrame();
            device.BindTexture(0, texture);
            Draw(device, target, swap, 2, 1);
            Draw(device, target, swap, 2, 1);
            Assert.Equal(original, Read(device, 2, 1));
            device.Present();
        }
        Assert.InRange(device.ReadSelfCopiesForTests.Created, 1, 3);
        GpuTest.AssertClean(device);
    }

    private sealed class Clock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    [Fact]
    public void FeedbackCopiesWaitForCompletionBeforeReuseOrRetirement()
    {
        var clock = new Clock { FrameRecorded = 7 };
        int next = 0;
        var destroyed = new List<int>();
        var pool = new FeedbackCopyPool(clock, _ => ++next, destroyed.Add, idleFrames: 2);
        var shape = new FeedbackCopyDesc(16, 16, Format.R8G8B8A8Unorm, 1, 1, false);
        int first = pool.Acquire(shape);
        pool.Release(first); pool.EndFrame(); pool.Collect();
        int second = pool.Acquire(shape);
        Assert.NotEqual(first, second);
        Assert.Empty(destroyed);
        clock.FrameCompleted = 7; pool.Collect();
        Assert.Equal(first, pool.Acquire(shape));
        pool.Release(first); pool.Release(second); pool.EndFrame();
        for (int i = 0; i < 5; i++) pool.Collect();
        Assert.Equal(0, pool.Live);
        Assert.Contains(first, destroyed);
        Assert.Contains(second, destroyed);
    }
}
