// Source: Optimum.Render.Vulkan.Tests/ComputePassPlanTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

/// <summary>
/// The compute pass kind without a device: the compute usages' layouts, stages and
/// accesses; the barriers a storage write, a sampled read and a mip chain derive from
/// them; declaration validation; group counts from an image; the signature the frame
/// plan sees; and the storage format fallback.
/// </summary>
public class ComputePassPlanTests
{
    private static List<ImageTransition> Require(ResourceStateTracker tracker, uint baseMip, uint mipCount,
        ResourceUsage usage)
    {
        var output = new List<ImageTransition>();
        int count = tracker.Require(baseMip, mipCount, 0, tracker.Layers, usage, false, output);
        Assert.Equal(output.Count, count);
        return output;
    }

    private static List<ImageTransition> Require(ResourceStateTracker tracker, ResourceUsage usage) =>
        Require(tracker, 0, tracker.MipLevels, usage);

    [Theory]
    [InlineData(ResourceUsage.SampleCompute, ImageLayout.ShaderReadOnlyOptimal, AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.StorageReadCompute, ImageLayout.General, AccessFlags2.ShaderStorageReadBit)]
    [InlineData(ResourceUsage.StorageWrite, ImageLayout.General, AccessFlags2.ShaderStorageWriteBit)]
    [InlineData(ResourceUsage.StorageReadWrite, ImageLayout.General, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit)]
    public void ComputeUsagesAreGeneralForStorageAndShaderReadOnlyForSampling(ResourceUsage usage, ImageLayout layout,
        AccessFlags2 access)
    {
        Assert.Equal(new UsageState(layout, PipelineStageFlags2.ComputeShaderBit, access), UsageState.For(usage, depth: false));
    }

    [Theory]
    [InlineData(ComputeAccess.Sampled, ResourceUsage.SampleCompute)]
    [InlineData(ComputeAccess.StorageRead, ResourceUsage.StorageReadCompute)]
    [InlineData(ComputeAccess.StorageWrite, ResourceUsage.StorageWrite)]
    [InlineData(ComputeAccess.StorageReadWrite, ResourceUsage.StorageReadWrite)]
    public void EveryAccessHasItsUsage(ComputeAccess access, ResourceUsage usage) =>
        Assert.Equal(usage, ComputePassPlanner.UsageOf(access));

