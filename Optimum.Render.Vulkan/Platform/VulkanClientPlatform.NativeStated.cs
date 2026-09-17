using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// The generic native draw: any program, drawn with the state the client stated through this
// platform's virtuals (StatedRenderState) - the last route before the emulated one. The dedicated
// routes (chunks, entities, sky, particles, GUI, clouds, the post chain) keep their own contracts
// and run first; this one takes everything they do not recognise: mod renderers with their own
// programs, the vanilla programs without a dedicated route (aurora, block highlights, held item,
// lines, wireframe, the debug views) and the seams' neutral bodies behind the route switches.
//
// What it states, and from where (all client statements, none read back from the device):
// - target: the framebuffer a fork renderer bound by id, else CurrentFrameBuffer, else the default;
//   every colour slot of the target is in the pass, and the draw buffers the client selected for
//   that framebuffer become per-attachment write masks (draw buffers are write masks, decision 4);
// - blend, colour mask, depth, cull, line width, polygon mode, viewport and scissor: StatedRenderState,
//   with OpenGL's semantics (a disabled blend keeps its functions, glBlendFunc sets every draw buffer);
// - textures: per sampler, the texture on the unit the program points it at (its SetSamplerUnit
//   mapping, else the sampler's declaration order - the same resolution the emulated draw makes),
//   with the unit's standalone sampler override if one is bound;
// - the depth attachment of the target sampled with depth writes off is read in the read-only
//   layout (SamplesBoundDepth), as the emulated draw does.
// Stencil is not applied: no framebuffer of this client has a stencil attachment, so a stencil
// test passes on either path (StatedRenderState).
//
// OPTIMUM_VK_NATIVE_STATED=0 sends these draws to the emulated route; OPTIMUM_VK_STATED_CHECK=1
// compares every stated value against the device's tracked state at each draw and logs each
// distinct mismatch once - the evidence that the device state can go.
// Pinned by Optimum.Tests/native-world-systems-coverage-tests.cs.
public partial class VulkanClientPlatform
{
    /// <summary>The fixed-function state the client stated, with OpenGL's semantics.</summary>
    internal readonly StatedRenderState stated = new();

    internal bool NativeStatedEnabled { get; set; } = Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_STATED") != "0";

    private static readonly bool StatedCheck = Environment.GetEnvironmentVariable("OPTIMUM_VK_STATED_CHECK") == "1";

    private readonly HashSet<string> statedCheckReported = new(StringComparer.Ordinal);

    private readonly HashSet<int> statedRefusalReported = new();

    /// <summary>Draws the generic route recorded, and draws it handed back to the emulated route. Tests and the trace read them.</summary>
    internal long StatedDrawsForTests { get; private set; }

    internal long StatedRefusalsForTests { get; private set; }

    // ------------------------------------------------------------------ recording helpers

    /// <summary>The draw buffers the client selects for a framebuffer (0 is the default target).</summary>
    internal void StateDrawBuffers(int framebufferId, int mask)
    {
        stated.SetDrawBuffers(framebufferId == 0 ? PassDeclaration.DefaultFramebuffer : framebufferId, (uint)mask);
        device.SetDrawBuffers(framebufferId, mask);
    }

    /// <summary><c>glBlendEquationi</c> + <c>glBlendFuncSeparatei</c>.</summary>
    internal void StateSlotBlend(int slot, int equation, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        stated.SetSlotBlend(slot, equation, srcColor, dstColor, srcAlpha, dstAlpha);
        device.SetBlendEquation(slot, equation);
        device.SetBlendFuncSeparate(slot, srcColor, dstColor, srcAlpha, dstAlpha);
    }

    /// <summary><c>glBlendFuncSeparatei</c> alone: the attachment's equation stays.</summary>
    internal void StateSlotBlendFunc(int slot, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        stated.SetSlotFunc(slot, srcColor, dstColor, srcAlpha, dstAlpha);
        device.SetBlendFuncSeparate(slot, srcColor, dstColor, srcAlpha, dstAlpha);
    }

    /// <summary>Blend on with a mode's functions on every attachment, or off with the functions kept.</summary>
    internal void StateBlend(bool on, EnumBlendMode mode)
    {
        stated.SetBlendEnabled(on);
        if (on) stated.SetBlendMode(mode);
        device.SetBlend(on, mode);
    }

