using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native render systems (docs/vulkan-native-render-systems.md), Phase 3b decision 5
// stage 2: the sky systems that are not the dome, the particle pools and the decal pool, all
// on the native device API the sky dome proved.
//
// What they draw, and where the other side is:
//   - the night sky box   - SystemRenderNightSky's 75-unit star cube, seam
//                           ClientPlatformAbstract.RenderNightSkyBox, neutral body RenderMesh;
//   - the moon            - SystemRenderSunMoon's quad under celestialobject, seam
//                           ClientPlatformAbstract.RenderCelestialQuad, neutral body RenderMesh;
//   - the cube particles  - SystemRenderParticles' instanced pool draw on Primary, seam
//                           ClientPlatformAbstract.RenderParticles, neutral body
//                           RenderMeshInstanced;
//   - the decals          - SystemRenderDecals' pooled multi-draw, scope seam
//                           ClientPlatformAbstract.BeginDecalPass / EndDecalPass with empty
//                           neutral bodies: the lib runs the vanilla MeshDataPool.Draw between
//                           them and the pool's RenderMesh multi-draw is taken natively while
//                           the scope is open (the mesh handle is internal in the vanilla API,
//                           so it can never be a seam parameter).
// NativeWorldEnabled false takes the neutral body on the Vulkan device too, which is the route
// the differential tests compare against.
//
// Target and slots: every one of them draws into the framebuffer its stage has bound - Primary
// for all four. The colour slots are NativeWorldPassColorSlots: the same set the emulated route's
// draw-buffer mask would hold, derived from the platform's own motion-window state
// (MotionAttachmentIndex, OptimumMotionWriteActive) rather than from the GL state tracker
// (decision 3). That is what makes a cube particle's and a decal's motion vector land through
// the one writer include exactly while their caller's window is open, and keeps the motion
// attachment out of the night sky's and the moon's scope entirely.
//
// State that is not obvious, per system, and where it comes from:
//   - the night sky and the moon run with the depth test off, because their callers call
//     GlDisableDepthTest; GL writes no depth with the test off, so depth writes are off too.
//     The night sky also disables culling itself. The moon inherits the cull state the night
//     sky left, which is off - no vanilla Opaque renderer between them turns it back on.
//   - the cube particles and the decals run with the depth test and depth writes on: the Opaque
//     stage is entered with ChunkRenderer.RenderOpaque's depth mask and test, and the AfterOIT
//     stage is entered with ClientMain's own GlDepthMask/GlEnableDepthTest. Culling is off for
//     both: SystemRenderNightSky leaves it off for the rest of the Opaque stage, and
//     SystemRenderDecals calls GlDisableCullFace itself.
//   - blending is on in the standard mode for the moon, the cube particles and the decals
//     (their callers call GlToggleBlend(on: true)), and off for the night sky.
//   - the motion attachment never blends. Inside a motion window the native pass states
//     replace-blending on that one attachment per attachment, which is what
//     ApplyOptimumMotionBlendState does for an emulated draw.
//
// What pins them: NativeWorldSystemsTests (old route against native route, including the motion
// attachment bit for bit) and Optimum.Tests/native-world-systems-coverage-tests.cs.
public partial class VulkanClientPlatform
{
    /// <summary>
    /// False runs each seam's neutral body - the OpenGL body's own draw - on the Vulkan device
    /// instead of the native pass: the old route the differential tests compare against, in the
    /// pattern of <see cref="NativeSkyEnabled" />.
    /// </summary>
    internal bool NativeWorldEnabled { get; set; } = Environment.GetEnvironmentVariable("OPTIMUM_VK_NATIVE_WORLD") != "0";

    /// <summary>The star cube's pipeline: no per-draw uniform, one samplerCube.</summary>
    private readonly NativeMeshPass nativeNightSky =
        new("nightsky", Array.Empty<string>(), new[] { "ctex" });

    /// <summary>
    /// The moon's pipeline. "tex" is the body's own texture; "sky" and "glow" are the frame
    /// textures skycolor.fsh reads to shade the body against the sky behind it, and they are
    /// passed here because a native draw resolves what it samples from handles and nothing else
    /// in this pass would refresh the frame table's entries for them.
    /// </summary>
    private readonly NativeMeshPass nativeCelestial =
        new("celestialobject", Array.Empty<string>(), new[] { "tex", "sky", "glow" });

