using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Vintagestory.API.MathTools;
using System.Numerics;

namespace VulkanStory.Game;

// Retained chunk pool scope and deferred first-draw pass opening.
internal sealed partial class GameGraphicsAdapter
{
    private bool chunkScopeActive, chunkPassOpen;
    private SceneDrawCategory previousChunkDrawCategory;
    private string chunkScopeName = "";
    private int chunkTarget;
    private uint chunkSlots;
    private bool chunkBlend, chunkDepthTest, chunkDepthWrite;
    private CullModeFlags chunkCull;
    private readonly Dictionary<string, NativeMeshPass> chunkPasses = new(StringComparer.Ordinal);
    private NativeTexture[] chunkTextures = [];
    private int[] chunkReads = [];
    private AttachmentBlend[]? chunkBlendSource;
    private AttachmentBlend[] chunkBlendCopy = [];
    private bool chunkBlendCopyEnabled;
    private bool residentReplay, residentForeignPolicy;
    private ulong residentPolicyFrame = ulong.MaxValue, residentVisibilityFrame = ulong.MaxValue;
    private int residentVisibilityEye = int.MinValue;
    private ulong[] residentVisibility = [];
    private bool? residentTerrainSetting;
    private sealed record ResidentMainSnapshot(TerrainView View, ulong Frame, int Eye, int VisibilityBuffer);
    private ResidentMainSnapshot? residentMainSnapshot;

