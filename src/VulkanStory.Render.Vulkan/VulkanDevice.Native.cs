using System;
using System.Collections.Generic;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

/// <summary>
/// The device API native render systems draw through (docs/vulkan.md,
/// section 2 decision 4 and section 3).
///
/// A native system asks for a pipeline by program and fixed state, declares a pass with its
/// target, its colour slots and the textures it reads, writes its uniforms by placement and
/// records draws. Nothing else reaches the GPU: the device has no GL state machine, no
/// texture-unit tables and no draw-buffer mask. Mod renderers and the vanilla systems without a
/// dedicated route draw through the platform's generic native draw (Platform/StatedDraw.cs).
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    private readonly Interner<BlendSignature> _nativeBlends = new();
    private readonly Interner<RenderTargetFormats> _nativeFormats = new();
    private readonly Dictionary<NativePipelineCacheKey, NativePipeline> _nativePipelines = new();

    /// <summary>The manifest variant each native program was linked for, keyed by program id.</summary>
    private readonly Dictionary<int, string> _programVariants = new();

    private NativePassDescription? _nativePass;
    private VulkanFramebuffer? _nativeTarget;
    private long _nativePasses;
    private long _nativeDraws;
    private long _nativeFullscreenDraws;
    private long _nativeMeshDraws;
    private long _nativeInstancedDraws;
    private long _nativeIndirectDraws;
    private ulong _nativeBoundPipelineSerial;
    private ulong _nativeBoundPipelineHandle;
    /// <summary>The recording and buffer the default-attribute binding was last bound in; it persists for the recording.</summary>
    private ulong _nativeBoundDefaultsSerial;
    private ulong _nativeBoundDefaultsHandle;
    /// <summary>Latest reason the retained native draw path refused a requested operation.</summary>
    internal string? NativeDrawRefusal { get; private set; }

    /// <summary>Stores the native-operation refusal reason and returns false to the caller.</summary>
    private bool RefuseNativeDraw(string reason)
    {
        NativeDrawRefusal = reason;
        AddDiagnostic(reason);
        return false;
    }

    /// <summary>
    /// The identity of a native pipeline: every field of its description that changes what a draw
    /// through it does. The vertex layout is in it, so a mesh pipeline never collides with the
    /// fullscreen one of the same program, formats and blend; so are the dynamic pieces (front
    /// face, line width) that are not in <see cref="PipelineKey" />, because the cached
    /// <see cref="NativePipeline" /> carries the description its draws emit.
    /// </summary>
    private readonly record struct NativePipelineCacheKey(
        int ProgramId, int FormatsId, int BlendId, bool DepthTest, bool DepthWrite,
        CompareOp DepthCompare, CullModeFlags Cull, PrimitiveTopology Topology,
        int VertexLayoutId, PolygonMode PolygonMode, FrontFace FrontFace, float LineWidth,
        bool SamplesBoundDepth);

    /// <summary>Native passes declared and native draws recorded (every kind). Tests only.</summary>
    internal long NativePassesForTests => _nativePasses;
    internal long NativeDrawsForTests => _nativeDraws;

    /// <summary>Native draws by kind: the fullscreen triangle, a mesh, an instanced mesh, a multi-draw. Tests only.</summary>
    internal long NativeFullscreenDrawsForTests => _nativeFullscreenDraws;
    internal long NativeMeshDrawsForTests => _nativeMeshDraws;
    internal long NativeInstancedDrawsForTests => _nativeInstancedDraws;
    internal long NativeIndirectDrawsForTests => _nativeIndirectDraws;

    /// <summary>Distinct native pipelines this device holds. Tests only.</summary>
    internal int NativePipelinesForTests => _nativePipelines.Count;

    /// <summary>Set 1's placeholders are written shader-read-only and never used any other way: put them there once.</summary>
    private void EnsureBindlessPlaceholdersReadable(CommandBuffer commandBuffer)
    {
        if (_bindlessPlaceholdersReadable || _bindless == null) return;

        for (int kind = 0; kind < BindlessKinds.Count; kind++)
        {
            VulkanTexture? placeholder = _textures.Get(_bindless.PlaceholderTextureId((TextureKind)kind));
            if (placeholder == null || placeholder.Layout == ImageLayout.ShaderReadOnlyOptimal) continue;
            _targets.EndRendering(commandBuffer);
            _textures.Require(_barriers, commandBuffer, placeholder, ResourceUsage.SampleFragment);
        }
        _bindlessPlaceholdersReadable = true;
    }

    // ------------------------------------------------------------------ pipelines

    /// <summary>
    /// The attachment formats of a target's colour slots, so a native system can state the
    /// formats its pipeline is built for. Null for a target that does not exist.
    /// </summary>
    internal RenderTargetFormats? NativeTargetFormats(int framebufferId, uint colorSlots)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(framebufferId));
        if (target == null) return null;

        return _targets.DeclaredFormats(target, colorSlots);
    }

    /// <summary>
    /// Unit assignments in linked sampler order. Writes from SetSamplerUnit and sampler
    /// uniform locations update this same array before the next stated draw.
    /// </summary>
    internal int[] NativeSamplerUnitsOf(int programId)
    {
        return _programs.TryGetValue(programId, out ShaderProgramResources? program)
            ? program.SamplerUnitsByIndex
            : Array.Empty<int>();
    }

    /// <summary>Whether the linked shader actually writes a colour output location.</summary>
    internal bool NativeProgramWritesOutput(int programId, int location) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) &&
        program.Interface.WrittenFragmentOutputs.Contains(location);
    /// <summary>The sampling state of a standalone sampler object (GenSampler), or null.</summary>
    internal SamplerState? NativeStandaloneSampler(int samplerId) =>
        _standaloneSamplers.TryGetValue(samplerId, out SamplerState state) ? state : null;

    /// <summary>The texture attached at a framebuffer's colour slot, 0 without one.</summary>
    internal int NativeFramebufferColorTexture(int framebufferId, int slot)
    {
        VulkanFramebuffer? target = _targets.Get(ResolveNativeFramebuffer(framebufferId));
        return target != null && (uint)slot < (uint)target.Color.Length ? target.Color[slot].TextureId : 0;
    }

    /// <summary>The depth texture attached to a framebuffer, 0 without one.</summary>
    internal int NativeFramebufferDepthTexture(int framebufferId) =>
        _targets.Get(ResolveNativeFramebuffer(framebufferId))?.DepthTextureId ?? 0;


    /// <summary>The manifest variant a program was linked for; "" for a program the rewriter linked.</summary>
    internal string NativeVariantOf(int programId) =>
        _programVariants.TryGetValue(programId, out string? key) ? key : "";

    /// <summary>
    /// The interned vertex layout of a mesh, which a native system states on the pipeline it
    /// draws that mesh through. -1 for a mesh that does not exist.
    /// </summary>
    internal int NativeMeshLayoutId(int meshId) => _meshes.LayoutIdOf(meshId);

    /// <summary>
    /// The state of a sampler object the client created (glGenSamplers), so a native system
    /// can read a program's own sampler override - the chunk terrain's linear sampler on the
    /// same atlas texture the nearest sampler reads - straight from the handle the client holds,
    /// instead of through the texture unit it was bound to. False for an id that is not one.
    /// </summary>
    internal bool TryNativeSamplerState(int samplerId, out SamplerState state) =>
        _standaloneSamplers.TryGetValue(samplerId, out state);

    private int ResolveNativeFramebuffer(int framebufferId) =>
        framebufferId == PassDeclaration.DefaultFramebuffer
            ? (_defaultRedirect > 0 ? _defaultRedirect : _defaultFramebuffer)
            : framebufferId;

    /// <summary>
    /// World/UI separation: the target <see cref="PassDeclaration.DefaultFramebuffer" /> stands for
    /// while the platform's UI scope is open - its UI image - or 0 for the window image itself. Every
    /// draw, clear, format query and readback that names Default resolves through
    /// <see cref="ResolveNativeFramebuffer" />, so each render system that has always drawn "onto the
    /// window" draws into the UI image instead without knowing it, and only the compose, which closes
    /// the scope first, writes the window image (VulkanClientPlatform.UiSeparation.cs).
    /// </summary>
    internal void RedirectDefaultFramebuffer(int framebufferId) => _defaultRedirect = framebufferId;

    /// <summary>The target Default currently resolves to instead of the window image; 0 for none.</summary>
    internal int DefaultFramebufferRedirect => _defaultRedirect;

    private int _defaultRedirect;

    /// <summary>
    /// The pipeline for a program and a piece of fixed state, created through the pipeline
    /// cache on first request and returned from this device's table afterwards. Null with a
    /// reason when the program is not linked, was linked as something else, or the request
    /// names no target formats.
    /// </summary>
    internal NativePipeline? RequestNativePipeline(NativePipelineDescription description, out string error)
    {
        error = "";
        if (!_programs.TryGetValue(description.ProgramId, out ShaderProgramResources? program))
        {
            error = "program " + description.ProgramId + " is not linked";
            return null;
        }
        if (description.PassName != null &&
            (!_programNames.TryGetValue(description.ProgramId, out string? name) ||
             !string.Equals(name, description.PassName, StringComparison.Ordinal)))
        {
            error = "program " + description.ProgramId + " is not '" + description.PassName + "'";
            return null;
        }
        if (description.VariantKey != null &&
            !string.Equals(NativeVariantOf(description.ProgramId), description.VariantKey, StringComparison.Ordinal))
        {
            error = "program '" + description.PassName + "' was linked for variant '" +
                NativeVariantOf(description.ProgramId) + "', not '" + description.VariantKey + "'";
            return null;
        }
        if (description.Targets == null)
        {
            error = "the request names no target formats";
            return null;
        }
        if (description.SamplesBoundDepth && description.DepthWrite)
        {
            error = "a pipeline that samples the bound depth attachment cannot also write depth";
            return null;
        }
        if (description.VertexLayoutId < 0 || description.VertexLayoutId >= _meshes.LayoutCount)
        {
            error = "vertex layout " + description.VertexLayoutId + " does not exist";
            return null;
        }

        ColorWriteTier tier = _context.Capabilities.ColorWriteTier;
        bool dynamicBlend = tier == ColorWriteTier.DynamicMask && _context.Capabilities.DynamicColorBlend;
        int count = description.Targets.ColorFormats.Length;

        int rawBlendId = _nativeBlends.Intern(new BlendSignature(description.Blend, count));
        int formatsId = _nativeFormats.Intern(description.Targets);

        // Keyed on the blend set as described, not as baked: the draw emits its dynamic blend and
        // write masks from the cached description, so two descriptions that bake to one Vulkan
        // pipeline under a dynamic tier are still two entries here (they share the PipelineKey below).
        var cacheKey = new NativePipelineCacheKey(description.ProgramId, formatsId, rawBlendId,
            description.DepthTest, description.DepthWrite, description.DepthCompare,
            description.Cull, description.Topology, description.VertexLayoutId, description.PolygonMode,
            description.FrontFace, description.LineWidth, description.SamplesBoundDepth);
        if (_nativePipelines.TryGetValue(cacheKey, out NativePipeline? cached) &&
            ReferenceEquals(cached.Program, program))
        {
            return cached;
        }

        // Baked blend state is owned by a new pipeline request. A cache hit only
        // needs the allocation-free raw signature above.
        var baked = new AttachmentBlend[Math.Max(count, 1)];
        for (int i = 0; i < baked.Length; i++)
        {
            AttachmentBlend blend = AttachmentBlend.Default;
            if (i < description.Blend.Length) blend = description.Blend[i];
            else blend.WriteMask = 0;

            if (tier == ColorWriteTier.DynamicMask)
            {
                if (dynamicBlend) blend = AttachmentBlend.Default;
                blend.WriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
            }
            baked[i] = blend;
        }
        int bakedBlendId = _nativeBlends.Intern(new BlendSignature(baked.AsSpan(0, count)));

        // The system's own vertex layout - the reserved empty one for a pass that generates its
        // vertices, the mesh's interned layout for a mesh draw - plus the constant attribute
        // defaults GL promises for anything the program declares and the layout does not carry.
        VertexLayoutDescription vertexLayout = _meshes.LayoutOf(description.VertexLayoutId)
            .WithDefaultsFor(program.Interface.VertexInputs);

        // The blend id is negative, a range of its own in the pipeline cache's key space. The
        // vertex layout is in the key, so a mesh pipeline and a fullscreen pipeline of the same
        // program are never the same entry.
        var key = new PipelineKey(
            ProgramId: description.ProgramId,
            VertexLayoutId: description.VertexLayoutId,
            TargetFormatsId: formatsId,
            BlendId: -(bakedBlendId + 1),
            PolygonMode: description.PolygonMode,
            TopologyClass: GlEnums.TopologyClassOf(description.Topology));

        var request = new GraphicsPipelineCache.PipelineRequest
        {
            Program = program,
            VertexLayout = vertexLayout,
            Targets = description.Targets,
            Blend = baked,
            PolygonMode = description.PolygonMode,
            Topology = description.Topology,
        };

        var pipeline = new NativePipeline(program, description, key, request, -(rawBlendId + 1),
            NativeColorWrite(description, program));
        _nativePipelines[cacheKey] = pipeline;

        // Created here rather than at the first draw where it can be; an async cache queues
        // the compile and the first draws are skipped until it is published.
        _pipelines.Prepare(key, request);
        return pipeline;
    }

    /// <summary>
    /// The colour write state a native pipeline's draws emit: each described write mask, cleared
    /// for an output the program never writes, packed for the device's tier (one enable bit per
    /// attachment, or four mask bits). It depends only on the pipeline, its program and the device,
    /// so <see cref="NativePipeline.ColorWrite" /> holds it instead of every draw recomputing it.
    /// </summary>
    private uint NativeColorWrite(NativePipelineDescription description, ShaderProgramResources program)
    {
        ColorWriteTier tier = _context.Capabilities.ColorWriteTier;
        if (tier == ColorWriteTier.PipelineKey) return 0;

        int colorStates = (int)Math.Min(_context.Capabilities.MaxColorAttachments, (uint)RenderLimits.MaxColorAttachments);
        var written = program.Interface.WrittenFragmentOutputs;
        uint colorWrite = 0;
        for (int i = 0; i < colorStates; i++)
        {
            ColorComponentFlags mask = i < description.Blend.Length ? description.Blend[i].WriteMask : 0;
            // An output the program never writes keeps the attachment's contents, as it does on GL.
            if (!written.Contains(i)) mask = 0;
            if (tier == ColorWriteTier.DynamicEnable)
            {
                if (mask != 0) colorWrite |= 1u << i;
            }
            else
            {
                colorWrite |= (uint)mask << (i * 4);
            }
        }
        return colorWrite;
    }

    /// <summary>Whether a pipeline's program is still the linked one of that id (a shader reload replaces it).</summary>
    internal bool IsNativePipelineLive(NativePipeline pipeline) =>
        _programs.TryGetValue(pipeline.ProgramId, out ShaderProgramResources? program) &&
        ReferenceEquals(program, pipeline.Program);

    /// <summary>Invalidates cached native pipeline bindings for a deleted program.</summary>
    private void ForgetNativePipelines(int programId)
    {
        if (_nativePipelines.Count == 0) return;

        var stale = new List<NativePipelineCacheKey>();
        foreach (KeyValuePair<NativePipelineCacheKey, NativePipeline> entry in _nativePipelines)
        {
            if (entry.Key.ProgramId == programId) stale.Add(entry.Key);
        }
        foreach (NativePipelineCacheKey key in stale) _nativePipelines.Remove(key);
    }

    // ---------------------------------------------------------------------- passes

    /// <summary>
    /// Opens a native pass on an explicit target: its colour slots, the textures it samples
    /// and the viewport its draws use. The draw-buffer mask is not consulted.
    /// </summary>
    private readonly Dictionary<string, string> _nativePassGpuLabels = new(StringComparer.Ordinal);

    /// <summary>
    /// GPU timestamps (stats log only): a native pass opened under a stage-level section
    /// gets its own "np_" section, so work no renderer labels still shows up by pass name.
    /// A narrower section a renderer already opened (a chunk pass, an upscaler) is kept.
    /// </summary>
    private void MarkNativePassSection(string name)
    {
        if (_gpuTimestamps == null) return;
        string open = _gpuTimestamps.OpenLabel;
        if (!open.StartsWith("stage_", StringComparison.Ordinal) &&
            !open.StartsWith("after_", StringComparison.Ordinal) &&
            !open.StartsWith("np_", StringComparison.Ordinal)) return;
        if (!_nativePassGpuLabels.TryGetValue(name, out string? label))
        {
            label = "np_" + name;
            _nativePassGpuLabels[name] = label;
        }
        GpuMark(label);
    }

    /// <summary>Declares a native pass, resolves its target and prepares the native rendering scope.</summary>
    /// <returns>Whether the declared pass could be prepared; NativeDrawRefusal records a rejected operation.</returns>
    internal bool BeginNativePass(NativePassDescription pass)
    {
        EndNativePass();
        NativeDrawRefusal = null;
        if (!_frameActive) return RefuseNativeDraw("no active frame");

        int id = ResolveNativeFramebuffer(pass.FramebufferId);
        VulkanFramebuffer? target = id > 0 ? _targets.Get(id) : null;
        if (target == null)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("native pass '" + pass.Name + "' skipped: no such target " + id);
            return RefuseNativeDraw("native pass '" + pass.Name + "' has no target " + id);
        }

        CommandBuffer commandBuffer = Commands;
        MarkNativePassSection(pass.Name);
        // A pass that coalesces into the current declaration (a GUI quad after the last one, an
        // entity in the stage's pass) would build a declaration DeclarePass then ignores.
        if (!_targets.IsCurrentDeclaration(id, pass.Name, pass.ColorSlots))
        {
            _targets.DeclarePass(commandBuffer, new PassDeclaration
            {
                Name = pass.Name,
                FramebufferId = id,
                ColorSlots = pass.ColorSlots,
                Reads = pass.Reads,
                TransientSlots = pass.TransientSlots,
                Flags = pass.Flags,
            }, id);
        }
        if (!ReferenceEquals(_targets.Bound, target)) _targets.Bind(commandBuffer, id);

        for (int slot = 0; slot < RenderLimits.MaxColorAttachments && pass.ClearSlots != 0; slot++)
        {
            if (((pass.ClearSlots >> slot) & 1) == 0) continue;
            _targets.ClearPassAttachment(commandBuffer, slot,
                pass.ClearValue[0], pass.ClearValue[1], pass.ClearValue[2], pass.ClearValue[3]);
        }

        _nativePass = pass;
        _nativeTarget = target;
        if (!pass.Generic) _nativePasses++;
        VulkanStats.NoteNativePass();
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native pass '" + pass.Name + "' target=" + id + " slots=" + pass.ColorSlots +
                " reads=" + string.Join(",", pass.Reads));
        }
        return true;
    }

    /// <summary>Closes the native pass and its scope.</summary>
    internal void EndNativePass() => EndNativePass(keepScope: false);

    /// <summary>
    /// Closes the native pass. <paramref name="keepScope" /> leaves the rendering scope and the
    /// pass declaration exactly as they were, for a native draw recorded inside a pass the
    /// surrounding stage has already declared - the entity loop, which records one native draw
    /// per entity into the Opaque stage's own pass and would otherwise end and restart the
    /// rendering scope once per entity. It is only correct when the pass description named that
    /// same declaration, so <see cref="BeginNativePass" /> coalesced into it rather than opening
    /// one of its own; a pass with its own name, slots or clears must be closed the normal way.
    ///
    /// The native-pass bookkeeping is cleared either way, so what a render system does between
    /// its draws (its uniforms by name) happens outside a native pass.
    /// </summary>
    internal void EndNativePass(bool keepScope)
    {
        if (_nativePass == null) return;

        _nativePass = null;
        _nativeTarget = null;
        if (!_frameActive || keepScope) return;

        CommandBuffer commandBuffer = Commands;
        _targets.EndPass(commandBuffer);
        _targets.EndRendering(commandBuffer);
    }

    // --------------------------------------------------------------------- uniforms

    /// <summary>Writes a uniform of a native pipeline's program at its resolved placement.</summary>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, ReadOnlySpan<byte> data)
    {
        switch (uniform.Block)
        {
        case NativeUniformBlock.Record:
            pipeline.Program.SetUniform(uniform.Offset, data);
            break;
        case NativeUniformBlock.Push:
            pipeline.Program.SetPushUniform(ShaderProgramResources.PushLocationBase + uniform.Offset, data);
            break;
        case NativeUniformBlock.Frame:
            WriteFrameGlobal(uniform.Offset, data);
            break;
        }
    }

    /// <summary>Writes scalar or vector bytes to a resolved native uniform in the pipeline's owned CPU shadow.</summary>
    /// <remarks>The NativeUniform block, offset and size must come from that pipeline's reflected interface.</remarks>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, float value) =>
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(&value, sizeof(float)));

    /// <summary>Writes scalar or vector bytes to a resolved native uniform in the pipeline's owned CPU shadow.</summary>
    /// <remarks>The NativeUniform block, offset and size must come from that pipeline's reflected interface.</remarks>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, int value) =>
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(&value, sizeof(int)));

    /// <summary>Writes scalar or vector bytes to a resolved native uniform in the pipeline's owned CPU shadow.</summary>
    /// <remarks>The NativeUniform block, offset and size must come from that pipeline's reflected interface.</remarks>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, float x, float y)
    {
        float* values = stackalloc float[2] { x, y };
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(values, 2 * sizeof(float)));
    }

    /// <summary>Writes scalar or vector bytes to a resolved native uniform in the pipeline's owned CPU shadow.</summary>
    /// <remarks>The NativeUniform block, offset and size must come from that pipeline's reflected interface.</remarks>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, float x, float y, float z)
    {
        float* values = stackalloc float[3] { x, y, z };
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(values, 3 * sizeof(float)));
    }

    /// <summary>Writes scalar or vector bytes to a resolved native uniform in the pipeline's owned CPU shadow.</summary>
    /// <remarks>The NativeUniform block, offset and size must come from that pipeline's reflected interface.</remarks>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, float x, float y, float z, float w)
    {
        float* values = stackalloc float[4] { x, y, z, w };
        WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(values, 4 * sizeof(float)));
    }

    /// <summary>
    /// A float run at a placement: a matrix, a vector array, a kernel. The model-view matrix a
    /// world system writes before each of its draws goes through here, which is what makes the
    /// write draw-frequency - a record member is snapshotted into this frame's uniform ring when
    /// the draw binds set 2, a push member is pushed with the draw's push block.
    /// </summary>
    internal void WriteNative(NativePipeline pipeline, NativeUniform uniform, ReadOnlySpan<float> values)
    {
        if (values.IsEmpty) return;
        fixed (float* first = values)
        {
            WriteNative(pipeline, uniform, new ReadOnlySpan<byte>(first, values.Length * sizeof(float)));
        }
    }

    // ------------------------------------------------------------------------ draws

    /// <summary>
    /// Records the fullscreen triangle of a native pass: the pass's reads are made
    /// shader-readable, the sampled textures resolve to bindless slots straight from their
    /// handles and sampler state, and the pipeline's fixed state is what the draw runs with.
    ///
    /// The mesh-drawing siblings are in VulkanDevice.Resources.cs; all of them share
    /// <see cref="BeginNativeDraw" />, which is this method's old body.
    /// </summary>
    internal bool DrawNativeFullscreen(NativePipeline pipeline, ReadOnlySpan<NativeTexture> textures,
        bool requirePipeline = false)
    {
        if (!BeginNativeDraw(pipeline, textures, 0, out CommandBuffer commandBuffer, out VulkanFramebuffer? target, requirePipeline))
        {
            return false;
        }

        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.Fullscreen, pipeline.ProgramId, target!.Id, 0));
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native fullscreen program=" + pipeline.ProgramId + " pass='" + _nativePass!.Name +
                "' target=" + target.Id);
        }
        _context.Api.CmdDraw(commandBuffer, 3, 1, 0, 0);
        NoteNativeDraw(NativeDrawKind.Fullscreen);
        return true;
    }

    /// <summary>
    /// Everything a native draw needs before its draw command: the pass and pipeline are
    /// checked, the textures the draw samples are put into the layout a shader read needs,
    /// the rendering scope is opened, the pipeline is bound, the sampled textures resolve to
    /// bindless slots straight from their handles and sampler state, the program's sets are
    /// bound (with <paramref name="meshId" />, so a chunk's storage-buffer vertex fetch and an
    /// entity's animation block resolve to this draw's mesh) and the dynamic state is emitted.
    ///
    /// Shared by the fullscreen draw and every mesh draw. None of it reads the GL state
    /// tracker, a texture unit or a draw-buffer mask.
    /// </summary>
    private bool BeginNativeDraw(NativePipeline pipeline, ReadOnlySpan<NativeTexture> textures, int meshId,
        out CommandBuffer commandBuffer, out VulkanFramebuffer? target, bool requirePipeline = false)
    {
        commandBuffer = default;
        target = null;
        NativeDrawRefusal = null;

        if (!_frameActive || _nativePass == null || _nativeTarget == null)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("native draw skipped: no open native pass");
            return RefuseNativeDraw("no open native pass; frameActive=" + _frameActive);
        }

        // Menu and loading-screen draws do not pass through a world render stage.
        // Start their render bracket at the first recorded draw instead.
        NoteRenderStageStarted();

        NativePassDescription pass = _nativePass;
        VulkanFramebuffer bound = _nativeTarget;
        if (!ReferenceEquals(_targets.Bound, bound))
        {
            return RefuseNativeDraw("native pass '" + pass.Name + "' lost its target before its draw");
        }

        ShaderProgramResources program = pipeline.Program;
        if (!IsNativePipelineLive(pipeline))
        {
            return RefuseNativeDraw("native pass '" + pass.Name + "' draws with program " + pipeline.ProgramId +
                ", which has been relinked or deleted");
        }

        commandBuffer = Commands;
        ReleaseReadSelfCopies();
        EnsureBindlessPlaceholdersReadable(commandBuffer);

        // The pass's reads, put into the layout a shader read needs. A colour attachment of
        // its own target is sampled through a pooled ReadSelf copy taken before the scope
        // opens - the atlas compositions (BlendedTextureManager, RenderTextureIntoFrameBuffer)
        // copy one region of an atlas into another region of the same atlas, and the copy is
        // what the draw samples (SnapshotColorAttachment). The bound
        // depth attachment with depth writes off is sampled in place, which the pipeline
        // declares (NativePipelineDescription.SamplesBoundDepth) and the scope then holds
        // read-only.
        bool depthReadOnly = false;
        for (int i = 0; i < textures.Length; i++)
        {
            if (!textures[i].Sampler.IsPresent) continue;
            VulkanTexture? texture = _textures.Get(textures[i].TextureId);
            if (texture == null) continue;
            if (_targets.IsBoundDepth(textures[i].TextureId))
            {
                if (!pipeline.Description.SamplesBoundDepth)
                {
                    return RefuseNativeDraw("native pass '" + pass.Name + "' samples texture " + textures[i].TextureId +
                        ", the depth attachment of its own target, through a pipeline that does not declare it");
                }
                depthReadOnly = true;
                continue;
            }
            if (_targets.IsAttachmentOfBound(textures[i].TextureId))
            {
                if (texture.Aspect != ImageAspectFlags.ColorBit)
                {
                    return RefuseNativeDraw("native pass '" + pass.Name + "' samples texture " + textures[i].TextureId +
                        ", a non-colour attachment of its own target");
                }
                SnapshotColorAttachment(commandBuffer, textures[i].TextureId, texture);
                if (_sampledTextureOverrides.TryGetValue(textures[i].TextureId, out int copyId) &&
                    _textures.Get(copyId) is { } copy)
                    RequireSamplerStages(commandBuffer, pipeline.SamplerStageUsages(textures[i].Sampler.Index), copy);
                continue;
            }
            _targets.FlushPendingClears(commandBuffer, texture);
            RequireSamplerStages(commandBuffer, pipeline.SamplerStageUsages(textures[i].Sampler.Index), texture);
        }
        PrepareUnnamedFrameTextures(commandBuffer, pipeline, textures);
        if (_barriers.Pending != 0) _targets.EndRendering(commandBuffer);
        _barriers.Flush(commandBuffer);

        // Decided before the scope opens, since it decides the depth attachment's layout.
        _targets.SetDepthReadOnly(depthReadOnly);
        _targets.EnsureRendering(commandBuffer);

        if (!_targets.ScopeFormatsMatch(bound, pipeline.Description.Targets))
        {
            return RefuseNativeDraw("native pass '" + pass.Name + "' has target formats its pipeline was not built for");
        }

        // Presentation cannot skip its only scene draw while a first-use PSO compiles.
        // Get retains the normal cache fast path and compiles blocking only on a miss.
        Pipeline handle;
        if (requirePipeline) handle = _pipelines.Get(pipeline.Key, pipeline.Request);
        else if (!_pipelines.TryGet(pipeline.Key, pipeline.Request, out handle))
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("native draw skipped: pipeline for program " + pipeline.ProgramId + " still compiling");
            }
            return RefuseNativeDraw("pipeline for program " + pipeline.ProgramId + " is still compiling");
        }

        Vk api = _context.Api;
        FrameSlot frameSlot = _frames.Current;
        ulong recordingSerial = frameSlot.CommandBuffer.Handle == commandBuffer.Handle ? frameSlot.RecordingSerial : 0;
        if (recordingSerial == 0 || _nativeBoundPipelineSerial != recordingSerial ||
            _nativeBoundPipelineHandle != handle.Handle)
        {
            api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, handle);
            _nativeBoundPipelineSerial = recordingSerial;
            _nativeBoundPipelineHandle = handle.Handle;
        }

        // The default-attribute binding persists for the recording, and mesh binds start at
        // binding 0 and never reach it, so it is bound once per recording like the pipeline.
        VertexLayoutDescription vertexLayout = pipeline.Request.VertexLayout;
        if (vertexLayout.Bindings.Length > 0 &&
            vertexLayout.Bindings[^1].Binding == VertexLayoutDescription.DefaultAttributeBinding &&
            _defaultAttributes != null)
        {
            Silk.NET.Vulkan.Buffer defaults = _defaultAttributes.Handle;
            if (recordingSerial == 0 || _nativeBoundDefaultsSerial != recordingSerial ||
                _nativeBoundDefaultsHandle != defaults.Handle)
            {
                ulong offset = 0;
                api.CmdBindVertexBuffers(commandBuffer,
                    VertexLayoutDescription.DefaultAttributeBinding, 1, &defaults, &offset);
                _nativeBoundDefaultsSerial = recordingSerial;
                _nativeBoundDefaultsHandle = defaults.Handle;
            }
        }

        // The program's push block, then this draw's slots over it.
        int pushSize = program.Interface.PushConstantSize;
        if (program.PushShadow != null) program.PushShadow.CopyTo(_pushShadow, 0);
        else if (pushSize > 0) _pushShadow.AsSpan(0, pushSize).Clear();

        for (int i = 0; i < textures.Length; i++)
        {
            NativeTexture sampled = textures[i];
            NativeSamplerSlot sampler = sampled.Sampler;
            if (!sampler.IsPresent) continue;

            // A ReadSelf copy taken above stands in for the attachment it copies.
            VulkanTexture? texture = _textures.Get(
                _sampledTextureOverrides.TryGetValue(sampled.TextureId, out int readSelfCopy)
                    ? readSelfCopy
                    : sampled.TextureId);
            if (texture != null && !BindlessKinds.Suits(TextureShape.Of(texture), sampler.Kind))
            {
                if (RenderTrace.Enabled)
                {
                    RenderTrace.Write("native sampler " + sampler.Index + " on program " + program.ProgramId +
                        " has texture " + sampled.TextureId + " of format " + texture.Format +
                        " bound, which it cannot sample; using a placeholder");
                }
                texture = null;
            }

            SamplerState sampling = SamplerState.Default;
            if (texture != null)
            {
                // MAX_LEVEL belongs to the texture, even when the caller overrides the filters.
                sampling = sampled.Sampling is { } state
                    ? state with { MaxLevel = texture.State.MaxLevel }
                    : texture.State;
            }

            // The bound depth attachment is sampled in the read-only depth layout, the one the
            // scope holds it in, exactly as the emulated resolve keys it.
            ImageLayout layout = depthReadOnly && _targets.IsBoundDepth(sampled.TextureId)
                ? ImageLayout.DepthReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;

            if (sampler.FrameBinding >= 0)
            {
                SamplerBindingValue value = texture == null
                    ? default
                    : new SamplerBindingValue((uint)sampler.FrameBinding, texture.View,
                        _textures.Samplers.Get(BindlessKinds.EffectiveState(sampling, sampler.Kind)), texture.Id, layout);
                if (texture == null) VulkanStats.NoteSamplerPlaceholder();
                int frameIndex = FrameTextureIndex(sampler.FrameBinding);
                lock (_frameTextureLock)
                {
                    _frameTextureValues[frameIndex] = value;
                    _frameTextureIds[frameIndex] = texture == null ? 0 : sampled.TextureId;
                }
                continue;
            }

            uint slot = _bindless!.Resolve(texture, sampler.Kind, sampling, layout);
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("native sample program=" + program.ProgramId + " sampler=" + sampler.Index +
                    " logical=" + sampled.TextureId + " physical=" + (texture?.Id ?? 0) +
                    " slot=" + slot + " layout=" + layout);
            }
            VulkanStats.NoteBindlessSlotResolution();
            BitConverter.TryWriteBytes(_pushShadow.AsSpan(sampler.PushOffset, ProgramInterfaceLayout.SlotBytes), slot);
        }

        if (!BindProgramSets(commandBuffer, program, meshId))
            return RefuseNativeDraw("uniform ring exhausted; this draw has no valid snapshot");
        EmitNativeDynamicState(commandBuffer, bound, pass, pipeline);

        target = bound;
        return true;
    }

    /// <summary>
    /// A frame texture the program reads (set 0) that this draw does not name keeps the value the
    /// last draw that named it left - GL's "whatever the unit still holds". That texture may have
    /// been written since (the liquid depth pass renders into liquidDepth's image before the sky
    /// dome, whose route names only sky and glow): it is put back into the read layout here, and
    /// replaced by the placeholder when it is an attachment of this draw's own target, which a
    /// shader cannot read.
    /// </summary>
    private void PrepareUnnamedFrameTextures(CommandBuffer commandBuffer, NativePipeline pipeline,
        ReadOnlySpan<NativeTexture> textures)
    {
        ShaderProgramResources program = pipeline.Program;
        if (!program.Interface.UsesFrameTextures) return;
        List<SamplerBinding> samplers = program.Interface.Samplers;
        for (int samplerIndex = 0; samplerIndex < samplers.Count; samplerIndex++)
        {
            SamplerBinding declared = samplers[samplerIndex];
            if (!declared.IsFrameTexture) continue;
            bool named = false;
            for (int i = 0; i < textures.Length && !named; i++)
            {
                named = textures[i].Sampler.IsPresent && textures[i].Sampler.FrameBinding == declared.FrameBinding;
            }
            if (named) continue;

            int index = FrameTextureIndex(declared.FrameBinding);
            SamplerBindingValue stale;
            int textureId;
            lock (_frameTextureLock)
            {
                stale = _frameTextureValues[index];
                textureId = _frameTextureIds[index];
            }
            if (stale.View.Handle == 0) continue;

            // Gone, recreated, a ReadSelf copy of an attachment, or an attachment of this draw's own
            // target: nothing the shader may read any more, so the placeholder stands in.
            VulkanTexture? texture = _textures.Get(textureId);
            if (texture == null || texture.Id != stale.Resource || _targets.IsAttachmentOfBound(textureId))
            {
                lock (_frameTextureLock)
                {
                    _frameTextureValues[index] = default;
                    _frameTextureIds[index] = 0;
                }
                continue;
            }
            if (stale.Layout != ImageLayout.ShaderReadOnlyOptimal)
            {
                lock (_frameTextureLock) _frameTextureValues[index] = stale with { Layout = ImageLayout.ShaderReadOnlyOptimal };
            }
            _targets.FlushPendingClears(commandBuffer, texture);
            RequireSamplerStages(commandBuffer, pipeline.SamplerStageUsages(samplerIndex), texture);
        }
    }

    /// <summary>
    /// Requests visibility for every shader stage that reads a sampler, even without a layout change.
    /// The stages come from the pipeline (<see cref="NativePipeline.SamplerStageUsages" />), resolved
    /// when it was created.
    /// </summary>
    private void RequireSamplerStages(CommandBuffer commands, ReadOnlySpan<ResourceUsage> stageUsages, VulkanTexture texture)
    {
        foreach (ResourceUsage usage in stageUsages)
        {
            _textures.Require(_barriers, commands, texture, usage);
        }
    }

    /// <summary>The dynamic state of a native draw: the pipeline's fixed state and the pass's viewport, never the tracker's.</summary>
    private void EmitNativeDynamicState(CommandBuffer commandBuffer, VulkanFramebuffer target,
        NativePassDescription pass, NativePipeline pipeline)
    {
        ColorWriteTier tier = _context.Capabilities.ColorWriteTier;
        bool dynamicBlend = tier == ColorWriteTier.DynamicMask && _context.Capabilities.DynamicColorBlend;
        int colorStates = (int)Math.Min(_context.Capabilities.MaxColorAttachments, (uint)RenderLimits.MaxColorAttachments);
        NativePipelineDescription description = pipeline.Description;
        // Fixed per pipeline, program and device tier (NativeColorWrite).
        uint colorWrite = pipeline.ColorWrite;

        int width = pass.ViewportWidth >= 0 ? pass.ViewportWidth : (int)target.Width;
        int height = pass.ViewportHeight >= 0 ? pass.ViewportHeight : (int)target.Height;
        var values = new DynamicStateValues
        {
            Viewport = new Viewport(pass.ViewportX, pass.ViewportY, width, height, 0f, 1f),
            Scissor = pass.Scissor ?? new Rect2D(new Offset2D(0, 0), new Extent2D(target.Width, target.Height)),
            CullMode = description.Cull,
            FrontFace = description.FrontFace,
            Topology = description.Topology,
            DepthTest = description.DepthTest,
            DepthWrite = description.DepthWrite,
            DepthCompare = description.DepthCompare,
            StencilTest = false,
            StencilFail = StencilOp.Keep,
            StencilPass = StencilOp.Keep,
            StencilDepthFail = StencilOp.Keep,
            StencilCompare = CompareOp.Always,
            StencilCompareMask = 0xFF,
            StencilWriteMask = 0xFF,
            StencilReference = 0,
            // Clamped through the same device range as the emulated path's, so a native line
            // draw and the seam's neutral body rasterize identically.
            LineWidth = _context.Capabilities.ClampLineWidth(description.LineWidth),
            ColorWrite = colorWrite,
            BlendStateId = dynamicBlend ? pipeline.DynamicBlendId : 0,
        };

        FrameSlot slot = _frames.Current;
        ulong serial = slot.CommandBuffer.Handle == commandBuffer.Handle ? slot.RecordingSerial : 0;

        Span<AttachmentBlend> blendStates = stackalloc AttachmentBlend[dynamicBlend ? colorStates : 0];
        for (int i = 0; i < blendStates.Length; i++)
        {
            blendStates[i] = i < description.Blend.Length ? description.Blend[i] : AttachmentBlend.Default;
        }
        EmitDynamicState(commandBuffer, values, serial, tier, dynamicBlend, colorStates, blendStates);
    }
}
