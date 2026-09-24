// Source: Optimum.Tests/frame-graph-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 2 step 2 (the streaming frame graph) at source level: the env
/// switch, one scope per pass through the pass recorder, clear promotion with its standalone
/// fallback, the platform declaring passes from the stage bracket, its binds and its post
/// methods, and the stats tokens. The pixels and scope counts are proven by
/// Optimum.Render.Vulkan.Tests/FrameGraphFrameTests.cs.
/// </summary>
public class FrameGraphCoverageTests
{
    [Fact]
    public void TheFrameGraphHasAnOffSwitchAndSolvesLoadOpsFromThePlan()
    {
        string graph = Read("Optimum.Render.Vulkan/Graph/FrameGraph.cs");
        Assert.Contains("public const string Variable = \"OPTIMUM_VULKAN_FRAMEGRAPH\";", graph);
        Assert.Contains("Environment.GetEnvironmentVariable(Variable) != \"0\"", graph);
        Assert.Contains("plan.MatchesPass(index, signature)", graph);
        Assert.Contains("_plans[0] = FramePlan.Build(_frame);", graph);

        string recorder = Read("Optimum.Render.Vulkan/Graph/PassRecorder.cs");
        Assert.Contains("_graph.PlannedLoad(passIndex, use)", recorder);
        Assert.Contains("attachments[i].LoadOp = AttachmentLoadOp.Clear;", recorder);
        Assert.Contains("CmdClearColorImage(", recorder);
        Assert.Contains("_graph.NoteSplit(allowed);", recorder);
    }

