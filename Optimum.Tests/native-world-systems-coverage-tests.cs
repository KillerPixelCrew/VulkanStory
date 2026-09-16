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
/// The sky dome was the first; the night sky box, the moon, the cube particle pool and the decal
/// pool followed. Later stages (chunks, entities, GUI) add their seams to the same lists here
/// rather than to a file named after the stage.
/// </summary>
public class NativeWorldSystemsCoverageTests
{
    /// <summary>The double quote the patcher's listings are spelled with, so assertions can name them.</summary>
    private const string Q = "\"";

    private const string SkyPlatformFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeSky.cs";
    private const string WorldPlatformFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeWorld.cs";
    private const string DeviceMeshFile = "Optimum.Render.Vulkan/VulkanDevice.NativeMesh.cs";
    private const string DeviceNativeFile = "Optimum.Render.Vulkan/VulkanDevice.Native.cs";

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

    // ------------------------------------- the night sky, the moon, the particles, the decals

    /// <summary>
    /// The four seams of the second wave exist on the platform abstraction, each with the
    /// neutral body of the draw it replaced - which is what makes "OFF is vanilla" true for
    /// OpenGL, because ClientPlatformWindows overrides none of them.
    /// </summary>
    [Fact]
    public void TheWorldSeamsHaveNeutralBodiesThatAreTheDrawsTheyReplaced()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains("public virtual void RenderNightSkyBox(MeshRef nightSkyBox, int cubeTextureId)", platform);
        Assert.Contains("RenderMesh(nightSkyBox);", platform);

        Assert.Contains(
            "public virtual void RenderCelestialQuad(MeshRef quad, int bodyTextureId, int skyTextureId, int glowTextureId)",
            platform);
        Assert.Contains("RenderMesh(quad);", platform);

        Assert.Contains("public virtual void RenderParticles(MeshRef model, int quantity, int particleTextureId)",
            platform);
        Assert.Contains("RenderMeshInstanced(model, quantity);", platform);

        Assert.Contains(
            "public virtual void RenderDecalPool(MeshRef decalMesh, int[] indicesStarts, int[] indicesSizes, int groupCount, int decalTextureId, int blockTextureId)",
            platform);
        Assert.Contains("RenderMesh(decalMesh, indicesStarts, indicesSizes, groupCount);", platform);