    [Fact]
    public void AStorageWriteThenASampledReadMakesTheWriteAvailableToTheFragmentShader()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        ImageTransition first = Assert.Single(Require(tracker, ResourceUsage.StorageWrite));
        Assert.Equal(ImageLayout.Undefined, first.Sides.OldLayout);
        Assert.Equal(ImageLayout.General, first.Sides.NewLayout);
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit, first.Sides.DstAccess);

        // The next raster pass samples it: GENERAL -> SHADER_READ_ONLY naming the dispatch's write.
        ImageTransition read = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal(ImageLayout.General, read.Sides.OldLayout);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, read.Sides.NewLayout);
        Assert.Equal(PipelineStageFlags2.ComputeShaderBit, read.Sides.SrcStage);
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit, read.Sides.SrcAccess);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, read.Sides.DstStage);

        // The frame after writes it again: back to GENERAL, naming the fragment read.
        ImageTransition again = Assert.Single(Require(tracker, ResourceUsage.StorageWrite));
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, again.Sides.OldLayout);
        Assert.Equal(ImageLayout.General, again.Sides.NewLayout);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, again.Sides.SrcStage);
        Assert.Equal(AccessFlags2.ShaderSampledReadBit, again.Sides.SrcAccess);
    }

    [Fact]
    public void RepeatedStorageWritesAndStorageReadsInGeneralNeedTheBarriersTheOrderingRulesAsk()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.StorageWrite);
        // Write after write at the same stage: none.
        Assert.Empty(Require(tracker, ResourceUsage.StorageWrite));
        // Read after write at the stage the write is visible to: none.
        Assert.Empty(Require(tracker, ResourceUsage.StorageReadCompute));
        // A second compute read: none.
        Assert.Empty(Require(tracker, ResourceUsage.StorageReadCompute));
        // A compute-only write is not visible to the fragment stage: one GENERAL -> GENERAL barrier.
        Require(tracker, ResourceUsage.StorageReadWrite);
        ImageTransition fragment = Assert.Single(Require(tracker, ResourceUsage.StorageRead));
        Assert.Equal(ImageLayout.General, fragment.Sides.OldLayout);
        Assert.Equal(ImageLayout.General, fragment.Sides.NewLayout);
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit, fragment.Sides.SrcAccess);
    }

    [Fact]
    public void ASampledComputeReadAfterAnUploadNeedsNoBarrierOnceTheImageIsShaderReadable()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: true);
        Require(tracker, ResourceUsage.TransferDst);
        Require(tracker, ResourceUsage.SampleFragment);
        // The barrier into SHADER_READ_ONLY already made the upload available: read after read.
        Assert.Empty(Require(tracker, ResourceUsage.SampleCompute));
        Assert.Empty(Require(tracker, ResourceUsage.SampleCompute));
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            tracker.StateOf(0, 0).ReadStages);
    }

    [Fact]
    public void AMipChainReadsLevelNAndWritesLevelNPlusOneWithOneBarrierPerLevel()
    {
        const uint levels = 4;
        var tracker = new ResourceStateTracker(levels, 1, depth: false);

        // Level 0 is stored first (the prefilter's input copy).
        ImageTransition level0 = Assert.Single(Require(tracker, 0, 1, ResourceUsage.StorageWrite));
        Assert.Equal((0u, 1u), (level0.BaseMip, level0.MipCount));
        Assert.True(tracker.IsSplit);

        for (uint n = 0; n + 1 < levels; n++)
        {
            ImageTransition read = Assert.Single(Require(tracker, n, 1, ResourceUsage.SampleCompute));
            Assert.Equal((n, 1u), (read.BaseMip, read.MipCount));
            Assert.Equal(ImageLayout.General, read.Sides.OldLayout);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, read.Sides.NewLayout);
            Assert.Equal(AccessFlags2.ShaderStorageWriteBit, read.Sides.SrcAccess);

            ImageTransition write = Assert.Single(Require(tracker, n + 1, 1, ResourceUsage.StorageWrite));
            Assert.Equal((n + 1, 1u), (write.BaseMip, write.MipCount));
            Assert.Equal(ImageLayout.Undefined, write.Sides.OldLayout);
            Assert.Equal(ImageLayout.General, write.Sides.NewLayout);

            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, tracker.StateOf(n, 0).Layout);
            Assert.Equal(ImageLayout.General, tracker.StateOf(n + 1, 0).Layout);
        }

        // The consumer samples the whole chain: only the last level still needs a barrier.
        ImageTransition last = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal((levels - 1, 1u), (last.BaseMip, last.MipCount));
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit, last.Sides.SrcAccess);
        for (uint n = 0; n < levels; n++) Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, tracker.StateOf(n, 0).Layout);

        // The next frame's chain starts over: every level leaves SHADER_READ_ONLY in one rectangle.
        ImageTransition next = Assert.Single(Require(tracker, 0, 1, ResourceUsage.StorageWrite));
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, next.Sides.OldLayout);
    }

    private static Func<int, ComputeImageInfo?> Images(params (int Id, ComputeImageInfo Info)[] images)
    {
        var map = new Dictionary<int, ComputeImageInfo>();
        foreach ((int id, ComputeImageInfo info) in images) map[id] = info;
        return id => map.TryGetValue(id, out ComputeImageInfo info) ? info : null;
    }

    private static ComputePassDeclaration Pass(params ComputeBinding[] bindings) => new()
    {
        Name = "test",
        ProgramId = 1,
        Bindings = bindings,
        Dispatches = new[] { ComputeDispatch.Covering(0) },
    };

    [Fact]
    public void AMipChainPassValidatesAndAConflictingUseOfOneLevelDoesNot()
    {
        var images = Images((5, new ComputeImageInfo(64, 32, 5, 1)), (6, new ComputeImageInfo(8, 8, 1, 4)));

        Assert.Null(ComputePassPlanner.Validate(Pass(
            new ComputeBinding(1, 5, ComputeAccess.StorageWrite, BaseMip: 1),
            new ComputeBinding(0, 5, ComputeAccess.Sampled, BaseMip: 0)), images));

        Assert.Contains("two ways", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.StorageWrite, BaseMip: 1),
            new ComputeBinding(1, 5, ComputeAccess.Sampled, BaseMip: 0, MipCount: 2)), images));
        Assert.Contains("two ways", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.StorageWrite, BaseMip: 2),
            new ComputeBinding(1, 5, ComputeAccess.StorageWrite, BaseMip: 2)), images));
        // Two sampled reads of one level are one layout: allowed.
        Assert.Null(ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.Sampled),
            new ComputeBinding(1, 5, ComputeAccess.Sampled)), images));

        Assert.Contains("exactly one level", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.StorageWrite, MipCount: 2)), images));
        Assert.Contains("of a 5-level texture", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.Sampled, BaseMip: 4, MipCount: 2)), images));
        Assert.Contains("bound twice", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 5, ComputeAccess.Sampled),
            new ComputeBinding(0, 5, ComputeAccess.StorageWrite, BaseMip: 1)), images));
        Assert.Contains("no texture", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 99, ComputeAccess.StorageWrite)), images));
        Assert.Contains("layered", ComputePassPlanner.Validate(Pass(
            new ComputeBinding(0, 6, ComputeAccess.StorageWrite)), images));
        Assert.Contains("no dispatch", ComputePassPlanner.Validate(new ComputePassDeclaration
        {
            Bindings = new[] { new ComputeBinding(0, 5, ComputeAccess.StorageWrite) },
        }, images));
        Assert.Contains("binding index", ComputePassPlanner.Validate(new ComputePassDeclaration
        {
            Bindings = new[] { new ComputeBinding(0, 5, ComputeAccess.StorageWrite) },
            Dispatches = new[] { ComputeDispatch.Covering(3) },
        }, images));
    }

    [Theory]
    [InlineData(64u, 8u, 8u)]
    [InlineData(65u, 8u, 9u)]
    [InlineData(1u, 16u, 1u)]
    [InlineData(1920u, 16u, 120u)]
    [InlineData(1081u, 16u, 68u)]
    public void GroupCountsCoverTheImage(uint extent, uint local, uint groups) =>
        Assert.Equal(groups, ComputePassPlanner.GroupsCovering(extent, local));

    [Fact]
    public void ADispatchSizedFromABindingCoversThatLevel()
    {
        var images = Images((5, new ComputeImageInfo(100, 30, 5, 1)));
        ComputePassDeclaration pass = Pass(new ComputeBinding(0, 5, ComputeAccess.StorageWrite, BaseMip: 2));
        // Level 2 is 25x7: 4x1 groups of 8x8.
        Assert.Equal((4u, 1u, 1u), ComputePassPlanner.Groups(pass.Dispatches[0], pass, images, 8, 8));
        Assert.Equal((3u, 2u, 1u), ComputePassPlanner.Groups(ComputeDispatch.Explicit(3, 2), pass, images, 8, 8));
        Assert.Equal(1u, ComputePassPlanner.LevelExtent(3, 7));
    }

    [Fact]
    public void TheSignatureNamesWritesAsPersistentUsesAndReadsAsReads()
    {
        var images = Images((5, new ComputeImageInfo(64, 32, 5, 1)), (7, new ComputeImageInfo(64, 32, 1, 1)),
            (8, new ComputeImageInfo(64, 32, 1, 1)));
        PassSignature signature = ComputePassPlanner.Signature(3, Pass(
            new ComputeBinding(0, 7, ComputeAccess.Sampled),
            new ComputeBinding(1, 5, ComputeAccess.Sampled, BaseMip: 0),
            new ComputeBinding(2, 5, ComputeAccess.StorageWrite, BaseMip: 1),
            new ComputeBinding(3, 8, ComputeAccess.StorageReadWrite)), images);

        Assert.Equal(3, signature.NameId);
        Assert.Equal(new[] { 7, 5, 8 }, signature.Reads);
        Assert.Equal(new[]
        {
            new AttachmentUse(5, ResourceUsage.StorageWrite, false),
            new AttachmentUse(8, ResourceUsage.StorageReadWrite, false),
        }, signature.Attachments);
        Assert.Equal((32, 16), (signature.Width, signature.Height));

        // A raster pass that attaches the dispatch's output later must load it, never DONT_CARE.
        var raster = new PassSignature
        {
            NameId = 4, Width = 64, Height = 32, FormatsId = 1,
            Attachments = new[] { new AttachmentUse(8, ResourceUsage.ColorWrite, true) },
        };
        FramePlan plan = FramePlan.Build(new[] { signature, raster });
        Assert.Equal(AttachmentLoadOp.Load, plan.LoadOp(1, 0));
        Assert.Equal(-1, plan.AliasSlot(8));
    }

    [SkippableFact]
    public void TheWorkGroupSizeIsReadFromTheModule()
    {
        Shaders.ShaderCompiler compiler;
        try
        {
            compiler = new Shaders.ShaderCompiler();
        }
        catch (Exception error) when (error is DllNotFoundException or InvalidOperationException)
        {
            throw new SkipException("shaderc unavailable: " + error.Message);
        }
        using (compiler)
        {
            Shaders.ShaderCompileResult literal = compiler.CompileCompute("""
                #version 450
                layout(local_size_x = 16, local_size_y = 4, local_size_z = 2) in;
                void main() {}
                """, "literal.comp");
            Assert.True(literal.Success, literal.Error);
            Assert.True(SpirvLocalSize.TryRead(literal.Spirv, out uint x, out uint y, out uint z));
            Assert.Equal((16u, 4u, 2u), (x, y, z));

            // A specialization-constant size is not a literal: the description's size applies.
            Shaders.ShaderCompileResult specialized = compiler.CompileCompute("""
                #version 450
                layout(local_size_x_id = 0, local_size_y_id = 1) in;
                void main() {}
                """, "specialized.comp");
            Assert.True(specialized.Success, specialized.Error);
            Assert.False(SpirvLocalSize.TryRead(specialized.Spirv, out _, out _, out _));
        }
        Assert.False(SpirvLocalSize.TryRead(new byte[16], out _, out _, out _));
    }

    [Fact]
    public void AStorageFormatFallsBackToAWiderFormatOfItsKindAndFinallyRgba8()
    {
        const FormatFeatureFlags storage = StorageFormats.Required;
        Func<Format, FormatFeatureFlags> only(params Format[] supported) =>
            format => Array.IndexOf(supported, format) >= 0 ? storage : FormatFeatureFlags.SampledImageBit;

        Assert.Equal(Format.R8Unorm, StorageFormats.Choose(Format.R8Unorm, only(Format.R8Unorm, Format.R8G8B8A8Unorm)));
        Assert.Equal(Format.R8G8B8A8Unorm, StorageFormats.Choose(Format.R8Unorm, only(Format.R8G8B8A8Unorm)));
        Assert.Equal(Format.R32Sfloat, StorageFormats.Choose(Format.R16Sfloat, only(Format.R32Sfloat)));
        Assert.Equal(Format.R32G32B32A32Sfloat, StorageFormats.Choose(Format.R32Sfloat, only(Format.R32G32B32A32Sfloat)));
        Assert.Equal(Format.R8G8B8A8Unorm, StorageFormats.Choose(Format.R32Sfloat, only()));
        // Storage alone is not enough: the next candidate that also samples wins.
        Assert.Equal(Format.R8G8Unorm, StorageFormats.Choose(Format.R8Unorm,
            format => format == Format.R8Unorm ? FormatFeatureFlags.StorageImageBit : storage));
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/FrameGraphUnitTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

/// <summary>
/// The frame graph's bookkeeping without a device: which plan a streamed frame is matched
/// against (the last two frames, so the TAA history ping-pong hits), the load op a pass
/// gets while the frame so far matches and after it stops matching, and the pending-clear
/// table behind clear promotion.
/// </summary>
public class FrameGraphUnitTests
{
    private static PassSignature Pass(int name, int resource, bool transient) => new()
    {
        NameId = name,
        Attachments = new[]
        {
            new AttachmentUse(resource, transient ? ResourceUsage.ColorWrite : ResourceUsage.ColorBlend, transient),
        },
        Width = 8,
        Height = 8,
        FormatsId = 1,
    };

    [Fact]
    public void AFrameCycleOfTwoHitsThePlanFromTwoFramesAgo()
    {
        var graph = new FrameGraph { Enabled = true };
        var loads = new List<AttachmentLoadOp>();
        for (int frame = 0; frame < 6; frame++)
        {
            int resource = frame % 2 == 0 ? 10 : 11;
            int index = graph.OpenPass(Pass(1, resource, transient: true), declared: true);
            loads.Add(graph.PlannedLoad(index, 0));
            graph.EndFrame();
        }

        Assert.Equal(4, graph.PlanHits);
        Assert.Equal(2, graph.PlanMisses);
        Assert.Equal(new[]
        {
            AttachmentLoadOp.Load, AttachmentLoadOp.Load, AttachmentLoadOp.DontCare,
            AttachmentLoadOp.DontCare, AttachmentLoadOp.DontCare, AttachmentLoadOp.DontCare,
        }, loads);
    }

    [Fact]
    public void APassThatStopsMatchingMakesTheRestOfTheFrameConservative()
    {
        var graph = new FrameGraph { Enabled = true };
        graph.OpenPass(Pass(1, 10, transient: true), declared: true);
        graph.OpenPass(Pass(2, 20, transient: true), declared: true);
        graph.EndFrame();

        int first = graph.OpenPass(Pass(1, 10, transient: true), declared: true);
        Assert.Equal(AttachmentLoadOp.DontCare, graph.PlannedLoad(first, 0));
        int changed = graph.OpenPass(Pass(3, 30, transient: true), declared: true);
        Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(changed, 0));
        // The same pass as last frame, but after a mismatch: no DONT_CARE.
        int later = graph.OpenPass(Pass(2, 20, transient: true), declared: true);
        Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(later, 0));
        graph.EndFrame();

        Assert.Equal(0, graph.PlanHits);
        Assert.Equal(2, graph.PlanMisses);
    }

    [Fact]
    public void PersistentAttachmentsAlwaysLoad()
    {
        var graph = new FrameGraph { Enabled = true };
        for (int frame = 0; frame < 3; frame++)
        {
            int index = graph.OpenPass(Pass(1, 10, transient: false), declared: true);
            Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(index, 0));
            graph.EndFrame();
        }
        Assert.Equal(2, graph.PlanHits);
    }

    [Fact]
    public void PendingClearsAreReplacedTakenInOrderAndDropped()
    {
        var graph = new FrameGraph { Enabled = true };
        var a = new VulkanTexture(null!);
        var b = new VulkanTexture(null!);

        graph.PromoteColorClear(a, 0, 1f, 0f, 0f, 1f);
        graph.PromoteColorClear(a, 0, 0f, 1f, 0f, 1f);
        graph.PromoteColorClear(a, 1, 0f, 0f, 1f, 1f);
        graph.PromoteDepthClear(b, 1f);
        Assert.True(graph.HasPendingClear(a));
        Assert.True(graph.HasPendingClear(b));

        // Layer 0's second clear replaced its first; layer 2 has none.
        Assert.False(graph.TakeForLoad(a, 2, depth: false, out _));
        Assert.True(graph.TakeForLoad(a, 0, depth: false, out PendingClear taken));
        Assert.Equal(1f, taken.G);
        Assert.Equal(1, graph.PromotedClears);

        var standalone = new List<PendingClear>();
        graph.TakeStandalone(a, standalone);
        Assert.Single(standalone);
        Assert.Equal(1u, standalone[0].Layer);
        Assert.False(graph.HasPendingClear(a));

        graph.Drop(b);
        Assert.False(graph.HasPendingClears);
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/FramePlanTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

/// <summary>
/// Pure tests for the frame plan (Phase 2, contract C2): signature matching, load/store
/// solving and transient alias placement. No device.
/// </summary>
public class FramePlanTests
{
    // Resource ids for the TAA-shaped frame.
    private const int Primary = 1;
    private const int Depth = 2;
    private const int Motion = 3;
    private const int HistoryOut = 4;
    private const int Resolved = 5;
    private const int Sharpened = 6;
    private const int Bloom1 = 7;
    private const int Bloom2 = 8;
    private const int Final = 9;
    private const int HistoryIn = 10;
    private const int Swapchain = 11;
    private const int BloomDepth = 12;

    // Pass indices.
    private const int Opaque = 0;
    private const int SkyMotion = 1;
    private const int TaaResolve = 2;
    private const int TaaSharpen = 3;
    private const int BloomDown = 4;
    private const int BloomUp = 5;
    private const int FinalComposition = 6;
    private const int Blit = 7;

    private static AttachmentUse Use(int id, ResourceUsage usage, bool transient = false) => new(id, usage, transient);

    private static PassSignature Pass(int name, AttachmentUse[] attachments, int[] reads, int width = 1920, int height = 1080, int formats = 1) =>
        new() { NameId = name, Attachments = attachments, Reads = reads, Width = width, Height = height, FormatsId = formats };

    /// <summary>Opaque, sky motion, TAA resolve and sharpen, a two-step bloom chain on
    /// transients, final composition and the blit.</summary>
    private static List<PassSignature> TaaFrame() => new()
    {
        Pass(100, new[] { Use(Primary, ResourceUsage.ColorWrite), Use(Motion, ResourceUsage.ColorWrite), Use(Depth, ResourceUsage.DepthWrite) },
            Array.Empty<int>(), formats: 7),
        Pass(101, new[] { Use(Motion, ResourceUsage.ColorBlend), Use(Depth, ResourceUsage.DepthReadOnly) },
            Array.Empty<int>(), formats: 8),
        Pass(102, new[] { Use(Resolved, ResourceUsage.ColorWrite, true), Use(HistoryOut, ResourceUsage.ColorWrite) },
            new[] { Primary, Motion, Depth, HistoryIn }, formats: 2),
        Pass(103, new[] { Use(Sharpened, ResourceUsage.ColorWrite, true) }, new[] { Resolved }, formats: 2),
        Pass(104, new[] { Use(Bloom1, ResourceUsage.ColorWrite, true), Use(BloomDepth, ResourceUsage.DepthWrite, true) },
            new[] { Sharpened }, formats: 2),
        Pass(105, new[] { Use(Bloom2, ResourceUsage.ColorWrite, true) }, new[] { Bloom1 }, formats: 2),
        Pass(106, new[] { Use(Final, ResourceUsage.ColorWrite) }, new[] { Sharpened, Bloom2 }, formats: 3),
        Pass(107, new[] { Use(Swapchain, ResourceUsage.ColorWrite) }, new[] { Final }, formats: 4),
    };

    [Fact]
    public void IdenticalFrameMatches()
    {
        FramePlan plan = FramePlan.Build(TaaFrame());
        Assert.True(plan.Matches(TaaFrame()));
        Assert.False(plan.IsConservative);
        Assert.Equal(8, plan.PassCount);
        for (int p = 0; p < plan.PassCount; p++)
            Assert.True(plan.MatchesPass(p, TaaFrame()[p]));
        Assert.False(plan.MatchesPass(8, TaaFrame()[0]));
    }

    [Fact]
    public void NullArraysMatchEmptyArrays()
    {
        var withEmpty = new List<PassSignature> { Pass(1, Array.Empty<AttachmentUse>(), Array.Empty<int>()) };
        var withNull = new List<PassSignature> { new() { NameId = 1, Attachments = null!, Reads = null!, Width = 1920, Height = 1080, FormatsId = 1 } };
        Assert.True(FramePlan.Build(withEmpty).Matches(withNull));
        Assert.True(FramePlan.Build(withNull).Matches(withEmpty));
    }

    public static IEnumerable<object[]> Mismatches()
    {
        yield return new object[] { "pass removed", (Action<List<PassSignature>>)(f => f.RemoveAt(BloomUp)) };
        yield return new object[] { "pass added", (Action<List<PassSignature>>)(f => f.Add(Pass(999, Array.Empty<AttachmentUse>(), Array.Empty<int>()))) };
        yield return new object[] { "passes reordered", (Action<List<PassSignature>>)(f => { (f[BloomDown], f[BloomUp]) = (f[BloomUp], f[BloomDown]); }) };
        yield return new object[] { "name", (Action<List<PassSignature>>)(f => f[TaaSharpen].NameId = 555) };
        yield return new object[] { "attachment added", (Action<List<PassSignature>>)(f => f[TaaSharpen].Attachments = new[] { Use(Sharpened, ResourceUsage.ColorWrite, true), Use(Motion, ResourceUsage.ColorWrite) }) };
        yield return new object[] { "attachment removed", (Action<List<PassSignature>>)(f => f[Opaque].Attachments = new[] { Use(Primary, ResourceUsage.ColorWrite), Use(Depth, ResourceUsage.DepthWrite) }) };
        yield return new object[] { "attachment resource", (Action<List<PassSignature>>)(f => f[Blit].Attachments[0] = Use(Final + 100, ResourceUsage.ColorWrite)) };
        yield return new object[] { "attachment order", (Action<List<PassSignature>>)(f => f[TaaResolve].Attachments = new[] { Use(HistoryOut, ResourceUsage.ColorWrite), Use(Resolved, ResourceUsage.ColorWrite, true) }) };
        yield return new object[] { "usage", (Action<List<PassSignature>>)(f => f[SkyMotion].Attachments[1] = Use(Depth, ResourceUsage.DepthReadOnlySampled)) };
        yield return new object[] { "transient flag", (Action<List<PassSignature>>)(f => f[BloomUp].Attachments[0] = Use(Bloom2, ResourceUsage.ColorWrite, false)) };
        yield return new object[] { "read added", (Action<List<PassSignature>>)(f => f[FinalComposition].Reads = new[] { Sharpened, Bloom2, Depth }) };
        yield return new object[] { "read removed", (Action<List<PassSignature>>)(f => f[FinalComposition].Reads = new[] { Sharpened }) };
        yield return new object[] { "read changed", (Action<List<PassSignature>>)(f => f[TaaSharpen].Reads = new[] { Primary }) };
        yield return new object[] { "read order", (Action<List<PassSignature>>)(f => f[FinalComposition].Reads = new[] { Bloom2, Sharpened }) };
        yield return new object[] { "width", (Action<List<PassSignature>>)(f => f[BloomDown].Width = 960) };
        yield return new object[] { "height", (Action<List<PassSignature>>)(f => f[BloomDown].Height = 540) };
        yield return new object[] { "formats", (Action<List<PassSignature>>)(f => f[Opaque].FormatsId = 70) };
    }

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void EveryFieldChangeIsAMismatch(string field, object mutation)
    {
        // xunit needs public parameter types; the signature types are internal.
        var mutate = (Action<List<PassSignature>>)mutation;
        FramePlan plan = FramePlan.Build(TaaFrame());
        List<PassSignature> changed = TaaFrame();
        mutate(changed);
        Assert.False(plan.Matches(changed), $"A change in '{field}' still matched.");
    }

    [Fact]
    public void PlanIsASnapshotOfTheSignatures()
    {
        List<PassSignature> frame = TaaFrame();
        FramePlan plan = FramePlan.Build(frame);
        frame[TaaResolve].Reads[0] = 77;
        frame[Opaque].Attachments[0] = Use(77, ResourceUsage.ColorWrite);
        Assert.True(plan.Matches(TaaFrame()));
        Assert.False(plan.Matches(frame));
    }

    [Fact]
    public void TaaFrameLoadStoreSolve()
    {
        FramePlan plan = FramePlan.Build(TaaFrame());
        const AttachmentLoadOp L = AttachmentLoadOp.Load, LX = AttachmentLoadOp.DontCare;
        const AttachmentStoreOp S = AttachmentStoreOp.Store, SX = AttachmentStoreOp.DontCare;

        // Persistent attachments always load and store, including the history the next frame reads.
        AssertOps(plan, Opaque, 0, L, S);
        AssertOps(plan, Opaque, 1, L, S);
        AssertOps(plan, Opaque, 2, L, S);
        AssertOps(plan, SkyMotion, 0, L, S);
        AssertOps(plan, SkyMotion, 1, L, S);
        AssertOps(plan, TaaResolve, 1, L, S);
        AssertOps(plan, FinalComposition, 0, L, S);
        AssertOps(plan, Blit, 0, L, S);

        // Transients: first write does not load; stored because a later pass reads them.
        AssertOps(plan, TaaResolve, 0, LX, S);
        AssertOps(plan, TaaSharpen, 0, LX, S);
        AssertOps(plan, BloomDown, 0, LX, S);
        AssertOps(plan, BloomUp, 0, LX, S);

        // A transient nobody reads after its only pass is neither loaded nor stored.
        AssertOps(plan, BloomDown, 1, LX, SX);

        Assert.Equal(-1, plan.AliasSlot(Primary));
        Assert.Equal(-1, plan.AliasSlot(HistoryOut));
        Assert.Equal(-1, plan.AliasSlot(HistoryIn));
        Assert.Equal(-1, plan.AliasSlot(12345));
    }

    [Fact]
    public void TransientLastAttachedIsNotStoredButLoadsInLaterPasses()
    {
        const int scratch = 50;
        var frame = new List<PassSignature>
        {
            Pass(1, new[] { Use(scratch, ResourceUsage.ColorWrite, true) }, Array.Empty<int>()),
            Pass(2, new[] { Use(scratch, ResourceUsage.ColorBlend, true) }, Array.Empty<int>()),
            Pass(3, new[] { Use(scratch, ResourceUsage.ColorWrite, true) }, Array.Empty<int>()),
        };
        FramePlan plan = FramePlan.Build(frame);
        AssertOps(plan, 0, 0, AttachmentLoadOp.DontCare, AttachmentStoreOp.Store);
        AssertOps(plan, 1, 0, AttachmentLoadOp.Load, AttachmentStoreOp.Store);
        AssertOps(plan, 2, 0, AttachmentLoadOp.Load, AttachmentStoreOp.DontCare);
        Assert.Equal(0, plan.AliasSlot(scratch));
    }

    public static IEnumerable<object[]> NotReallyTransient()
    {
        const int r = 60;
        // Read before it is written this frame: it depends on last frame's contents.
        yield return new object[] { "read first", new List<PassSignature>
        {
            Pass(1, new[] { Use(99, ResourceUsage.ColorWrite) }, new[] { r }),
            Pass(2, new[] { Use(r, ResourceUsage.ColorWrite, true) }, Array.Empty<int>()),
        }, 1 };
        // Blended into on first use: the destination is read.
        yield return new object[] { "blend first", new List<PassSignature>
        {
            Pass(1, new[] { Use(r, ResourceUsage.ColorBlend, true) }, Array.Empty<int>()),
            Pass(2, new[] { Use(99, ResourceUsage.ColorWrite) }, new[] { r }),
        }, 0 };
        // Depth tested read-only on first use.
        yield return new object[] { "depth read first", new List<PassSignature>
        {
            Pass(1, new[] { Use(r, ResourceUsage.DepthReadOnly, true) }, Array.Empty<int>()),
            Pass(2, new[] { Use(r, ResourceUsage.DepthWrite, true) }, Array.Empty<int>()),
        }, 0 };
        // Sampled by the pass that first writes it (feedback).
        yield return new object[] { "feedback first", new List<PassSignature>
        {
            Pass(1, new[] { Use(r, ResourceUsage.ColorWrite, true) }, new[] { r }),
        }, 0 };
        // One use is not marked transient.
        yield return new object[] { "mixed flag", new List<PassSignature>
        {
            Pass(1, new[] { Use(r, ResourceUsage.ColorWrite, true) }, Array.Empty<int>()),
            Pass(2, new[] { Use(r, ResourceUsage.ColorBlend, false) }, Array.Empty<int>()),
        }, 0 };
    }

    [Theory]
    [MemberData(nameof(NotReallyTransient))]
    public void TransientThatDependsOnOlderContentsStaysPersistent(string why, object frameObject, int firstAttachedPass)
    {
        var frame = (List<PassSignature>)frameObject;
        const int r = 60;
        FramePlan plan = FramePlan.Build(frame);
        Assert.True(plan.AliasSlot(r) == -1, why);
        for (int p = 0; p < frame.Count; p++)
        {
            for (int a = 0; a < frame[p].Attachments.Length; a++)
            {
                if (frame[p].Attachments[a].ResourceId != r) continue;
                Assert.True(plan.LoadOp(p, a) == AttachmentLoadOp.Load, $"{why}: pass {p} (first attached {firstAttachedPass}) does not load.");
                Assert.True(plan.StoreOp(p, a) == AttachmentStoreOp.Store, $"{why}: pass {p} does not store.");
            }
        }
    }

    [Fact]
    public void TaaFrameAliasesDisjointTransientsAndNeverOverlapping()
    {
        List<PassSignature> frame = TaaFrame();
        FramePlan plan = FramePlan.Build(frame);

        // Resolved [2,3] and Bloom1 [4,5] share bucket (1920x1080, formats 2, index 0) and are disjoint.
        Assert.Equal(plan.AliasSlot(Resolved), plan.AliasSlot(Bloom1));
        // Bloom depth has another attachment index, so another bucket and its own slot.
        Assert.NotEqual(plan.AliasSlot(Resolved), plan.AliasSlot(BloomDepth));
        Assert.Equal(4, plan.AliasSlotCount);

        AssertNoOverlap(frame, plan, new[] { Resolved, Sharpened, Bloom1, Bloom2, BloomDepth });
    }

    [Fact]
    public void DifferentExtentOrFormatsNeverShareASlot()
    {
        var frame = new List<PassSignature>
        {
            Pass(1, new[] { Use(20, ResourceUsage.ColorWrite, true) }, Array.Empty<int>(), width: 960, height: 540),
            Pass(2, new[] { Use(21, ResourceUsage.ColorWrite, true) }, new[] { 20 }, width: 480, height: 270),
            Pass(3, new[] { Use(22, ResourceUsage.ColorWrite, true) }, new[] { 21 }, width: 480, height: 270, formats: 9),
            Pass(4, new[] { Use(23, ResourceUsage.ColorWrite, true) }, new[] { 22 }, width: 960, height: 540),
        };
        FramePlan plan = FramePlan.Build(frame);
        // 20 [0,1] and 23 [3,3] share the 960x540 bucket; 21 and 22 differ in extent/formats.
        Assert.Equal(plan.AliasSlot(20), plan.AliasSlot(23));
        Assert.Equal(3, plan.AliasSlotCount);
        Assert.NotEqual(plan.AliasSlot(21), plan.AliasSlot(22));
        Assert.NotEqual(plan.AliasSlot(20), plan.AliasSlot(21));
    }

    [Fact]
    public void RandomIntervalsNeverOverlapAndUseMinimalSlots()
    {
        var random = new Random(1234);
        var buckets = new[]
        {
            new SizeBucket(1920, 1080, 1, 0),
            new SizeBucket(960, 540, 1, 0),
            new SizeBucket(1920, 1080, 2, 0),
        };

        for (int round = 0; round < 500; round++)
        {
            int count = random.Next(0, 40);
            var intervals = new List<TransientInterval>(count);
            for (int i = 0; i < count; i++)
            {
                int first = random.Next(0, 30);
                intervals.Add(new TransientInterval(i, buckets[random.Next(buckets.Length)], first, first + random.Next(0, 8)));
            }

            int[] slots = TransientPlacement.Place(intervals);
            Assert.Equal(count, slots.Length);

            var slotBucket = new Dictionary<int, SizeBucket>();
            for (int i = 0; i < count; i++)
            {
                if (slotBucket.TryGetValue(slots[i], out SizeBucket existing))
                    Assert.Equal(existing, intervals[i].Bucket);
                else
                    slotBucket[slots[i]] = intervals[i].Bucket;

                for (int j = i + 1; j < count; j++)
                {
                    if (slots[i] != slots[j]) continue;
                    bool overlap = intervals[i].FirstPass <= intervals[j].LastPass && intervals[j].FirstPass <= intervals[i].LastPass;
                    Assert.False(overlap, $"round {round}: {intervals[i]} and {intervals[j]} share slot {slots[i]}");
                }
            }

            // Slots are dense and, per bucket, equal to the peak number of live intervals.
            for (int s = 0; s < slotBucket.Count; s++) Assert.True(slotBucket.ContainsKey(s));
            foreach (SizeBucket bucket in buckets)
            {
                int peak = 0;
                for (int pass = 0; pass < 40; pass++)
                {
                    int live = 0;
                    foreach (TransientInterval t in intervals)
                        if (t.Bucket == bucket && t.FirstPass <= pass && pass <= t.LastPass) live++;
                    peak = Math.Max(peak, live);
                }
                int used = 0;
                foreach (KeyValuePair<int, SizeBucket> entry in slotBucket)
                    if (entry.Value == bucket) used++;
                Assert.Equal(peak, used);
            }
        }
    }

    [Fact]
    public void PlacementRejectsInvertedIntervals()
    {
        Assert.Throws<ArgumentException>(() =>
            TransientPlacement.Place(new[] { new TransientInterval(1, new SizeBucket(1, 1, 1, 0), 3, 2) }));
    }

    [Fact]
    public void ChangedFrameGetsConservativePlan()
    {
        FramePlan previous = FramePlan.Build(TaaFrame());

        List<PassSignature> changed = TaaFrame();
        changed.RemoveAt(BloomUp); // bloom toggled: the chain is one pass shorter
        changed[BloomUp].Reads = new[] { Sharpened, Bloom1 };

        FramePlan applied = FramePlan.Select(previous, changed);
        Assert.NotSame(previous, applied);
        Assert.True(applied.IsConservative);
        Assert.True(applied.Matches(changed));
        Assert.Equal(0, applied.AliasSlotCount);
        for (int p = 0; p < changed.Count; p++)
        {
            for (int a = 0; a < changed[p].Attachments.Length; a++)
            {
                Assert.Equal(AttachmentLoadOp.Load, applied.LoadOp(p, a));
                Assert.Equal(AttachmentStoreOp.Store, applied.StoreOp(p, a));
                Assert.Equal(-1, applied.AliasSlot(changed[p].Attachments[a].ResourceId));
            }
        }

        // The same frame again reuses the plan built from it; no previous plan is conservative.
        Assert.Same(previous, FramePlan.Select(previous, TaaFrame()));
        Assert.True(FramePlan.Select(null, TaaFrame()).IsConservative);

        // The next frame's plan, built from the changed frame, is solved again.
        FramePlan rebuilt = FramePlan.Build(changed);
        Assert.False(rebuilt.IsConservative);
        Assert.Equal(AttachmentStoreOp.DontCare, rebuilt.StoreOp(BloomDown, 1));
    }

    [Fact]
    public void OutOfRangeIndicesThrow()
    {
        FramePlan plan = FramePlan.Build(TaaFrame());
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.LoadOp(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.LoadOp(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.StoreOp(TaaSharpen, 1));
    }

    private static void AssertOps(FramePlan plan, int pass, int attachment, AttachmentLoadOp load, AttachmentStoreOp store)
    {
        Assert.True(load == plan.LoadOp(pass, attachment), $"pass {pass} attachment {attachment}: load {plan.LoadOp(pass, attachment)}, expected {load}");
        Assert.True(store == plan.StoreOp(pass, attachment), $"pass {pass} attachment {attachment}: store {plan.StoreOp(pass, attachment)}, expected {store}");
    }

    private static void AssertNoOverlap(List<PassSignature> frame, FramePlan plan, int[] transients)
    {
        var first = new Dictionary<int, int>();
        var last = new Dictionary<int, int>();
        for (int p = 0; p < frame.Count; p++)
        {
            var ids = new List<int>();
            foreach (AttachmentUse use in frame[p].Attachments) ids.Add(use.ResourceId);
            ids.AddRange(frame[p].Reads);
            foreach (int id in ids)
            {
                if (!first.ContainsKey(id)) first[id] = p;
                last[id] = p;
            }
        }

        foreach (int a in transients)
        {
            Assert.True(plan.AliasSlot(a) >= 0, $"transient {a} has no slot");
            foreach (int b in transients)
            {
                if (a >= b || plan.AliasSlot(a) != plan.AliasSlot(b)) continue;
                Assert.False(first[a] <= last[b] && first[b] <= last[a], $"{a} [{first[a]},{last[a]}] and {b} [{first[b]},{last[b]}] share slot {plan.AliasSlot(a)}");
            }
        }
    }
}
}