    /// <summary>A viewport the client states (the fork bridge, and the platform's own full-target binds).</summary>
    internal void NoteForkViewport(int x, int y, int width, int height) =>
        stated.Viewport = new Rect2D(new Offset2D(x, y), new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));

    internal void NoteForkTexture(int unit, int textureId) => stated.BindTexture(unit, textureId);

    internal void NoteForkDrawBuffers(int framebufferId, int mask) =>
        stated.SetDrawBuffers(framebufferId == 0 ? PassDeclaration.DefaultFramebuffer : framebufferId, (uint)mask);

    // ------------------------------------------------------------------------- the route

    /// <summary>
    /// Records one draw of the current program natively from the stated state. A null
    /// <paramref name="vao" /> is the fullscreen triangle; <paramref name="starts" /> is a
    /// pool's multi-draw. False: nothing was recorded and the caller runs the emulated draw.
    /// </summary>
    private bool TryDrawStated(VAO? vao, int instances, int[]? starts, int[]? sizes, int groupCount)
    {
        if (!NativeStatedEnabled || device == null) return false;
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (program == null || program.ProgramId <= 0) return false;
        if (vao != null && (vao.VaoId == 0 || vao.Disposed)) return false;

        // The target and every colour slot attached to it on the device. Not the FrameBufferRef's own
        // list: the OIT accumulation targets are attached to Transparent at slots 3-5 without being in
        // its ColorTextureIds, and a pass without them drops the accumulated colour (2026-09-17: water
        // drew black through this route until the slots came from the attachments).
        FrameBufferRef? target = forkFramebuffer > 0 ? null : CurrentFrameBuffer;
        int framebufferId = forkFramebuffer > 0
            ? forkFramebuffer
            : target != null ? target.FboId : PassDeclaration.DefaultFramebuffer;
        RenderTargetFormats? all = device.NativeTargetFormats(framebufferId, uint.MaxValue);
        if (all == null) return Refuse(program, "framebuffer " + framebufferId + " does not exist");
        int attached = all.ColorFormats.Length;
        uint slots = attached >= 32 ? uint.MaxValue : (1u << attached) - 1u;
        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, slots);
        if (formats == null) return Refuse(program, "no formats for framebuffer " + framebufferId);

        int layoutId = vao != null ? device.NativeMeshLayoutId(vao.VaoId) : MeshManager.EmptyLayoutId;
        if (layoutId < 0) return Refuse(program, "the mesh has no layout");

        // Every sampler the program declares, from the unit it points at.
        List<string> names = device.SamplerNamesOf(program.ProgramId);
        int depthTexture = device.NativeFramebufferDepthTexture(framebufferId);
        bool samplesBoundDepth = false;
        var reads = new int[names.Count];
        var units = new int[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            units[i] = device.NativeSamplerUnit(program.ProgramId, names[i]);
            reads[i] = stated.TextureAt(units[i]);
            if (reads[i] != 0 && reads[i] == depthTexture)
            {
                if (stated.DepthWrite && stated.DepthTest)
                {
                    return Refuse(program, "it samples the depth attachment it writes");
                }
                samplesBoundDepth = true;
            }
        }

        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++) blend[i] = stated.AttachmentFor(framebufferId, i);

        var description = new NativePipelineDescription
        {
            ProgramId = program.ProgramId,
            Blend = blend,
            DepthTest = stated.DepthTest,
            DepthWrite = stated.DepthWrite && !samplesBoundDepth,
            DepthCompare = stated.DepthCompare,
            Cull = stated.CullMode,
            Topology = vao != null ? device.NativeMeshTopology(vao.VaoId) : PrimitiveTopology.TriangleList,
            PolygonMode = stated.Wireframe ? PolygonMode.Line : PolygonMode.Fill,
            LineWidth = stated.LineWidth,
            VertexLayoutId = layoutId,
            SamplesBoundDepth = samplesBoundDepth,
            Targets = formats,
        };
        NativePipeline? pipeline = device.RequestNativePipeline(description, out string error);
        if (pipeline == null) return Refuse(program, error);

        var textures = new NativeTexture[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            int sampler = stated.SamplerAt(units[i]);
            textures[i] = new NativeTexture(pipeline.Sampler(names[i]), reads[i],
                sampler != 0 ? device.NativeStandaloneSampler(sampler) : null);
        }

        if (StatedCheck) CheckStatedAgainstDevice(program, framebufferId, blend, description, textures);

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        Rect2D viewport = stated.Viewport;
        bool drawn = false;
        if (device.BeginNativePass(new NativePassDescription
        {
            Name = "Stated/" + framebufferId,
            FramebufferId = framebufferId,
            ColorSlots = slots,
            Reads = reads,
            Flags = PassFlags.AllowSplit,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
            Scissor = stated.ScissorEnabled ? stated.Scissor : null,
        }))
        {
            drawn = vao == null
                ? device.DrawNativeFullscreen(pipeline, textures)
                : starts != null
                    ? device.DrawNativeMeshMulti(pipeline, vao.VaoId, starts, sizes!, groupCount, textures)
                    : device.DrawNativeMeshInstanced(pipeline, vao.VaoId, instances, textures);
        }
        device.EndNativePass();
        // The emulated calls a system makes between its draws still address the target it bound.
        if (framebufferId == PassDeclaration.DefaultFramebuffer) device.BindDefaultFramebuffer();
        else device.BindFramebuffer(framebufferId);
        SetPassContext(outer, outerFlags);
        if (drawn) StatedDrawsForTests++;
        else RuntimeStats.drawCallsCount--;
        return drawn;
    }

    private bool Refuse(ShaderProgramBase program, string reason)
    {
        StatedRefusalsForTests++;
        if (statedRefusalReported.Add(program.ProgramId))
        {
            Logger.Warning("Optimum: program '{0}' draws through the emulated route: {1}", program.PassName ?? "", reason);
        }
        return false;
    }

    private void CheckStatedAgainstDevice(ShaderProgramBase program, int framebufferId, AttachmentBlend[] blend,
        NativePipelineDescription description, NativeTexture[] textures)
    {
        List<string> mismatches = device.DebugStatedMismatches(program.ProgramId, framebufferId, blend, description,
            stated.Viewport, stated.ScissorEnabled, stated.Scissor, textures);
        foreach (string mismatch in mismatches)
        {
            string key = (program.PassName ?? "") + ": " + mismatch;
            if (statedCheckReported.Add(key)) Logger.Warning("Optimum stated check: {0}", key);
        }
    }
}
