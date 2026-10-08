using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Retained chunk pool scope and deferred first-draw pass opening.
internal sealed partial class GameGraphicsAdapter
{
    private bool chunkScopeActive, chunkPassOpen;
    private string chunkScopeName = "";
    private int chunkTarget;
    private uint chunkSlots;
    private bool chunkBlend, chunkDepthTest, chunkDepthWrite;
    private CullModeFlags chunkCull;
    private readonly Dictionary<string, NativeMeshPass> chunkPasses = new(StringComparer.Ordinal);
    private NativeTexture[] chunkTextures = [];
    private int[] chunkReads = [];

    /// <summary>Captures target/write-mask and fixed draw state for a named terrain pool; native pass opening is deferred to its first draw.</summary>
    /// <param name="name">Retained terrain pool/pass name.</param>
    internal void BeginChunkPool(string name)
    {
        RequireDevice();
        if (chunkScopeActive) throw new InvalidOperationException("Recursive terrain pool draw.");
        chunkScopeActive = true; chunkPassOpen = false; chunkScopeName = name;
        chunkTarget = CurrentTargetId; chunkSlots = Stated.DrawBuffers(chunkTarget);
        int motion = FrameState.MotionAttachment;
        if (currentFramebuffer != null && ReferenceEquals(currentFramebuffer, platform!.FrameBuffers[0]) && motion is >= 0 and < 32)
            // BeginMotionWrite owns the exact mask, including a liquid motion-only redraw.
            if (!FrameState.MotionWriteActive) chunkSlots = (1u << motion) - 1u;
        chunkBlend = Stated.BlendEnabled; chunkDepthTest = Stated.DepthTest;
        chunkDepthWrite = Stated.DepthWrite; chunkCull = Stated.CullMode;
        RequireDevice().GpuMark(name);
    }
    /// <summary>Closes an opened native terrain pass and clears its enclosing pool scope.</summary>
    internal void EndChunkPool()
    {
        if (!chunkScopeActive) return;
        chunkScopeActive = false;
        if (chunkPassOpen) { chunkPassOpen = false; RequireDevice().EndNativePass(); }
        chunkScopeName = "";
    }
    private bool TryDrawChunkPoolNative(VAO vao, int[] starts, int[] sizes, int groups)
    {
        var renderer = RequireDevice();
        if (!chunkScopeActive || vao.VaoId == 0 || vao.Disposed) return false;
        if (groups <= 0) return chunkPassOpen;
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (program == null || program.ProgramId <= 0) return RefuseOpenChunkDraw();
        int handle = MeshHandle(vao), layout = renderer.NativeMeshLayoutId(handle);
        RenderTargetFormats? formats = renderer.NativeTargetFormats(chunkTarget, chunkSlots);
        if (layout < 0 || formats == null) return RefuseOpenChunkDraw();
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
        var blend = Stated.BlendFor(chunkTarget, Math.Max(formats.ColorFormats.Length, 1)).ToArray();
        for (int index = 0; index < blend.Length; index++) blend[index].Enabled = chunkBlend;
        NativePipeline? pipeline = NativeMeshPipelineFor(pass, program, chunkTarget, chunkSlots, layout,
            new NativePipelineDescription
            {
                Blend = blend, DepthTest = chunkDepthTest, DepthWrite = chunkDepthWrite, DepthCompare = CompareOp.Less,
                Cull = chunkCull, Topology = PrimitiveTopology.TriangleList, SamplesBoundDepth = sampledDepth,
            });
        if (pipeline == null) return RefuseOpenChunkDraw();
        for (int index = 0; index < names.Length; index++)
            chunkTextures[index] = chunkTextures[index] with { Sampler = pipeline.Sampler(names[index]) };
        if (!chunkPassOpen)
        {
            Rect2D viewport = Stated.Viewport;
            if (!renderer.BeginNativePass(new NativePassDescription
            {
                Name = chunkScopeName + "/" + chunkTarget, FramebufferId = chunkTarget, ColorSlots = chunkSlots,
                Reads = chunkReads.AsSpan(0, names.Length).ToArray(), Flags = PassFlags.AllowSplit,
                ViewportX = viewport.Offset.X, ViewportY = viewport.Offset.Y,
                ViewportWidth = (int)viewport.Extent.Width, ViewportHeight = (int)viewport.Extent.Height,
            })) return false;
            chunkPassOpen = true;
        }
        if (renderer.DrawNativeMeshMulti(pipeline, handle, starts, sizes, groups, chunkTextures.AsSpan(0, names.Length)))
            RuntimeStats.drawCallsCount++;
        else RejectSceneDraw("chunk native multi-draw rejected");
        // Once opened, this scope owns the pool even if a later pipeline/draw is skipped.
        return true;
    }
    private bool RefuseOpenChunkDraw()
    {
        if (chunkPassOpen) RejectSceneDraw("chunk scope refused a subsequent draw");
        return chunkPassOpen;
    }
}
