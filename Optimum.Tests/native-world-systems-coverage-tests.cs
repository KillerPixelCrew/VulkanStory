using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The world systems on the native device API (docs/vulkan-native-render-systems.md, decision 5
/// stage 2 onwards). Each system that moves gets a seam in the library whose neutral body is the
/// OpenGL body's own draw, a patcher listing for that seam, and a Vulkan override that records a
/// native pass with the old route kept reachable behind a switch.
///
/// The sky dome is the first. Later stages (chunks, entities, particles and decals, GUI) add
/// their seams to the same lists here rather than to a file named after the stage.
/// </summary>
public class NativeWorldSystemsCoverageTests
{
    private const string SkyPlatformFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSky.cs";
    private const string ChunkPlatformFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeChunks.cs";
    private const string DeviceMeshFile = "Optimum.Render.Vulkan/VulkanDevice.NativeMesh.cs";
    private const string DeviceNativeFile = "Optimum.Render.Vulkan/VulkanDevice.Native.cs";

    private const string EntityPlatformFile =
        "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeEntities.cs";

    /// <summary>
    /// The seam exists on the platform abstraction, and its neutral body is exactly the
    /// RenderMesh call it replaced - which is what makes "OFF is vanilla" true for OpenGL,
    /// because ClientPlatformWindows does not override it at all.
    /// </summary>
    [Fact]
    public void TheSkyDomeHasASeamWhoseNeutralBodyIsTheDrawItReplaced()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains(
            "public virtual void RenderSkyDome(MeshRef skyDome, int skyTextureId, int glowTextureId, float[] modelViewMatrix)",
            platform);
        Assert.Contains("RenderMesh(skyDome);", platform);

        // The OpenGL platform leaves it alone: nothing about the GL path changes.
        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.DoesNotContain("RenderSkyDome", windows);
    }

    /// <summary>
    /// SystemRenderSkyColor draws through the seam and hands it the values a native pass cannot
    /// read off the GL state: the two textures and the model-view matrix.
    /// </summary>
    [Fact]
    public void TheSkyRendererDrawsThroughTheSeam()
    {
        string system = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs");

        Assert.Contains(
            "game.Platform.RenderSkyDome(skyIcosahedron, game.skyTextureId, game.skyGlowTextureId, game.CurrentModelViewMatrix);",
            system);
        Assert.DoesNotContain("game.Platform.RenderMesh(skyIcosahedron);", system);
    }

    /// <summary>Every new or changed lib member is listed for the Cecil transplant.</summary>
    [Fact]
    public void TheSeamAndItsCallerAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"RenderSkyDome\"", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderSkyColor\", \"OnRenderFrame3D\", 1", patcher);
    }

    /// <summary>
    /// The Vulkan platform records the sky as a native pass, states its own fixed state rather
    /// than reading the tracker's, and keeps the neutral body reachable behind a switch in the
    /// pattern of NativeBlitEnabled.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsTheSkyNativelyAndKeepsTheOldRoute()
    {
        string sky = Read(SkyPlatformFile);

        Assert.Contains("internal bool NativeSkyEnabled { get; set; } = true;", sky);
        Assert.Contains("public override void RenderSkyDome(", sky);
        Assert.Contains("base.RenderSkyDome(", sky);
        Assert.Contains("device.BeginNativePass(", sky);
        Assert.Contains("device.DrawNativeMesh(", sky);
        Assert.Contains("device.EndNativePass();", sky);

        // The state the pass states outright, and the per-draw write.
        Assert.Contains("DepthTest = false", sky);
        Assert.Contains("DepthWrite = false", sky);
        Assert.Contains("Cull = CullModeFlags.None", sky);
        Assert.Contains("device.WriteNative(pipeline, nativeSky.Uniforms[0]", sky);

        // The pipeline is built for the mesh's own vertex layout, not the fullscreen one.
        Assert.Contains("device.NativeMeshLayoutId(", sky);
        Assert.Contains("VertexLayoutId = layoutId", sky);
    }

    /// <summary>
    /// The device records mesh draws through the mesh manager it already has - indexed,
    /// non-indexed, instanced and multi-draw through the per-slot indirect ring - and never
    /// builds a second mesh path.
    /// </summary>
    [Fact]
    public void TheDeviceRecordsEveryMeshDrawKindThroughTheExistingMeshPath()
    {
        string mesh = Read(DeviceMeshFile);

        foreach (string entry in new[]
                 {
                     "internal bool DrawNativeMesh(",
                     "internal bool DrawNativeMeshInstanced(",
                     "internal bool DrawNativeMeshArrays(",
                     "internal bool DrawNativeMeshMulti(",
                 })
        {
            Assert.Contains(entry, mesh);
        }

        // The existing machinery, reused: the mesh manager binds and draws, and the multi-draw
        // allocates from the same indirect ring the emulated DrawMeshMulti allocates from.
        Assert.Contains("_meshes.Bind(commandBuffer, mesh!);", mesh);
        Assert.Contains("_meshes.DrawMulti(commandBuffer, meshId,", mesh);
        Assert.Contains("AllocateIndirect(groupCount, out ulong indirectOffset)", mesh);

        // The real mesh id reaches BindProgramSets, which is what makes a chunk's storage-buffer
        // vertex fetch and an entity's animation block resolve per draw.
        Assert.Contains("BeginNativeDraw(pipeline, textures, meshId,", mesh);

        // Counted apart from fullscreen draws.
        foreach (string counter in new[]
                 {
                     "NoteNativeFullscreenDraw", "NoteNativeMeshDraw",
                     "NoteNativeInstancedDraw", "NoteNativeIndirectDraw",
                 })
        {
            Assert.Contains(counter, mesh);
        }

        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("native_fullscreen_draws=", stats);
        Assert.Contains("native_mesh_draws=", stats);
        Assert.Contains("native_instanced_draws=", stats);
        Assert.Contains("native_indirect_draws=", stats);
    }

    /// <summary>
    /// The pipeline description carries what a mesh draw needs and a fullscreen draw did not,
    /// and every one of those dimensions is in the key, so a mesh pipeline can never be handed
    /// out for a fullscreen request or the other way round.
    /// </summary>
    [Fact]
    public void ThePipelineDescriptionAndKeyCarryTheMeshDrawState()
    {
        string native = Read(DeviceNativeFile);

        foreach (string field in new[]
                 {
                     "public FrontFace FrontFace = GlStateTracker.FrontFace;",
                     "public PolygonMode PolygonMode = PolygonMode.Fill;",
                     "public float LineWidth = 1.0f;",
                     "public int VertexLayoutId = MeshManager.EmptyLayoutId;",
                     "public bool SamplesBoundDepth;",
                 })
        {
            Assert.Contains(field, native);
        }

        string key = Section(native, "private readonly record struct NativePipelineCacheKey(", ");");
        foreach (string dimension in new[]
                 {
                     "VertexLayoutId", "PolygonMode", "FrontFace", "LineWidth", "SamplesBoundDepth",
                 })
        {
            Assert.Contains(dimension, key);
        }

        // The pipeline-cache key takes the layout and polygon mode from the description too,
        // rather than the fullscreen constants stage 1 baked in.
        Assert.Contains("VertexLayoutId: description.VertexLayoutId,", native);
        Assert.Contains("PolygonMode: description.PolygonMode,", native);
        Assert.Contains("_meshes.LayoutOf(description.VertexLayoutId)", native);
    }

    /// <summary>
    /// The chunk groups have a seam of their own, and its neutral bodies do nothing at all -
    /// which is what keeps the OpenGL path drawing exactly the bodies it drew before, with its
    /// GlToggleBlend / depth / cull calls still in place.
    /// </summary>
    [Fact]
    public void TheChunkGroupsHaveAScopeSeamWhoseNeutralBodyDoesNothing()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains(
            "public virtual bool BeginChunkPass(string chunkPass, bool blend, bool depthTest, bool depthWrite, bool cullFace)",
            platform);
        Assert.Contains("public virtual void EndChunkPass()", platform);

        // The OpenGL platform leaves both alone: nothing about the GL path changes.
        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.DoesNotContain("BeginChunkPass", windows);
        Assert.DoesNotContain("EndChunkPass", windows);
    }

    /// <summary>
    /// Every ChunkRenderer draw group brackets its pools with the seam and states the fixed
    /// state that group runs under - and still makes the GL state calls the OpenGL path needs,
    /// because those are what the GL body draws with.
    /// </summary>
    [Fact]
    public void EveryChunkDrawGroupDrawsInsideTheScope()
    {
        string renderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        foreach (string group in new[]
                 {
                     "chunk-shadow-opaque", "chunk-shadow-topsoil", "chunk-shadow-vegetation",
                     "chunk-shadow-blendnocull", "chunk-opaque", "chunk-topsoil", "chunk-vegetation",
                     "chunk-blendnocull", "chunk-decorative", "chunk-oit-liquid", "chunk-oit-transparent",
                     "chunk-liquid-motion", "chunk-overlay",
                 })
        {
            Assert.Contains("platform.BeginChunkPass(\"" + group + "\"", renderer);
        }

        // One close per open, and each in a finally, so a throwing pool draw cannot leave a
        // pass open for the rest of the frame.
        int opens = Count(renderer, "platform.BeginChunkPass(");
        int closes = Count(renderer, "platform.EndChunkPass();");
        Assert.Equal(13, opens);
        Assert.Equal(opens, closes);

        // "OFF is vanilla": the GL state the OpenGL body draws under is still set.
        Assert.Contains("platform.GlToggleBlend(on: false);", renderer);
        Assert.Contains("platform.GlEnableCullFace();", renderer);
        Assert.Contains("platform.GlDepthMask(flag: true);", renderer);
    }

    /// <summary>Every new or changed lib member of the chunk port is listed for the Cecil transplant.</summary>
    [Fact]
    public void TheChunkSeamAndItsCallersAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"BeginChunkPass\"", patcher);
        Assert.Contains("\"EndChunkPass\"", patcher);
        foreach (string method in new[] { "RenderShadow", "RenderOpaque", "RenderOIT", "RenderAfterOIT" })
        {
            Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"" + method + "\", 1", patcher);
        }
        // RenderLiquidMotion is an injected member rather than a transplanted vanilla one.
        Assert.Contains("\"RenderLiquidMotion\"", patcher);
    }

    /// <summary>
    /// The Vulkan platform records the chunk groups as native passes with indirect multi-draws,
    /// states its own fixed state, expresses the motion window as a colour-write mask, and keeps
    /// the old route reachable behind a switch.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsTheChunkGroupsNativelyAndKeepsTheOldRoute()
    {
        string chunks = Read(ChunkPlatformFile);

        Assert.Contains("internal bool NativeChunksEnabled { get; set; } = true;", chunks);
        Assert.Contains("public override bool BeginChunkPass(", chunks);
        Assert.Contains("public override void EndChunkPass()", chunks);
        Assert.Contains("device.BeginNativePass(", chunks);
        Assert.Contains("device.EndNativePass();", chunks);

        // The multi-draw stays a multi-draw, over the mesh's own vertex layout.
        Assert.Contains("device.DrawNativeMeshMulti(", chunks);
        Assert.Contains("VertexLayoutId = layoutId", chunks);
        Assert.Contains("device.NativeMeshLayoutId(", chunks);

        // The motion window is a write mask, never a draw-buffer toggle.
        Assert.Contains("if (chunkScopeMotionOnly && slot != motion) entry.WriteMask = 0;", chunks);
        Assert.DoesNotContain("SetDrawBuffers", chunks);

        // The state is stated, not read back off the tracker.
        Assert.Contains("DepthTest = chunkScopeDepthTest", chunks);
        Assert.Contains("DepthWrite = chunkScopeDepthWrite", chunks);
        Assert.Contains("Cull = chunkScopeCull ? CullModeFlags.BackBit : CullModeFlags.None", chunks);
        Assert.Contains("SamplesBoundDepth = samplesBoundDepth", chunks);

        // The route in: the pool's multi-draw seam takes the native path only inside a scope.
        string meshes = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Meshes.cs");
        Assert.Contains("if (TryDrawChunkPoolNative(vAO, indices, indicesSizes, groupCount)) return;", meshes);
        Assert.Contains("device.DrawMeshMulti(vAO.VaoId, indices, indicesSizes, groupCount, useSSBOs);", meshes);
    }

    /// <summary>
    /// The values a native chunk pass cannot read off GL state are recorded where the client
    /// states them: the texture behind each sampler, and the Transparent target's blend contract.
    /// </summary>
    [Fact]
    public void TheClientStateANativeChunkPassNeedsIsRecordedAtItsOwnSeam()
    {
        string shaders = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Shaders.cs");
        Assert.Contains("NoteNativeProgramTexture(program.ProgramId, samplerName, textureId);", shaders);
        // A relinked program's cached interface and pipelines go with it.
        Assert.Contains("ForgetNativeChunkProgram(program.ProgramId);", shaders);

        string leaf = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Leaf.cs");
        Assert.Contains("NoteNativeTransparentBlend(0, 32774, 774, 0, 774, 0);", leaf);

        string buffers = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains("NoteNativeTransparentBlend(2, 32774, 770, 771, 770, 771);", buffers);
    }

    // ------------------------------------------------------------- entities (stage 2)

    /// <summary>
    /// The entity draw seam exists on the platform abstraction, its neutral body is exactly the
    /// RenderMesh call it replaced, and ClientPlatformWindows does not override it - which is what
    /// makes "OFF is vanilla" true for OpenGL.
    /// </summary>
    [Fact]
    public void TheEntityDrawHasASeamWhoseNeutralBodyIsTheDrawItReplaced()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains(
            "public virtual void RenderEntityMesh(MeshRef mesh, string samplerName, int textureId)",
            platform);
        Assert.Contains("RenderMesh(mesh);", platform);

        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.DoesNotContain("RenderEntityMesh", windows);
    }

    /// <summary>
    /// The entity renderers reach the seam where they already were: RenderMultiTextureMesh draws
    /// each sub-mesh through it and hands it the sampler name and texture id it just bound, which
    /// is what a native pass needs to resolve the draw's texture from a handle.
    /// </summary>
    [Fact]
    public void TheMultiTextureDrawGoesThroughTheSeam()
    {
        string api = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client/RenderAPIBase.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client/RenderAPIBase.cs");

        Assert.Contains("plat.RenderEntityMesh(vao, textureSampleName, mmr.textureids[i]);", api);
        Assert.DoesNotContain("plat.RenderMesh(vao);", api);
    }

    /// <summary>The seam and its caller are listed for the Cecil transplant.</summary>
    [Fact]
    public void TheEntitySeamAndItsCallerAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"RenderEntityMesh\"", patcher);
        Assert.Contains("\"Vintagestory.Client.RenderAPIBase\", \"RenderMultiTextureMesh\", 3", patcher);
    }

    /// <summary>
    /// The Vulkan platform records the entity draws natively for the two programs it owns, states
    /// its own fixed state rather than reading the tracker's, treats the motion window as a colour
    /// write mask, and keeps the neutral body reachable behind a switch.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsEntitiesNativelyAndKeepsTheOldRoute()
    {
        string entities = Read(EntityPlatformFile);

        Assert.Contains("internal bool NativeEntitiesEnabled { get; set; } = true;", entities);
        Assert.Contains("public override void RenderEntityMesh(", entities);
        Assert.Contains("base.RenderEntityMesh(", entities);
        Assert.Contains("device.BeginNativePass(", entities);
        Assert.Contains("device.DrawNativeMesh(", entities);

        // The two programs it owns, and nothing else.
        Assert.Contains("private const string EntityAnimatedPass = \"entityanimated\";", entities);
        Assert.Contains("private const string EntityShadowPass = \"shadowmapentityanimated\";", entities);

        // The fixed state stated outright, from the values SystemRenderEntities sets.
        Assert.Contains("DepthTest = true", entities);
        Assert.Contains("DepthWrite = true", entities);
        Assert.Contains("DepthCompare = CompareOp.Less", entities);
        Assert.Contains("Cull = CullModeFlags.None", entities);
        Assert.Contains("VertexLayoutId = layoutId", entities);

        // The motion window is a write mask on the pipeline, never a draw-buffer toggle.
        Assert.Contains("OptimumMotionWriteActive", entities);
        Assert.Contains("attachment.WriteMask = 0;", entities);
        Assert.DoesNotContain("SetDrawBuffers", entities);
    }

    /// <summary>
    /// The native draw is recorded inside the stage's own declared pass, so a loop of hundreds of
    /// entities does not end and restart the rendering scope once per entity, and the device has
    /// the close that makes that safe.
    /// </summary>
    [Fact]
    public void TheEntityDrawsShareTheStagesPassInsteadOfOnePassPerEntity()
    {
        string entities = Read(EntityPlatformFile);
        Assert.Contains("Name = BoundPassName(),", entities);
        Assert.Contains("ColorSlots = uint.MaxValue,", entities);
        Assert.Contains("device.EndNativePass(keepScope: true);", entities);

        string native = Read(DeviceNativeFile);
        Assert.Contains("internal void EndNativePass(bool keepScope)", native);
        Assert.Contains("if (!_frameActive || keepScope) return;", native);
    }

    /// <summary>
    /// A native draw resolves every sampler its program declares, from what the client declared
    /// for it by name - not from a texture unit, which decision 3 forbids and which the emulated
    /// resolve (never run for a program whose draws are all native) would otherwise have filled.
    /// </summary>
    [Fact]
    public void ANativeDrawResolvesEverySamplerTheProgramDeclares()
    {
        string entities = Read(EntityPlatformFile);
        Assert.Contains("string[] names = pipeline.SamplerNames;", entities);
        Assert.Contains("DeclaredProgramTexture(programId, names[i])", entities);

        string shaders = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Shaders.cs");
        Assert.Contains("NoteNativeProgramTexture(program.ProgramId, samplerName, textureId);", shaders);

        string native = Read(DeviceNativeFile);
        Assert.Contains("internal string[] SamplerNames { get; }", native);
    }

    private static int Count(string source, string needle)
    {
        int count = 0;
        int at = source.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    // ------------------------------------------------------------------------ helpers

    private static string Section(string source, string from, string to)
    {
        int start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + from);
        int end = source.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, "end not found after: " + from);
        return source.Substring(start, end - start);
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
