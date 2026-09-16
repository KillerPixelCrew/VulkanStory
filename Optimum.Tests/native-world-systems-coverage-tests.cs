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