    /// <summary>The cube particle pool's pipeline: no per-draw uniform and no sampler at all.</summary>
    private readonly NativeMeshPass nativeParticlesCube =
        new("particlescube", Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// The decal pool's pipeline. origin and modelViewMatrix are DRAW uniforms the client system
    /// already set through the program's own setters, so they ride in the program's push shadow
    /// and the pass writes nothing per draw; the two atlases are its samplers.
    /// </summary>
    private readonly NativeMeshPass nativeDecals =
        new("decals", Array.Empty<string>(), new[] { "decalTexture", "blockTexture" });

    // ------------------------------------------------------------------ shared derivations

    /// <summary>
    /// The colour slots a world pass writes: the set the emulated route's draw-buffer mask would
    /// hold at this point in the frame, computed from the platform's own motion-window state
    /// rather than read back out of the tracker.
    ///
    /// Outside Primary, and with TAA off (<see cref="ClientPlatformWindows.MotionAttachmentIndex" />
    /// negative), that is every bound colour slot. On Primary with TAA on it is Primary's default
    /// colour set, plus the motion attachment exactly while a motion window is open - the two
    /// sets <see cref="EnableMotionDrawBuffers" /> and <see cref="RestorePrimaryDrawBuffers" />
    /// switch between.
    /// </summary>
    private uint NativeWorldPassColorSlots(FrameBufferRef target)
    {
        uint all = NativeAllColorSlots(target);
        int motion = MotionAttachmentIndex;
        if (motion < 0 || motion >= 32) return all;

        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || buffers.Count == 0 || !ReferenceEquals(target, buffers[0])) return all;

        uint mask = OptimumMotionWriteActive
            ? (1u << (motion + 1)) - 1u
            : (1u << motion) - 1u;
        return mask & all;
    }

    /// <summary>
    /// A world pass's per-attachment blend: the caller's blend mode on every colour attachment,
    /// except the motion attachment inside an open motion window, which replaces rather than
    /// blends. A blended motion vector is a weighted average of two surfaces' displacements and
    /// belongs to neither, which is why <see cref="ApplyOptimumMotionBlendState" /> forces
    /// (ONE, ZERO) with FUNC_ADD there on the emulated route; this states the same thing on the
    /// pipeline instead of through a tracked toggle.
    /// </summary>
    private AttachmentBlend[] NativeWorldBlend(RenderTargetFormats formats, bool blending)
    {
        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++)
        {
            blend[i] = AttachmentBlend.Default;
            blend[i].Enabled = blending;
        }

