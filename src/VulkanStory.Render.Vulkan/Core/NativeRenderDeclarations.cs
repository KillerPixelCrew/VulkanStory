using System;
using System.Collections.Generic;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

/// <summary>Which block of a program's interface a native uniform lives in.</summary>
internal enum NativeUniformBlock : byte
{
    None = 0,
    /// <summary>The program record at set 2, snapshotted into the uniform ring per draw.</summary>
    Record = 1,
    /// <summary>A DRAW uniform in the program's push block.</summary>
    Push = 2,
    /// <summary>A member of the shared frame block at set 0.</summary>
    Frame = 3,
}

/// <summary>
/// A uniform's placement in one native pipeline's program, resolved once when the pipeline
/// is created. A native renderer holds the value and writes through it, so no draw looks a
/// uniform up by name.
/// </summary>
internal readonly record struct NativeUniform(NativeUniformBlock Block, int Offset, int Size)
{
    /// <summary>Whether this resolved native uniform/sampler refers to a present reflected declaration.</summary>
    public bool IsPresent => Block != NativeUniformBlock.None;
}

/// <summary>
/// A sampler's placement: the push-block offset its bindless slot index is written to (or
/// the set 0 binding, for a fixed frame texture) and the array kind it indexes. Resolved
/// once with the pipeline.
/// </summary>
internal readonly record struct NativeSamplerSlot(int Index, int PushOffset, int FrameBinding, TextureKind Kind)
{
    /// <summary>Sentinel for an absent native sampler declaration.</summary>
    public static NativeSamplerSlot None => new(-1, -1, -1, TextureKind.Texture2D);

    /// <summary>Whether this resolved native uniform/sampler refers to a present reflected declaration.</summary>
    public bool IsPresent => Index >= 0;
}

/// <summary>One sampled texture of a native draw: the slot, the texture handle and the sampler state to read it with (null: the texture's own).</summary>
internal readonly record struct NativeTexture(NativeSamplerSlot Sampler, int TextureId, SamplerState? Sampling = null);

/// <summary>
/// The fixed state a native pipeline is built for (docs/vulkan.md,
/// decision 4). It is <see cref="PipelineKey" />'s shape stated outright: per-attachment blend and colour write mask,
/// depth test/write/compare, cull, topology and the target's formats.
/// </summary>
internal sealed class NativePipelineDescription
{
    /// <summary>The linked program: a manifest program when native shaders are on, its rewritten twin otherwise.</summary>
    public int ProgramId;

    /// <summary>The pass name the program was linked under, checked against the device's; null skips the check.</summary>
    public string? PassName;

    /// <summary>The variant the program must have been linked for, checked against the device's; null skips the check.</summary>
    public string? VariantKey;

    /// <summary>Per colour attachment; <see cref="AttachmentBlend.WriteMask" /> is the colour write mask. Attachments past the array are not written.</summary>
    public AttachmentBlend[] Blend = Array.Empty<AttachmentBlend>();

    public bool DepthTest;
    public bool DepthWrite;
    public CompareOp DepthCompare = CompareOp.Less;
    /// <summary>Sentinel for an absent native sampler declaration.</summary>
    public CullModeFlags Cull = CullModeFlags.None;

    /// <summary>
    /// The winding a front face has. The game never calls glFrontFace, so every vanilla system
    /// states <see cref="RenderLimits.FrontFace" />; a native system that needs the other one
    /// says so here rather than through a tracked toggle.
    /// </summary>
    public FrontFace FrontFace = RenderLimits.FrontFace;

    public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;

    /// <summary>Fill for every vanilla system; Line is the wireframe debug render's.</summary>
    public PolygonMode PolygonMode = PolygonMode.Fill;

    /// <summary>The width a line-topology draw rasterizes with (autocamera's debug path sets 2).</summary>
    public float LineWidth = 1.0f;

    /// <summary>
    /// The vertex layout the pipeline's draws feed it with: <see cref="MeshManager.EmptyLayoutId" />
    /// for a pass that generates its vertices (the fullscreen triangle), otherwise the layout id of
    /// the mesh the system draws (<see cref="VulkanDevice.NativeMeshLayoutId" />). It is part of the
    /// pipeline key, so a mesh pipeline can never be handed a fullscreen one.
    /// </summary>
    public int VertexLayoutId = MeshManager.EmptyLayoutId;

    /// <summary>
    /// The draws sample the depth attachment of the target they draw into, with depth writes off -
    /// what the liquid pass does to fade water at its edges. The scope then holds depth read-only
    /// for the draw. Only legal with <see cref="DepthWrite" /> false; a fullscreen pass leaves it
    /// false and sampling its own attachment stays an error.
    /// </summary>
    public bool SamplesBoundDepth;

    /// <summary>The attachment formats of the target the pipeline renders into.</summary>
    public RenderTargetFormats Targets = null!;
}

/// <summary>
/// A pipeline a native render system owns: the program, its fixed state, the pipeline-cache
/// key built from them, and the placement tables the draws write through.
/// </summary>
internal sealed class NativePipeline
{
    private readonly Dictionary<string, NativeUniform> _uniforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeSamplerSlot> _samplers = new(StringComparer.Ordinal);
    private readonly NativeSamplerSlot[] _samplerSlots;

