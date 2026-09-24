// Source: Optimum.Render.Vulkan.Tests/BarrierBatcherFrameTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.GpuTest;

/// <summary>
/// A representative frame through the device: a primary target with two colour
/// attachments and depth, a composition pass that writes attachment 0 while
/// sampling attachment 1, and an output pass that samples the scene colour and
/// the depth. Measures the image barriers and barrier commands per frame in
/// steady state.
/// </summary>
public class BarrierBatcherFrameTests
{
    private readonly ITestOutputHelper _output;

    public BarrierBatcherFrameTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.5, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    internal readonly record struct FrameCounts(long ImageBarriers, long BarrierCommands, byte[] Pixels);

    internal static unsafe FrameCounts RenderRepresentativeFrames(VulkanDevice seam, int warmup, int measured)
    {
        const int size = 16;

        int sceneProgram = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            layout(location = 1) out vec4 outGlow;
            void main(void)
            {
                outColor = vec4(40.0 / 255.0, 90.0 / 255.0, 160.0 / 255.0, 1.0);
                outGlow = vec4(20.0 / 255.0, 0.0, 0.0, 1.0);
            }
            """, "scene");
        int composeProgram = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D glow;
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = vec4(texture(glow, uv).r + 40.0 / 255.0, 90.0 / 255.0, 160.0 / 255.0, 1.0); }
            """, "compose");
        int outputProgram = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D scene;
            uniform sampler2D depthTex;
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = vec4(texture(scene, uv).rgb, texture(depthTex, uv).r); }
            """, "output");

        int Texture(EnumTextureInternalFormat format) => seam.CreateTexture2D(
            size, size, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

        int scene = Texture(EnumTextureInternalFormat.Rgba8);
        int glow = Texture(EnumTextureInternalFormat.Rgba8);
        int depth = Texture(EnumTextureInternalFormat.DepthComponent32);
        int final = Texture(EnumTextureInternalFormat.Rgba8);

        int primary = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(primary, EnumFramebufferAttachment.ColorAttachment0, scene, 0);
        seam.AttachTexture(primary, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        seam.AttachTexture(primary, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        seam.SetDrawBuffers(primary, 0b11);

        int output = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(output, EnumFramebufferAttachment.ColorAttachment0, final, 0);
        seam.SetDrawBuffers(output, 0b1);

        long barriersBefore = 0;
        long commandsBefore = 0;
        for (int frame = 0; frame < warmup + measured; frame++)
        {
            if (frame == warmup)
            {
                barriersBefore = VulkanStats.ImageBarriers;
                commandsBefore = BarrierCommands();
            }

            seam.BeginFrame();

            seam.BindFramebuffer(primary);
            seam.SetDrawBuffers(primary, 0b11);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(true);
            seam.SetDepthMask(true);
            seam.SetDepthFunc(0x203);
            seam.ClearDepth(1f);
            seam.ClearColor(0, 0, 0, 0, 0);
            seam.ClearColor(1, 0, 0, 0, 0);
            seam.UseProgram(sceneProgram);
            seam.DrawFullscreenTriangle();

            // Composition: write attachment 0, sample attachment 1.
            seam.SetDepthTest(false);
            seam.SetDepthMask(false);
            seam.SetDrawBuffers(primary, 0b1);
            seam.UseProgram(composeProgram);
            seam.SetSamplerUnit(composeProgram, "glow", 0);
            seam.BindTexture(0, glow);
            seam.DrawFullscreenTriangle();
            seam.SetDrawBuffers(primary, 0b11);

            // Output: sample the scene colour and the depth.
            seam.BindFramebuffer(output);
            seam.SetViewport(0, 0, size, size);
            seam.UseProgram(outputProgram);
            seam.SetSamplerUnit(outputProgram, "scene", 0);
            seam.SetSamplerUnit(outputProgram, "depthTex", 1);
            seam.BindTexture(0, scene);
            seam.BindTexture(1, depth);
            seam.DrawFullscreenTriangle();
            seam.BindTexture(0, 0);
            seam.BindTexture(1, 0);

            seam.Present();
        }

        long barriers = VulkanStats.ImageBarriers - barriersBefore;
        long commands = BarrierCommands() - commandsBefore;

        var pixels = new byte[size * size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(output);
            seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
        }
        return new FrameCounts(barriers, commands, pixels);
    }

    private static long BarrierCommands() => VulkanStats.BarrierCommands;

    /// <summary>
    /// Recorded at f187375 (feat/vulkan-native before Phase 2 step 1) with this
    /// exact frame: 18 image barriers over 3 steady-state frames, each its own
    /// vkCmdPipelineBarrier2 with ALL_COMMANDS stages (6 commands per frame).
    /// Centre pixel 60,90,160,191.
    /// </summary>
    private const long RecordedImageBarriers = 18;
    private const long RecordedBarrierCommands = 18;

    [SkippableFact]
    public void ARepresentativeFrameNeedsFewerBarrierCommandsAndStaysSyncClean()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            const int measured = 3;
            FrameCounts counts = RenderRepresentativeFrames(device!, warmup: 2, measured);
            _output.WriteLine("image barriers over " + measured + " frames: " + counts.ImageBarriers +
                              " (recorded " + RecordedImageBarriers + "), barrier commands: " +
                              counts.BarrierCommands + " (recorded " + RecordedBarrierCommands + ")");

            // The pixels are what they were before the barriers changed: glow
            // (20) added to the scene red (40) by the composition pass, the
            // scene's green and blue, and the depth (0.5 remapped to 0.75).
            int centre = (16 / 2 * 16 + 16 / 2) * 4;
            Assert.Equal(60, counts.Pixels[centre]);
            Assert.Equal(90, counts.Pixels[centre + 1]);
            Assert.Equal(160, counts.Pixels[centre + 2]);
            Assert.InRange(counts.Pixels[centre + 3], (byte)189, (byte)193);

            Assert.True(counts.ImageBarriers <= RecordedImageBarriers,
                "image barriers per frame grew: " + counts.ImageBarriers);
            Assert.True(counts.BarrierCommands < RecordedBarrierCommands,
                "barrier commands per frame did not drop: " + counts.BarrierCommands);
            Assert.True(counts.BarrierCommands <= counts.ImageBarriers);
            AssertClean(device!);
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/FrameGraphBarrierTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System.Collections.Generic;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

/// <summary>
/// The usage table and the barrier derivation of <see cref="ResourceStateTracker" />,
/// without a device: layout, stage and access come from the usage; read after
/// write, write after read and write after write are ordered; repeated reads and
/// repeated uses cost nothing; sub-ranges split and merge; a present ends in
/// PRESENT_SRC.
/// </summary>
public class FrameGraphBarrierTests
{
    private static List<ImageTransition> Require(ResourceStateTracker tracker, ResourceUsage usage,
        bool discard = false) =>
        Require(tracker, 0, tracker.MipLevels, 0, tracker.Layers, usage, discard);

    private static List<ImageTransition> Require(ResourceStateTracker tracker, uint baseMip, uint mipCount,
        uint baseLayer, uint layerCount, ResourceUsage usage, bool discard = false)
    {
        var output = new List<ImageTransition>();
        int count = tracker.Require(baseMip, mipCount, baseLayer, layerCount, usage, discard, output);
        Assert.Equal(output.Count, count);
        return output;
    }

    [Theory]
    [InlineData(ResourceUsage.ColorWrite, false, ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit)]
    [InlineData(ResourceUsage.ColorBlend, false, ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit)]
    [InlineData(ResourceUsage.DepthWrite, true, ImageLayout.DepthAttachmentOptimal, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit)]
    [InlineData(ResourceUsage.DepthReadOnly, true, ImageLayout.DepthReadOnlyOptimal, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentReadBit)]
    [InlineData(ResourceUsage.DepthReadOnlySampled, true, ImageLayout.DepthReadOnlyOptimal, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.SampleFragment, false, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.SampleFragment, true, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.SampleVertex, false, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.StorageRead, false, ImageLayout.General, PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit)]
    [InlineData(ResourceUsage.TransferSrc, false, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit)]
    [InlineData(ResourceUsage.TransferDst, false, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit)]
    [InlineData(ResourceUsage.PresentSrc, false, ImageLayout.PresentSrcKhr, PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None)]
    public void TheUsageTableDerivesLayoutStageAndAccess(ResourceUsage usage, bool depth, ImageLayout layout,
        PipelineStageFlags2 stage, AccessFlags2 access)
    {
        Assert.Equal(new UsageState(layout, stage, access), UsageState.For(usage, depth));
        Assert.NotEqual(PipelineStageFlags2.AllCommandsBit, UsageState.For(usage, depth).Stage & PipelineStageFlags2.AllCommandsBit);
    }

    [Fact]
    public void AnAttachmentUsageFollowsTheImageAspect()
    {
        Assert.Equal(ImageLayout.DepthAttachmentOptimal, UsageState.For(ResourceUsage.ColorBlend, depth: true).Layout);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, UsageState.For(ResourceUsage.DepthWrite, depth: false).Layout);
    }

    [Fact]
    public void TheFirstUseStartsFromUndefinedWithNoSourceStage()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        ImageTransition only = Assert.Single(Require(tracker, ResourceUsage.TransferDst));
        Assert.Equal(ImageLayout.Undefined, only.Sides.OldLayout);
        Assert.Equal(ImageLayout.TransferDstOptimal, only.Sides.NewLayout);
        Assert.Equal(PipelineStageFlags2.None, only.Sides.SrcStage);
        Assert.Equal(AccessFlags2.None, only.Sides.SrcAccess);
        Assert.Equal(PipelineStageFlags2.TransferBit, only.Sides.DstStage);
        Assert.Equal(AccessFlags2.TransferWriteBit, only.Sides.DstAccess);
    }

    [Fact]
    public void WriteThenReadGivesOneBarrierThatMakesTheWriteAvailable()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.TransferDst);

        ImageTransition read = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal(ImageLayout.TransferDstOptimal, read.Sides.OldLayout);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, read.Sides.NewLayout);
        Assert.Equal(PipelineStageFlags2.TransferBit, read.Sides.SrcStage);
        Assert.Equal(AccessFlags2.TransferWriteBit, read.Sides.SrcAccess);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, read.Sides.DstStage);
        Assert.Equal(AccessFlags2.ShaderSampledReadBit, read.Sides.DstAccess);
    }

    [Fact]
    public void ReadThenReadNeedsNoBarrier()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.TransferDst);
        Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Empty(Require(tracker, ResourceUsage.SampleFragment));
        // A reader at another stage, with no write since the barrier: still nothing.
        Assert.Empty(Require(tracker, ResourceUsage.SampleVertex));
    }

    [Fact]
    public void RepeatedAttachmentUseNeedsNoBarrier()
    {
        var colour = new ResourceStateTracker(1, 1, depth: false);
        Assert.Single(Require(colour, ResourceUsage.ColorBlend));
        Assert.Empty(Require(colour, ResourceUsage.ColorBlend));
        Assert.Empty(Require(colour, ResourceUsage.ColorWrite));

        var depth = new ResourceStateTracker(1, 1, depth: true);
        Assert.Single(Require(depth, ResourceUsage.DepthReadOnlySampled));
        Assert.Empty(Require(depth, ResourceUsage.DepthReadOnlySampled));
    }

    [Fact]
    public void ReadAfterWriteInTheSameLayoutOrdersOnlyAStageTheWriteIsNotVisibleTo()
    {
        // A storage write at the fragment shader in GENERAL, then readers in the
        // same layout. No usage in the table writes a layout a pure reader shares,
        // so the rule is exercised on the state directly.
        var written = new SubresourceState(ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit, PipelineStageFlags2.None, AccessFlags2.None);

        SubresourceState sameStage = written;
        var fragmentReader = new UsageState(ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit);
        Assert.False(ResourceStateTracker.Advance(ref sameStage, fragmentReader,
            PipelineStageFlags2.None, AccessFlags2.None, discard: false, out _));

        SubresourceState otherStage = written;
        Assert.True(ResourceStateTracker.Advance(ref otherStage, UsageState.For(ResourceUsage.StorageRead, false),
            PipelineStageFlags2.None, AccessFlags2.None, discard: false, out BarrierSides raw));
        Assert.Equal(ImageLayout.General, raw.OldLayout);
        Assert.Equal(ImageLayout.General, raw.NewLayout);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, raw.SrcStage);
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit, raw.SrcAccess);
        Assert.True((raw.DstStage & PipelineStageFlags2.ComputeShaderBit) != 0);

        // Once visible, the same reader again costs nothing.
        Assert.False(ResourceStateTracker.Advance(ref otherStage, UsageState.For(ResourceUsage.StorageRead, false),
            PipelineStageFlags2.None, AccessFlags2.None, discard: false, out _));
    }

    [Fact]
    public void SamplingTheReadOnlyDepthOfTheSameScopeNeedsNoBarrier()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: true);
        Require(tracker, ResourceUsage.DepthReadOnly);
        // Both store the attachment at the depth tests; the sampled form only adds a reader.
        Assert.Empty(Require(tracker, ResourceUsage.DepthReadOnlySampled));
    }

    [Fact]
    public void WriteAfterReadInTheSameLayoutOrdersAgainstTheEarlierReaderStage()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: true);
        Require(tracker, ResourceUsage.DepthReadOnlySampled);
        // The depth-test-only use does not run at the fragment shader stage that read before.
        ImageTransition war = Assert.Single(Require(tracker, ResourceUsage.DepthReadOnly));
        Assert.True((war.Sides.SrcStage & PipelineStageFlags2.FragmentShaderBit) != 0);
        Assert.True((war.Sides.SrcAccess & AccessFlags2.ShaderSampledReadBit) != 0);
    }

    [Fact]
    public void AReadOnlyDepthAttachmentMakesItsStoreWriteAvailableToTheNextTransition()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: true);
        Require(tracker, ResourceUsage.DepthReadOnlySampled);
        ImageTransition next = Assert.Single(Require(tracker, ResourceUsage.DepthWrite));
        Assert.True((next.Sides.SrcAccess & AccessFlags2.DepthStencilAttachmentWriteBit) != 0,
            "write-after-write: the read-only pass's store must be named on the source side");
        Assert.Equal(ImageLayout.DepthReadOnlyOptimal, next.Sides.OldLayout);
        Assert.Equal(ImageLayout.DepthAttachmentOptimal, next.Sides.NewLayout);
    }

    [Fact]
    public void WriteAfterWriteAcrossLayoutsNamesThePreviousWrite()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.ColorWrite);
        ImageTransition waw = Assert.Single(Require(tracker, ResourceUsage.TransferDst));
        Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, waw.Sides.SrcStage);
        Assert.Equal(AccessFlags2.ColorAttachmentWriteBit, waw.Sides.SrcAccess);
    }

    [Fact]
    public void ADiscardingUseStartsFromUndefined()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.SampleFragment);
        ImageTransition discarded = Assert.Single(Require(tracker, ResourceUsage.TransferDst, discard: true));
        Assert.Equal(ImageLayout.Undefined, discarded.Sides.OldLayout);
    }

    [Fact]
    public void ASubRangeSplitsTheImageAndAgreeingAgainMergesIt()
    {
        var tracker = new ResourceStateTracker(7, 1, depth: false);
        Require(tracker, ResourceUsage.TransferSrc);
        Assert.False(tracker.IsSplit);

        // Mip 3 alone: one barrier over exactly that level, and the image splits.
        ImageTransition level = Assert.Single(Require(tracker, 3, 1, 0, 1, ResourceUsage.TransferDst, discard: true));
        Assert.Equal((3u, 1u, 0u, 1u), (level.BaseMip, level.MipCount, level.BaseLayer, level.LayerCount));
        Assert.True(tracker.IsSplit);
        Assert.Equal(ImageLayout.Undefined, tracker.Layout);
        Assert.Equal(ImageLayout.TransferDstOptimal, tracker.StateOf(3, 0).Layout);
        Assert.Equal(ImageLayout.TransferSrcOptimal, tracker.StateOf(2, 0).Layout);

        // Back to TRANSFER_SRC: every level agrees, one entry again.
        Assert.Single(Require(tracker, 3, 1, 0, 1, ResourceUsage.TransferSrc));
        Assert.False(tracker.IsSplit);
        Assert.Equal(ImageLayout.TransferSrcOptimal, tracker.Layout);

        // And the whole image moves in a single barrier.
        ImageTransition whole = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal((0u, 7u), (whole.BaseMip, whole.MipCount));
    }

    [Fact]
    public void AWholeUseOfASplitImageEmitsOneRectanglePerAgreeingRun()
    {
        var tracker = new ResourceStateTracker(4, 2, depth: false);
        Require(tracker, ResourceUsage.SampleFragment);
        // Layer 1 of mips 2 and 3 becomes a copy destination.
        Assert.Single(Require(tracker, 2, 2, 1, 1, ResourceUsage.TransferDst));

        List<ImageTransition> back = Require(tracker, ResourceUsage.SampleFragment);
        // Only the destination rectangle changes layout; the rest is already sampled.
        ImageTransition rectangle = Assert.Single(back);
        Assert.Equal((2u, 2u, 1u, 1u), (rectangle.BaseMip, rectangle.MipCount, rectangle.BaseLayer, rectangle.LayerCount));
        Assert.False(tracker.IsSplit);
    }

    [Fact]
    public void AMipChainBuildEndsInOneWholeImageBarrier()
    {
        const uint mips = 7;
        var tracker = new ResourceStateTracker(mips, 1, depth: false);
        int barriers = 0;
        barriers += Require(tracker, ResourceUsage.TransferDst).Count;
        barriers += Require(tracker, ResourceUsage.SampleFragment).Count;
        barriers += Require(tracker, ResourceUsage.TransferSrc).Count;
        for (uint level = 1; level < mips; level++)
        {
            barriers += Require(tracker, level, 1, 0, 1, ResourceUsage.TransferDst, discard: true).Count;
            barriers += Require(tracker, level, 1, 0, 1, ResourceUsage.TransferSrc).Count;
        }
        Assert.False(tracker.IsSplit);
        ImageTransition final = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal(mips, final.MipCount);
        Assert.Equal(3 + 2 * (int)(mips - 1), barriers);
    }

    [Fact]
    public void APresentEndsInPresentSrc()
    {
        var swapchainImage = new ResourceStateTracker(1, 1, depth: false);
        Require(swapchainImage, ResourceUsage.TransferDst, discard: true);
        ImageTransition present = Assert.Single(Require(swapchainImage, ResourceUsage.PresentSrc));
        Assert.Equal(ImageLayout.PresentSrcKhr, present.Sides.NewLayout);
        Assert.Equal(ImageLayout.PresentSrcKhr, swapchainImage.Layout);
        Assert.Equal(PipelineStageFlags2.TransferBit, present.Sides.SrcStage);
        Assert.Equal(AccessFlags2.TransferWriteBit, present.Sides.SrcAccess);
        Assert.Equal(PipelineStageFlags2.BottomOfPipeBit, present.Sides.DstStage);

        swapchainImage.Reset();
        Assert.Equal(ImageLayout.Undefined, swapchainImage.Layout);
    }

    /// <summary>
    /// The next frame's first barrier on an acquired image names the acquire's
    /// wait stage on its source side (synchronization validation reported
    /// write-after-read against vkAcquireNextImageKHR without it, 2026-09-11).
    /// </summary>
    [Fact]
    public void AnAcquiredImagesFirstBarrierOrdersAgainstTheAcquireWaitStage()
    {
        var swapchainImage = new ResourceStateTracker(1, 1, depth: false);
        Require(swapchainImage, ResourceUsage.TransferDst, discard: true);
        Require(swapchainImage, ResourceUsage.PresentSrc);

        swapchainImage.Reset(PipelineStageFlags2.TransferBit);
        ImageTransition first = Assert.Single(Require(swapchainImage, ResourceUsage.TransferDst, discard: true));
        Assert.Equal(ImageLayout.Undefined, first.Sides.OldLayout);
        Assert.Equal(PipelineStageFlags2.TransferBit, first.Sides.SrcStage);
        Assert.Equal(AccessFlags2.None, first.Sides.SrcAccess);
    }

    [Fact]
    public void NoBarrierEverNamesAllCommands()
    {
        foreach (ResourceUsage from in System.Enum.GetValues<ResourceUsage>())
        foreach (ResourceUsage to in System.Enum.GetValues<ResourceUsage>())
        {
            bool depth = from is ResourceUsage.DepthWrite or ResourceUsage.DepthReadOnly or ResourceUsage.DepthReadOnlySampled;
            var tracker = new ResourceStateTracker(1, 1, depth);
            Require(tracker, from);
            foreach (ImageTransition transition in Require(tracker, to))
            {
                Assert.True((transition.Sides.SrcStage & PipelineStageFlags2.AllCommandsBit) == 0, from + "->" + to);
                Assert.True((transition.Sides.DstStage & PipelineStageFlags2.AllCommandsBit) == 0, from + "->" + to);
            }
        }
    }

    [Fact]
    public void BufferCopiesNameTheBuffersOwnUses()
    {
        (PipelineStageFlags2 stage, AccessFlags2 access) = BufferUsageState.UsesOf(
            BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit);
        Assert.Equal(PipelineStageFlags2.VertexAttributeInputBit | PipelineStageFlags2.IndexInputBit |
                     PipelineStageFlags2.TransferBit, stage);
        Assert.Equal(AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit | AccessFlags2.TransferWriteBit, access);
        Assert.Equal(0, (int)((ulong)stage & (ulong)PipelineStageFlags2.AllCommandsBit));
    }
}
}