    [Fact]
    public void ScopesOpenThroughThePassRecorderAndClearsArePromoted()
    {
        string targets = Read("Optimum.Render.Vulkan/Core/RenderTargetManager.cs");
        Assert.Contains("_recorder.Prepare(commandBuffer, framebuffer, scopeColour!, scopeDepth, DepthReadOnly,", targets);
        Assert.Contains("public void DeclarePass(CommandBuffer commandBuffer, PassDeclaration declaration, int framebufferId)", targets);
        Assert.Contains("_graph.PromoteColorClear(texture, target.Color[attachment].Layer, r, g, b, a);", targets);
        Assert.Contains("_graph.PromoteDepthClear(texture, depth);", targets);
        Assert.Contains("_graph.NoteInPassClear();", targets);
        // The masked-out clear stays a no-op before either path, dropped where the mask is stated.
        Assert.Contains("stated.ColorMask == 0) return;",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeStated.cs"));

        string device = VulkanDeviceSource.Read();
        Assert.Contains("_targets.FlushAllPendingClears(_frames.Current.CommandBuffer);", device);
        Assert.Contains("if (_graph.Enabled) _graph.EndFrame();", device);
        Assert.Contains("_targets.FlushPendingClears(commandBuffer, texture);", Read("Optimum.Render.Vulkan/VulkanDevice.Native.cs"));
        Assert.Contains("_targets.FlushPendingClears(Commands, texture);", device);
    }

    [Fact]
    public void ASlotTheDeclaredPassLeavesOutIsTreatedAsOutsideTheScope()
    {
        // Phase 2 review: sampling a left-out slot neither splits nor takes a ReadSelf copy,
        // and a clear on it (draw buffer on) is promoted instead of dropped.
        // GPU proof: Optimum.Render.Vulkan.Tests/PassExclusionTests.cs.
        string targets = Read("Optimum.Render.Vulkan/Core/RenderTargetManager.cs");
        Assert.Contains("private void ApplyPassExclusion(CommandBuffer commandBuffer, VulkanFramebuffer target, uint colorSlots)", targets);
        Assert.Contains("if (((_bound.PassExclusion >> i) & 1) != 0) continue;", targets);
        Assert.Contains("if (((target.PassExclusion >> attachment) & 1) != 0)", targets);
    }

    [Fact]
    public void ThePlatformDeclaresThePassesOfTheFrame()
    {
        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        foreach (string member in new[]
                 {
                     "public override void MergeTransparentRenderPass()",
                     "public override bool RenderOptimumSkyMotion()",
                     "public override void RenderPostprocessingEffects(float[] projectMatrix)",
                     "public override bool RenderOptimumTaaResolve()",
                     "public override int RenderOptimumTaaSharpen(int resolvedScene)",
                     "public override void RenderFinalComposition()",
                     "public override void BlitPrimaryToDefault()",
                 })
        {
            Assert.Contains(member, graph);
        }
        Assert.Contains("PassFlags.OpenSampling | PassFlags.AllowSplit", graph);
        // Every pass is declared by the native route that records it; the stage bracket ends
        // whatever pass a stage left open.
        Assert.Contains("platform.GraphDevice?.EndStagePass();", graph);
        Assert.Contains("const uint slots = ~(1u << 1);",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativePostFinal.cs"));
        Assert.Contains("Name = declared?.Name ?? \"Stated/\" + framebufferId,", Read("Optimum.Render.Vulkan/Platform/StatedDraw.cs"));

        string main = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        Assert.Contains("RenderStageListener = new FrameGraphStageListener(this);", main);
        Assert.Contains("new(true, \"RenderOptimumTaaResolve\", Array.Empty<string>()),", main);
    }

    [Fact]
    public void TheStatsLineCarriesTheFrameGraphCounters()
    {
        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("\"passes={12} plan_hits={13} plan_misses={14} in_pass_clears={15} promoted_clears={16} \"", stats);
        Assert.Contains("\"standalone_clears={17} pass_splits={18} push_constants={19} storage_set_binds={20} \"", stats);
        Assert.Contains("compute_passes={23} dispatches={24}", stats);

        string doc = Read("docs/taa-acceptance.md");
        Assert.Contains("OPTIMUM_VULKAN_FRAMEGRAPH=0", doc);
    }

    private static string Read(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "VintageStory.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, relativePath));
    }
}
}

// Source: Optimum.Tests/persistent-upload-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public class PersistentUploadCoverageTests
{
    [Fact]
    public void PersistentUploadMethodsAreRegisteredWithExactSignatures()
    {
        string source = PatcherSource.Read();

        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"updateVAO\", 6", source);
        Assert.Contains("\"System.Single[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"System.Int32[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"System.Int16[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"System.UInt16[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"System.Byte[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"updateIndices\", 5", source);
        Assert.Contains("\"Vintagestory.Client.NoObf.VAO\", \"System.Boolean\"", source);
    }

    [Fact]
    public void PersistentUploadPatchCopiesAllVertexAndIndexBuffersInBlocks()
    {
        string patch = File.ReadAllText(FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch"));

        Assert.Equal(6, Count(patch, "Unsafe.CopyBlockUnaligned"));
        Assert.DoesNotContain("RecordPersistentMappedUpload", patch);
        Assert.DoesNotContain("RecordChunkUpload(byteCount", patch);
        Assert.Contains("byte* dest = (byte*)vao.indicesPtr + IndicesOffset;", patch);
        Assert.Contains("GL.BufferSubData<int>((BufferTarget)34963", patch);
    }

    [Fact]
    public void PersistentUploadDonorMatchesThePatchAndPreservesTheFallback()
    {
        string donor = File.ReadAllText(FindRepositoryFile(
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs"));

        Assert.Equal(6, Count(donor, "Unsafe.CopyBlockUnaligned"));
        Assert.DoesNotContain("RecordPersistentMappedUpload", donor);
        Assert.DoesNotContain("*(indicesPtr++) = Indices[i];", donor);
        Assert.Contains("GL.BufferSubData<int>((BufferTarget)34963", donor);
        Assert.Contains("vao.IndicesCount = IndicesCount;", donor);
    }

    [Fact]
    public void ChunkAndPersistentUploadCountersRemainSeparate()
    {
        string diagnostics = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Config/OptimumConfig.cs"));
        string pool = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Client/MeshPool/MeshDataPool.cs"));

        Assert.Contains("RecordChunkUpload(int bytes, long ticks)", diagnostics);
        Assert.Contains("RecordChunkUpload(uploadBytes, uploadTicks)", pool);
        Assert.DoesNotContain("RecordPersistentMappedUpload", diagnostics);
    }

    private static int Count(string source, string value)
    {
        return (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
}

// Source: Optimum.Tests/transient-allocator-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

/// <summary>
/// Phase 2 step 4 (transient allocator) at source level: the post-chain slots opt in through
/// the Transient pool class, aliasing is env-gated and default off, an aliased lease discards,
/// ReadSelf copies are pooled instead of kept per texture, and the stats line exists.
/// The pixels are proven by Optimum.Render.Vulkan.Tests/TransientAllocatorTests.cs.
/// </summary>
public class TransientAllocatorCoverageTests
{
    [Fact]
    public void ThePostChainSlotsOptIn()
    {
        string platform = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains("device.CreateTransientTexture2DRaw(ssaoWidth, ssaoHeight, 6407, 13);", platform);
        // Every other post-chain slot is built by CreateOptimumColorTarget, whose texture is transient.
        foreach (int slot in new[] { 2, 3, 4, 7, 8, 9, 10, 14, 15 })
            Assert.Contains("list[" + slot + "] = CreateOptimumColorTarget(", platform);
        Assert.Contains("list[OptimumTaaSharpenIndex] = CreateOptimumColorTarget(", platform);
        Assert.Contains("list[OptimumFsrFramebufferIndex] = CreateOptimumColorTarget(", platform);
        Assert.Contains("target.ColorTextureIds[0] = device.CreateTransientTexture2D(width, height, format, -1);", platform);
        Assert.Contains("foreach (int transientSlot in Graph.TransientAllocator.PostChainSlots)", platform);
        Assert.Contains("device.OptInTransient(transientTarget.ColorTextureIds[0], transientSlot);", platform);
        int helper = platform.IndexOf("private FrameBufferRef CreateOptimumColorTarget(", StringComparison.Ordinal);
        Assert.True(helper >= 0);
        int helperEnd = platform.IndexOf("return target;", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("device.CreateTexture2D(", platform.Substring(helper, helperEnd - helper));

        string allocator = Read("Optimum.Render.Vulkan/Graph/TransientAllocator.cs");
        Assert.Contains("PostChainSlots = { 2, 3, 4, 7, 8, 9, 10, 13, 14, 15, 18, 21 };", allocator);

        string device = VulkanDeviceSource.Read();
        Assert.Contains("poolClass: MemoryPoolClass.Transient);", device);
        Assert.Contains("_transients.OptIn(id, framebufferSlot);", device);
    }

    [Fact]
    public void AliasingIsEnvGatedAndDiscardsOnFirstUse()
    {
        string allocator = Read("Optimum.Render.Vulkan/Graph/TransientAllocator.cs");
        Assert.Contains("public const string AliasVariable = \"OPTIMUM_VULKAN_ALIAS\";", allocator);
        Assert.Contains("Environment.GetEnvironmentVariable(AliasVariable) == \"1\"", allocator);
        Assert.Contains("int[] slots = TransientPlacement.Place(_intervals);", allocator);
        Assert.Contains("if (_aliasing) _backing.Discard(image.TextureId);", allocator);

        string backing = Read("Optimum.Render.Vulkan/Graph/TextureTransientBacking.cs");
        Assert.Contains("poolClass: MemoryPoolClass.Transient", backing);

        string tracker = Read("Optimum.Render.Vulkan/Graph/ResourceStateTracker.cs");
        Assert.Contains("public void Discard()", tracker);

        string device = VulkanDeviceSource.Read();
        Assert.Contains("TransientAliasingOverride ?? Graph.TransientAllocator.AliasingFromEnvironment()", device);
        Assert.Contains("_transients.BeginFrame();", device);
    }

    [Fact]
    public void ReadSelfCopiesArePooledOnTheTimeline()
    {
        string device = VulkanDeviceSource.Read();
        Assert.DoesNotContain("_feedbackCopies", device);
        Assert.Contains("int copyId = _readSelfCopies.Acquire(new Graph.FeedbackCopyDesc(", device);
        Assert.Contains("_readSelfCopies.EndFrame();", device);
        Assert.Contains("_readSelfCopies.Collect();", device);
        Assert.Contains("new Graph.FeedbackCopyPool(_frames.Timeline, CreateReadSelfCopy, ReleaseTexture)", device);

        string pool = Read("Optimum.Render.Vulkan/Graph/FeedbackCopyPool.cs");
        Assert.Contains("if (copy.RetiredAt <= completed)", pool);
    }

    [Fact]
    public void StatsReportTransientAliasedAndHeapPeak()
    {
        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("\"stats.transients transient_mib={0:F1} aliased_mib={1:F1} heap_peak_mib={2:F1} leases={3} \"", stats);
        Assert.Contains("HeapPeakBytes: memory?.TakeTransientHeapPeak() ?? 0,", stats);

        string doc = Read("docs/taa-acceptance.md");
        Assert.Contains("stats.transients", doc);
    }

    private static string Read(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "VintageStory.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, relativePath));
    }
}
}