    /// <summary>Captures target/write-mask and fixed draw state for a named terrain pool; native pass opening is deferred to its first draw.</summary>
    /// <param name="name">Retained terrain pool/pass name.</param>
    /// <param name="mode">Stock visibility policy for this pool.</param>
    internal void BeginChunkPool(string name, EnumFrustumCullMode mode = EnumFrustumCullMode.NoCull)
    {
        RequireDevice();
        if (chunkScopeActive) throw new InvalidOperationException("Recursive terrain pool draw.");
        chunkScopeActive = true; chunkPassOpen = false; chunkScopeName = name;
        residentReplay = false;
        chunkTarget = CurrentTargetId; chunkSlots = Stated.DrawBuffers(chunkTarget);
        Performance.GpuTerrainDraws.BeginScope(mode == EnumFrustumCullMode.CullNormal &&
            RequireDevice().CanPrepareTerrainDraws && currentFramebuffer != null &&
            ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[0]));
        int motion = FrameState.MotionAttachment;
        if (currentFramebuffer != null && ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[0]) && motion is >= 0 and < 32)
            // BeginMotionWrite owns the exact mask, including a liquid motion-only redraw.
            if (!FrameState.MotionWriteActive) chunkSlots = (1u << motion) - 1u;
        chunkBlend = Stated.BlendEnabled; chunkDepthTest = Stated.DepthTest;
        chunkDepthWrite = Stated.DepthWrite; chunkCull = Stated.CullMode;
        RequireDevice().GpuMark(name);
        previousChunkDrawCategory = RequireDevice().SceneDrawCategory;
        RequireDevice().SceneDrawCategory = SceneDrawCategory.Terrain;
    }
    /// <summary>Closes an opened native terrain pass and clears its enclosing pool scope.</summary>
    internal void EndChunkPool()
    {
        if (!chunkScopeActive) return;
        RequireDevice().SceneDrawCategory = previousChunkDrawCategory;
        chunkScopeActive = false;
        residentReplay = false;
        Performance.GpuTerrainDraws.EndScope();
        if (chunkPassOpen) { chunkPassOpen = false; RequireDevice().EndNativePass(); }
        chunkScopeName = "";
    }
    private bool TryDrawChunkPoolNative(VAO vao, int[] starts, int[] sizes, int groups)
    {
        var renderer = RequireDevice();
        if (!chunkScopeActive || vao.VaoId == 0 || vao.Disposed) return false;
        int handle = MeshHandle(vao);
        if (!TryOpenChunkPipeline(handle, out var pipeline, out int textureCount)) return RefuseOpenChunkDraw();
        var camera = Performance.TerrainOcclusionCull.CameraNow();
        var bounds = camera.HasValue ? Performance.GpuTerrainDraws.BoundsFor(starts, sizes, groups) : default;
        if (renderer.DrawNativeMeshMulti(pipeline!, handle, starts, sizes, groups, chunkTextures.AsSpan(0, textureCount),
                bounds, bounds.IsEmpty ? default : Performance.GpuTerrainDraws.Planes,
                camera?.X ?? 0, camera?.Y ?? 0, camera?.Z ?? 0))
            RuntimeStats.drawCallsCount++;
        else RejectSceneDraw("chunk native multi-draw rejected");
        return true;
    }
    private bool TryOpenChunkPipeline(int handle, out NativePipeline? pipeline, out int textureCount)
    {
        var renderer = RequireDevice();
        pipeline = null; textureCount = 0;
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (program == null || program.ProgramId <= 0) return false;
        int layout = renderer.NativeMeshLayoutId(handle);
        RenderTargetFormats? formats = renderer.NativeTargetFormats(chunkTarget, chunkSlots);
        if (layout < 0 || formats == null) return false;
        if (!worldSamplerNames.TryGetValue(program.ProgramId, out var names))
            worldSamplerNames.Add(program.ProgramId, names = renderer.SamplerNamesOf(program.ProgramId));
        if (chunkTextures.Length < names.Length) { chunkTextures = new NativeTexture[names.Length]; chunkReads = new int[names.Length]; }
        bool sampledDepth = false;
        int depth = renderer.NativeFramebufferDepthTexture(chunkTarget);
        for (int index = 0; index < names.Length; index++)
        {
            int texture = programTextures.GetValueOrDefault((program.ProgramId, names[index]));
            SamplerState? sampling = null;
            if (program.customSamplers.TryGetValue(names[index], out int sampler) && renderer.TryNativeSamplerState(sampler, out var state)) sampling = state;
            sampledDepth |= texture > 0 && texture == depth && !chunkDepthWrite;
            chunkTextures[index] = new NativeTexture(NativeSamplerSlot.None, texture, sampling); chunkReads[index] = texture;
        }
        if (!chunkPasses.TryGetValue(program.PassName, out var pass))
            chunkPasses.Add(program.PassName, pass = new NativeMeshPass(program.PassName, [], []));
        pipeline = NativeMeshPipelineFor(pass, program, formats, layout,
            new NativePipelineDescription
            {
                Blend = ChunkBlend(Math.Max(formats.ColorFormats.Length, 1)), DepthTest = chunkDepthTest, DepthWrite = chunkDepthWrite, DepthCompare = CompareOp.Less,
                Cull = chunkCull, Topology = PrimitiveTopology.TriangleList, SamplesBoundDepth = sampledDepth,
            });
        if (pipeline == null) return false;
        for (int index = 0; index < names.Length; index++)
            chunkTextures[index] = chunkTextures[index] with { Sampler = pipeline.Sampler(names[index]) };
        if (!chunkPassOpen)
        {
            if (!renderer.BeginNativePass(StatedViewportPass(chunkScopeName + "/" + chunkTarget, chunkTarget, chunkSlots,
                    chunkReads.AsSpan(0, names.Length).ToArray(), PassFlags.AllowSplit))) return false;
            chunkPassOpen = true;
        }
        textureCount = names.Length;
        return true;
    }
    internal bool DrawResidentTerrain(MeshDataPoolManager pool, Vec3d position, string uniform, EnumFrustumCullMode mode, int site)
    {
        var renderer = RequireDevice();
        residentReplay = false;
        renderer.ResidentTerrainEnabled = AoSettings.Settings.ResidentTerrain;
        if (residentTerrainSetting != renderer.ResidentTerrainEnabled)
        {
            ResetResidentTerrain();
            ResidentTerrain.Republish(this);
            residentTerrainSetting = renderer.ResidentTerrainEnabled;
        }
        if (!renderer.ResidentTerrainEnabled || !ResidentTerrain.TryManager(pool, out var manager) ||
            manager.ResidentMesh == 0 || aoTemporal?.CurrentClient is not { } client) return false;
        if (residentPolicyFrame != renderer.LatencyFrameId)
        {
            residentPolicyFrame = renderer.LatencyFrameId;
            residentForeignPolicy = ResidentTerrain.ForeignPolicy();
        }
        if (residentForeignPolicy) return false;
        if (residentVisibilityFrame != renderer.LatencyFrameId || residentVisibilityEye != renderer.TerrainEyeIndex)
        {
            int words = ResidentTerrain.VisibilityWords;
            if (residentVisibility.Length < words) Array.Resize(ref residentVisibility, words);
            ResidentTerrain.Visibility(residentVisibility.AsSpan(0, words));
            renderer.SetTerrainChunkVisibility(residentVisibility.AsSpan(0, words));
            residentVisibilityFrame = renderer.LatencyFrameId; residentVisibilityEye = renderer.TerrainEyeIndex;
        }
        if (!TryOpenChunkPipeline(manager.ResidentMesh, out var pipeline, out int textureCount)) return false;
        if (pipeline?.Program.IsNative != true) return false;
        var projection = client.CurrentProjectionMatrix;
        var modelView = client.CurrentModelViewMatrix;
        float[] combined = Mat4f.Mul(new float[16], projection, modelView);
        static Matrix4x4 Matrix(float[] value) => new(value[0], value[1], value[2], value[3],
            value[4], value[5], value[6], value[7], value[8], value[9], value[10], value[11],
            value[12], value[13], value[14], value[15]);
        TerrainView view = ResidentTerrain.View(ResidentTerrain.Frustum(pool), position, renderer.TerrainEyeIndex,
            Matrix(combined), mode, uniform);
        if (!renderer.DrawTerrainPass(pipeline, chunkTextures.AsSpan(0, textureCount), site, manager.Pass, manager.Atlas, view))
            return false;
        residentReplay = true;
        if (mode == EnumFrustumCullMode.CullNormal)
        {
            manager.MainFrame = renderer.LatencyFrameId; manager.MainEye = renderer.TerrainEyeIndex;
            Volatile.Write(ref residentMainSnapshot, new ResidentMainSnapshot(view, renderer.LatencyFrameId,
                renderer.TerrainEyeIndex, ModelDataPoolLocation.VisibleBufIndex));
        }
        // Fully native managers do no CPU pool/part walk. Mixed managers replay only their compat pools.
        return manager.CompatPools == 0;
    }
    internal bool SkipResidentTerrainPool(int mesh) => residentReplay && RequireDevice().IsTerrainPoolResident(mesh);
    internal void RefreshResidentTerrainFrustum(ModelDataPoolLocation location, ResidentTerrain.Part part)
    {
        // Consumers may run off-thread; only immutable copies of the main-camera culler cross that boundary.
        var snapshot = Volatile.Read(ref residentMainSnapshot);
        if (device?.ResidentTerrainEnabled != true || snapshot == null ||
            part.Pool.Manager.MainFrame != snapshot.Frame || part.Pool.Manager.MainEye != snapshot.Eye) return;
        lock (part)
        {
            if (part.FrustumFrame == snapshot.Frame && part.FrustumEye == snapshot.Eye) return;
            part.FrustumFrame = snapshot.Frame; part.FrustumEye = snapshot.Eye;
            // Stock IsVisible only writes FrustumVisible after both of these admission tests.
            if (location.Hide || !location.CullVisible[snapshot.VisibilityBuffer]) return;
            Sphere sphere = location.FrustumCullSphere;
            TerrainView view = snapshot.View;
            static bool Outside(TerrainPlane plane, Sphere box)
            {
                double x = (double)box.x + (double)((plane.A > 0 ? 1f : -1f) * box.radius / 1.7320508f);
                double y = (double)box.y + (double)((plane.B > 0 ? 1f : -1f) * box.radiusY / 1.7320508f);
                double z = (double)box.z + (double)((plane.C > 0 ? 1f : -1f) * box.radiusZ / 1.7320508f);
                return x * plane.A + y * plane.B + z * plane.C + plane.D < 0;
            }
            bool visible = !Outside(view.Plane0, sphere) && !Outside(view.Plane1, sphere) &&
                !Outside(view.Plane2, sphere) && !Outside(view.Plane3, sphere) && !Outside(view.Plane4, sphere);
            if (visible)
            {
                double dx = view.PlayerBlockX - (double)sphere.x, dz = view.PlayerBlockZ - (double)sphere.z;
                double squared = (float)(dx * dx + dz * dz);
                visible = location.LodLevel switch
                {
                    0 => view.Lod0BiasSq > 0 && squared < (double)((float)view.Lod0BiasSq + 1024f),
                    1 => squared < view.ViewDistanceSq,
                    2 => squared <= view.Lod2BiasSq,
                    3 => squared > view.Lod2BiasSq && squared < view.ViewDistanceSq,
                    _ => false,
                };
            }
            location.FrustumVisible = visible;
        }
    }
    /// <summary>
    /// The stated blend of the pool's target with every attachment's enable taken from the pool
    /// scope. The copy is reused while the stated snapshot and the scope's enable are the same;
    /// a pipeline description keeps the array it was given, so a change builds a new copy and
    /// never writes into one already handed out.
    /// </summary>
    private AttachmentBlend[] ChunkBlend(int count)
    {
        AttachmentBlend[] source = Stated.BlendFor(chunkTarget, count);
        if (ReferenceEquals(source, chunkBlendSource) && chunkBlendCopyEnabled == chunkBlend) return chunkBlendCopy;
        var copy = source.ToArray();
        for (int index = 0; index < copy.Length; index++) copy[index].Enabled = chunkBlend;
        chunkBlendSource = source; chunkBlendCopyEnabled = chunkBlend; chunkBlendCopy = copy;
        return copy;
    }
    private bool RefuseOpenChunkDraw()
    {
        if (chunkPassOpen) RejectSceneDraw("chunk scope refused a subsequent draw");
        return chunkPassOpen;
    }
}