    internal NativePipeline(ShaderProgramResources program, NativePipelineDescription description,
        PipelineKey key, GraphicsPipelineCache.PipelineRequest request, int dynamicBlendId)
    {
        Program = program;
        Description = description;
        Key = key;
        Request = request;
        DynamicBlendId = dynamicBlendId;

        ProgramInterfaceLayout layout = program.Interface;
        foreach (UniformMember member in layout.Members)
        {
            _uniforms[member.Name] = new NativeUniform(NativeUniformBlock.Record, member.Offset, member.Size);
        }
        foreach (KeyValuePair<string, UniformMember> push in layout.PushMembersByName)
        {
            _uniforms[push.Key] = new NativeUniform(NativeUniformBlock.Push, push.Value.Offset, push.Value.Size);
        }
        foreach (string name in layout.FrameMemberDeclaredLengths.Keys)
        {
            if (FrameGlobals.TryGetMember(name, out UniformMember frame))
            {
                _uniforms[name] = new NativeUniform(NativeUniformBlock.Frame, frame.Offset, frame.Size);
            }
        }
        SamplerNames = program.SamplerNames;
        _samplerSlots = new NativeSamplerSlot[layout.Samplers.Count];
        for (int i = 0; i < layout.Samplers.Count; i++)
        {
            SamplerBinding sampler = layout.Samplers[i];
            TextureKind kind = sampler.Kind;
            if (sampler.IsFrameTexture) BindlessKinds.TryFromGlslType(sampler.TypeName, out kind);
            NativeSamplerSlot slot = new(i, sampler.PushOffset, sampler.FrameBinding, kind);
            _samplers[sampler.Name] = slot;
            _samplerSlots[i] = slot;
        }
    }

    /// <summary>
    /// Every sampler the program declares, in binding order. A system whose draws are all
    /// native has to resolve all of them: the emulated resolve that would otherwise fill the
    /// push block's slots from the texture units never runs for such a program, so a sampler
    /// left out would read whatever slot index was last written there.
    /// </summary>
    internal string[] SamplerNames { get; }

    /// <summary>Borrowed linked program resources from which this native pipeline was resolved.</summary>
    internal ShaderProgramResources Program { get; }

    /// <summary>Renderer ID of the linked program supplying this native pipeline.</summary>
    public int ProgramId => Program.ProgramId;

    /// <summary>Native pipeline state declaration retained for resolution and drawing.</summary>
    public NativePipelineDescription Description { get; }

    /// <summary>Resolved graphics-pipeline cache key for this native declaration.</summary>
    internal PipelineKey Key { get; }

    /// <summary>Borrowed graphics pipeline request matching the resolved native state.</summary>
    internal GraphicsPipelineCache.PipelineRequest Request { get; }

    /// <summary>The interned blend set the dynamic-state cache compares on, with the mask tier's dynamic blend.</summary>
    internal int DynamicBlendId { get; }

    /// <summary>The placement of a uniform, resolved once here rather than per draw.</summary>
    public NativeUniform Uniform(string name) =>
        _uniforms.TryGetValue(name, out NativeUniform member) ? member : default;

    /// <summary>The placement of a sampler, resolved once here rather than per draw.</summary>
    public NativeSamplerSlot Sampler(string name) =>
        _samplers.TryGetValue(name, out NativeSamplerSlot sampler) ? sampler : NativeSamplerSlot.None;

    /// <summary>The declared sampler slot, already resolved when this pipeline was created.</summary>
    public NativeSamplerSlot SamplerAt(int index) =>
        (uint)index < (uint)_samplerSlots.Length ? _samplerSlots[index] : NativeSamplerSlot.None;
}

/// <summary>
/// A native pass: an explicit target, the colour slots it writes, the textures it samples
/// and the viewport its draws use. No draw-buffer mask and no bound-target guessing.
/// </summary>
internal sealed class NativePassDescription
{
    public string Name = "";

    /// <summary>A render target id, or <see cref="PassDeclaration.DefaultFramebuffer" /> for the default target.</summary>
    public int FramebufferId = PassDeclaration.DefaultFramebuffer;

    /// <summary>Bit i: colour slot i is an attachment of the pass.</summary>
    public uint ColorSlots = 1u;

    /// <summary>The textures the pass samples, made shader-readable at pass entry.</summary>
    public int[] Reads = Array.Empty<int>();

    public uint TransientSlots;
    /// <summary>Sentinel for an absent native sampler declaration.</summary>
    public PassFlags Flags = PassFlags.None;

    /// <summary>
    /// Bit i: colour slot i starts the pass cleared to <see cref="ClearValue" />. The pass states
    /// its own clear instead of a glClearBuffer against a draw-buffer mask, so it lands as the
    /// scope's load op.
    /// </summary>
    public uint ClearSlots;

    /// <summary>The value <see cref="ClearSlots" /> clears to.</summary>
    public float[] ClearValue = { 0f, 0f, 0f, 0f };

    public int ViewportX;
    public int ViewportY;

    /// <summary>
    /// The scissor the client stated for this draw, in the target's own (GL-oriented) pixels;
    /// null: the full target. A GUI draw inside a clipped dialog needs it.
    /// </summary>
    public Rect2D? Scissor;

    /// <summary>
    /// A pass of the generic stated route (Platform/StatedDraw.cs) rather than of a dedicated
    /// native system. It records the same way; only the test counters keep it apart.
    /// </summary>
    public bool Generic;

    /// <summary>Negative: the full target.</summary>
    public int ViewportWidth = -1;
    public int ViewportHeight = -1;
}