        int motion = MotionAttachmentIndex;
        if (OptimumMotionWriteActive && motion >= 0 && motion < blend.Length)
        {
            blend[motion] = AttachmentBlend.Default;
            blend[motion].Enabled = blending;
            blend[motion].SrcColor = BlendFactor.One;
            blend[motion].DstColor = BlendFactor.Zero;
            blend[motion].SrcAlpha = BlendFactor.One;
            blend[motion].DstAlpha = BlendFactor.Zero;
        }
        return blend;
    }

    /// <summary>
    /// Everything a native world draw needs before it can be recorded: the target the stage
    /// bound, the program it is drawing with, the mesh and its vertex layout, the colour slots
    /// and their formats, and the pipeline for that combination. False means the caller takes
    /// its seam's neutral body, which is always a legal answer.
    /// </summary>
    private bool NativeWorldPrepare(NativeMeshPass pass, MeshRef mesh, bool blending, bool depth,
        out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline)
    {
        target = null!;
        vao = null!;
        slots = 0;
        pipeline = null!;

        if (!NativeWorldEnabled || device == null || mesh == null) return false;

        FrameBufferRef bound = CurrentFrameBuffer;
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        var buffers = mesh as VAO;
        if (bound == null || program == null || buffers == null || buffers.VaoId == 0 || buffers.Disposed)
        {
            return false;
        }

        int layoutId = device.NativeMeshLayoutId(buffers.VaoId);
        if (layoutId < 0) return false;

        uint colorSlots = NativeWorldPassColorSlots(bound);
        if (colorSlots == 0) return false;

        RenderTargetFormats? formats = device.NativeTargetFormats(bound.FboId, colorSlots);
        if (formats == null) return false;

        NativePipeline? built = NativeMeshPipelineFor(pass, program, bound.FboId, colorSlots, layoutId,
            new NativePipelineDescription
            {
                Blend = NativeWorldBlend(formats, blending),
                DepthTest = depth,
                DepthWrite = depth,
                DepthCompare = CompareOp.Less,
                Cull = CullModeFlags.None,
                Topology = PrimitiveTopology.TriangleList,
            });
        if (built == null) return false;

        target = bound;
        vao = buffers;
        slots = colorSlots;
        pipeline = built;
        return true;
    }

    /// <summary>
    /// Opens the native pass for one world draw on the target its stage bound, with the reads it
    /// samples declared outright.
    /// </summary>
    private bool NativeWorldBeginPass(string name, FrameBufferRef target, uint slots, int[] reads)
    {
        Rect2D viewport = device.NativeCurrentViewport;
        return device.BeginNativePass(new NativePassDescription
        {
            Name = name + "/" + target.FboId,
            FramebufferId = target.FboId,
            ColorSlots = slots,
            Reads = reads,
            Flags = PassFlags.AllowSplit,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
        });
    }

    /// <summary>
    /// Closes the native pass and declares the stage's own pass context again, because every
    /// renderer after this one draws into the same target through the emulated path - the same
    /// restoration the sky dome's pass and the TAA resolve's do.
    /// </summary>
    private void NativeWorldEndPass(FrameBufferRef target, string outer, PassFlags outerFlags)
    {
        device.EndNativePass();
        device.BindFramebuffer(target.FboId);
        SetPassContext(outer, outerFlags);
    }

    // ------------------------------------------------------------------------ the seams

    /// <summary>The star cube's draw: the native pass, or the seam's neutral body.</summary>
    public override void RenderNightSkyBox(MeshRef nightSkyBox, int cubeTextureId)
    {
        // No depth and no blending: SystemRenderNightSky has called GlDisableDepthTest and
        // GlDisableCullFace, and nightsky.fsh writes opaque colour into slot 0.
        if (!NativeWorldPrepare(nativeNightSky, nightSkyBox, blending: false, depth: false,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            base.RenderNightSkyBox(nightSkyBox, cubeTextureId);
            return;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        if (NativeWorldBeginPass("NightSky", target, slots, new[] { cubeTextureId }))
        {
            // The cube map resolves into the bindless table's cube array, which the sampler's
            // own kind selects - a 2D texture bound here would be refused rather than sampled.
            device.DrawNativeMesh(pipeline, vao.VaoId, new[]
            {
                new NativeTexture(nativeNightSky.Samplers[0], cubeTextureId),
            });
        }
        NativeWorldEndPass(target, outer, outerFlags);
    }

    /// <summary>The moon's draw: the native pass, or the seam's neutral body.</summary>
    public override void RenderCelestialQuad(MeshRef quad, int bodyTextureId, int skyTextureId, int glowTextureId)
    {
        // Blending on in the standard mode and no depth: SystemRenderSunMoon has called
        // GlToggleBlend(on: true), GlDisableCullFace and GlDisableDepthTest before both bodies.
        if (!NativeWorldPrepare(nativeCelestial, quad, blending: true, depth: false,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            base.RenderCelestialQuad(quad, bodyTextureId, skyTextureId, glowTextureId);
            return;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        if (NativeWorldBeginPass("Celestial", target, slots, new[] { bodyTextureId, skyTextureId, glowTextureId }))
        {
            device.DrawNativeMesh(pipeline, vao.VaoId, new[]
            {
                new NativeTexture(nativeCelestial.Samplers[0], bodyTextureId),
                new NativeTexture(nativeCelestial.Samplers[1], skyTextureId),
                new NativeTexture(nativeCelestial.Samplers[2], glowTextureId),
            });
        }
        NativeWorldEndPass(target, outer, outerFlags);
    }

    /// <summary>
    /// One particle pool's instanced draw: the native pass, or the seam's neutral body.
    ///
    /// Only the cube pool takes the native route. The quad pool draws into the Transparent
    /// target in the OIT stage, whose per-attachment weighted-blend state belongs to the OIT
    /// pass rather than to the particle system, and which this seam cannot state; the pipeline
    /// request names "particlescube", so a draw under any other program falls through to the
    /// neutral body on its own rather than by a separate test.
    /// </summary>
    public override void RenderParticles(MeshRef model, int quantity, int particleTextureId)
    {
        if (quantity <= 0)
        {
            base.RenderParticles(model, quantity, particleTextureId);
            return;
        }

        // Blending on in the standard mode (the caller's GlToggleBlend) and the Opaque stage's
        // depth test and depth writes, which ChunkRenderer.RenderOpaque established and no
        // renderer between it and the particles turns off again.
        if (!NativeWorldPrepare(nativeParticlesCube, model, blending: true, depth: true,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            base.RenderParticles(model, quantity, particleTextureId);
            return;
        }

        RuntimeStats.drawCallsCount++;
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        // Inside the caller's motion window the motion attachment is one of the pass's colour
        // slots (NativeWorldPassColorSlots) and replaces rather than blends (NativeWorldBlend), so
        // particlescube.fsh's motion.glsl writer lands exactly what it lands on the GL path.
        if (NativeWorldBeginPass("Particles", target, slots, Array.Empty<int>()))
        {
            device.DrawNativeMeshInstanced(pipeline, vao.VaoId, quantity, ReadOnlySpan<NativeTexture>.Empty);
        }
        NativeWorldEndPass(target, outer, outerFlags);
    }

    // ------------------------------------------------------------------- the decal scope

    /// <summary>True between <see cref="BeginDecalPass" /> and <see cref="EndDecalPass" />.</summary>
    private bool decalScopeActive;

    /// <summary>The decal atlas handle the open scope passed in.</summary>
    private int decalScopeDecalTextureId;

    /// <summary>The block atlas handle the open scope passed in.</summary>
    private int decalScopeBlockTextureId;

    /// <summary>
    /// Opens the decal pool's scope: the two atlas handles, which a native pass resolves what it
    /// samples from, instead of from the units ShaderProgramDecals' setters bound them to.
    ///
    /// The mesh handle is deliberately not a parameter. MeshDataPool.modelRef is internal in the
    /// vanilla API and the shipped VintagestoryAPI-patched.dll is vanilla plus api-patcher.cs's
    /// hooks only, so a new public member on MeshDataPool would never reach the running client
    /// (it did not, and the shipped client threw MissingMethodException on both backends). The
    /// lib therefore runs the vanilla MeshDataPool.Draw, whose own
    /// <c>capi.Render.RenderMesh(modelRef, starts, sizes, count)</c> lands in this platform's
    /// <see cref="RenderMesh(MeshRef, int[], int[], int, bool)" /> override, and that override
    /// routes to <see cref="TryDrawDecalPoolNative" /> while this scope is open. The caller's
    /// Draw runs the pool's cull first on both routes, so both draw the same ranges and only the
    /// draw command differs.
    ///
    /// Where the other side is: ClientPlatformAbstract.BeginDecalPass / EndDecalPass have empty
    /// neutral bodies, so the OpenGL path is vanilla MeshDataPool.Draw into
    /// ClientPlatformWindows.RenderMesh -> GL.MultiDrawElements, exactly as before the seam.
    /// Target and slots: Primary, inside SystemRenderDecals' motion window - a decal nudges the
    /// depth buffer in front of the block it sits on and writes that surface's motion vector
    /// itself, so the motion attachment is one of NativeWorldPassColorSlots and replaces rather
    /// than blends (NativeWorldBlend).
    /// State that is not obvious: standard blending on and the AfterOIT stage's depth test and
    /// depth writes, which ClientMain sets before the stage; SystemRenderDecals turns culling
    /// off itself.
    /// What pins it: NativeWorldSystemsTests (old route against native route, including the
    /// motion attachment bit for bit) and Optimum.Tests/native-world-systems-coverage-tests.cs.
    /// </summary>
    public override void BeginDecalPass(int decalTextureId, int blockTextureId)
    {
        decalScopeActive = true;
        decalScopeDecalTextureId = decalTextureId;
        decalScopeBlockTextureId = blockTextureId;
    }

    /// <summary>Closes the scope <see cref="BeginDecalPass" /> opened.</summary>
    public override void EndDecalPass()
    {
        decalScopeActive = false;
        decalScopeDecalTextureId = 0;
        decalScopeBlockTextureId = 0;
    }

    /// <summary>
    /// The decal pool's multi-draw, recorded natively, when it arrives through
    /// <see cref="RenderMesh(MeshRef, int[], int[], int, bool)" /> inside an open decal scope.
    /// False means the scope is closed, the native route is off, or the pass could not be
    /// prepared, and the caller takes the emulated multi-draw the OpenGL body takes.
    /// </summary>
    internal bool TryDrawDecalPoolNative(MeshRef decalMesh, int[] indicesStarts, int[] indicesSizes, int groupCount)
    {
        if (!decalScopeActive) return false;
        if (groupCount <= 0 || indicesStarts == null || indicesSizes == null) return false;

        if (!NativeWorldPrepare(nativeDecals, decalMesh, blending: true, depth: true,
                out FrameBufferRef target, out VAO vao, out uint slots, out NativePipeline pipeline))
        {
            return false;
        }

        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        if (NativeWorldBeginPass("Decals", target, slots,
                new[] { decalScopeDecalTextureId, decalScopeBlockTextureId }))
        {
            device.DrawNativeMeshMulti(pipeline, vao.VaoId, indicesStarts, indicesSizes, groupCount, new[]
            {
                new NativeTexture(nativeDecals.Samplers[0], decalScopeDecalTextureId),
                new NativeTexture(nativeDecals.Samplers[1], decalScopeBlockTextureId),
            });
        }
        NativeWorldEndPass(target, outer, outerFlags);
        return true;
    }
}