        // The OpenGL platform leaves every one of them alone: nothing about the GL path changes.
        string windows = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        foreach (string seam in new[]
                 {
                     "RenderNightSkyBox", "RenderCelestialQuad", "RenderParticles", "RenderDecalPool",
                 })
        {
            Assert.DoesNotContain(seam, windows);
        }
    }

    /// <summary>
    /// Each render system draws through its seam and hands it the values a native pass cannot
    /// read off the GL state: the textures it samples, and for the decals the cull results the
    /// pool produced.
    /// </summary>
    [Fact]
    public void TheWorldRenderersDrawThroughTheirSeams()
    {
        string nightSky = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderNightSky.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderNightSky.cs");
        Assert.Contains("game.Platform.RenderNightSkyBox(nightSkyBox, textureId);", nightSky);
        Assert.DoesNotContain("game.Platform.RenderMesh(nightSkyBox);", nightSky);

        string sunMoon = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSunMoon.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSunMoon.cs");
        Assert.Contains(
            "platform.RenderCelestialQuad(quadModel, moontextureIds[4], game.skyTextureId, game.skyGlowTextureId);",
            sunMoon);

        string particles = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderParticles.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderParticles.cs");
        Assert.Contains("game.Platform.RenderParticles(particlePool.Model, particlePool.QuantityAlive, 0);", particles);
        Assert.Contains("game.Platform.RenderParticles(particlePool2.Model, particlePool2.QuantityAlive, 0);", particles);
        Assert.DoesNotContain("game.Platform.RenderMeshInstanced(", particles);

        // The motion window still wraps the draw: the cube pool writes the motion attachment
        // through the one writer include, and the window is what puts that attachment in the
        // colour set on both routes.
        Assert.Contains("optimumPlatform.BeginMotionWrite()", particles);
        Assert.Contains("optimumPlatform.EndMotionWrite();", particles);

        string decals = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs");
        // The cull half of MeshDataPool.Draw, then the seam with its cull results: both routes
        // draw the same ranges and only the draw command differs.
        Assert.Contains("decalPool.FrustumCull(game.frustumCuller, EnumFrustumCullMode.CullInstant);", decals);
        Assert.Contains(
            "game.Platform.RenderDecalPool(decalPool.ModelRef, decalPool.indicesStartsByte, decalPool.indicesSizes, decalPool.indicesGroupsCount,",
            decals);
        Assert.DoesNotContain("decalPool.Draw(game.api,", decals);
        Assert.Contains("optimumPlatform.BeginMotionWrite()", decals);
    }

    /// <summary>Every new or changed lib member of this wave is listed for the Cecil transplant.</summary>
    [Fact]
    public void TheWorldSeamsAndTheirCallersAreListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string seam in new[]
                 {
                     "RenderNightSkyBox", "RenderCelestialQuad", "RenderParticles", "RenderDecalPool",
                 })
        {
            Assert.Contains(Q + seam + Q, patcher);
        }

        foreach (string caller in new[]
                 {
                     "SystemRenderNightSky" + Q + ", " + Q + "OnRenderFrame3D" + Q + ", 1",
                     "SystemRenderSunMoon" + Q + ", " + Q + "OnRenderFrame3D" + Q + ", 1",
                     "SystemRenderParticles" + Q + ", " + Q + "Render" + Q + ", 2",
                     "SystemRenderDecals" + Q + ", " + Q + "OnRenderFrame3D" + Q + ", 1",
                 })
        {
            Assert.Contains(caller, patcher);
        }
    }

    /// <summary>
    /// The Vulkan platform records all four systems as native passes, states their fixed state
    /// rather than reading the tracker's, and keeps every neutral body reachable behind one
    /// switch in the pattern of NativeSkyEnabled.
    /// </summary>
    [Fact]
    public void TheVulkanPlatformRecordsTheWorldSystemsNativelyAndKeepsTheOldRoutes()
    {
        string world = Read(WorldPlatformFile);

        Assert.Contains("internal bool NativeWorldEnabled { get; set; } = true;", world);
        foreach (string seam in new[]
                 {
                     "RenderNightSkyBox", "RenderCelestialQuad", "RenderParticles", "RenderDecalPool",
                 })
        {
            Assert.Contains("public override void " + seam + "(", world);
            Assert.Contains("base." + seam + "(", world);
        }

        // Each mesh-draw kind the device API grew for world systems is used by the system whose
        // shape needs it: a single mesh, an instanced pool, an indirect multi-draw.
        Assert.Contains("device.DrawNativeMesh(", world);
        Assert.Contains("device.DrawNativeMeshInstanced(", world);
        Assert.Contains("device.DrawNativeMeshMulti(", world);
        Assert.Contains("device.BeginNativePass(", world);
        Assert.Contains("device.EndNativePass();", world);

        // The pipelines are built for each mesh's own vertex layout rather than the fullscreen
        // one: NativeWorldPrepare resolves it and hands it to the shared NativeMeshPipelineFor,
        // whose VertexLayoutId wiring is pinned by TheVulkanPlatformRecordsTheSkyNativelyAndKeepsTheOldRoute.
        Assert.Contains("device.NativeMeshLayoutId(", world);
    }

    /// <summary>
    /// The colour slots and the per-attachment blend of a native world pass come from the
    /// platform's own motion-window state, not from the GL state tracker (decision 3), and the
    /// motion attachment replaces rather than blends inside the window - what
    /// ApplyOptimumMotionBlendState does for an emulated draw.
    /// </summary>
    [Fact]
    public void TheWorldPassesDeriveTheirSlotsAndBlendFromTheMotionWindowNotTheTracker()
    {
        string world = Read(WorldPlatformFile);

        Assert.Contains("private uint NativeWorldPassColorSlots(FrameBufferRef target)", world);
        Assert.Contains("OptimumMotionWriteActive", world);
        Assert.Contains("MotionAttachmentIndex", world);
        Assert.Contains("(1u << (motion + 1)) - 1u", world);
        Assert.Contains("(1u << motion) - 1u", world);

        string blend = Section(world, "private AttachmentBlend[] NativeWorldBlend(", "return blend;");
        Assert.Contains("BlendFactor.One", blend);
        Assert.Contains("BlendFactor.Zero", blend);

        // Nothing in a native world pass asks the tracker what state it is in.
        Assert.DoesNotContain("GlStateTracker.", world);
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
