using VulkanStory.Render.Vulkan;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Retained ClientPlatformWindows resolve/sharpen decisions and native draw seams.
// Baseline 386e0d05386d0b228b439d09aeca851428f7bbf3; TAA GLSL is copied unchanged.
internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeFullscreenPass nativeTaaResolve = new("taa-resolve",
        ["renderSize", "jitterPx", "prevJitterPx", "invViewProjJittered", "prevViewProj", "viewMatrix", "cameraDelta",
            "resetHistory", "blendAlpha", "varianceGamma"],
        ["sceneTex", "glowTex", "motionTex", "depthTex", "historyColor", "historyGlow", "historyDepth"]);
    private readonly NativeFullscreenPass nativeTaaSharpen = new("taa-sharpen",
        ["inputTexelSize", "sharpness"], ["inputScene"]);
    private int taaResolveProgram, taaSharpenProgram, taaParity;
    private bool taaResolveFailed, taaSharpenFailed;
    internal string TaaReadiness { get; private set; } = "not resolved";
    private int taaResolvedColor, taaResolvedGlow;
    private readonly Dictionary<(int Program, string Name), int> ownedUniforms = new();
    internal object? TaaResolveCapture { get; private set; }
    internal int NextTaaReadIndex => (taaParity & 1) == 0 ? TaaHistoryIndexB : TaaHistoryIndexA;
    internal int ResolvedTaaWriteIndex => (taaParity & 1) == 0 ? TaaHistoryIndexB : TaaHistoryIndexA;

    private int OwnedUniform(int program, string name)
    {
        if (!ownedUniforms.TryGetValue((program, name), out int location))
            ownedUniforms.Add((program, name), location = RequireDevice().GetUniformLocation(program, name));
        return location;
    }
    private static AttachmentBlend[] NativeOpaqueBlend(uint slots)
    {
        int count = 0;
        for (int index = 0; index < RenderLimits.MaxColorAttachments; index++)
            if (((slots >> index) & 1) != 0) count = index + 1;
        var result = new AttachmentBlend[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = AttachmentBlend.Default;
            if (((slots >> index) & 1) == 0) result[index].WriteMask = 0;
        }
        return result;
    }
    private bool BeginNativeTargetPass(string name, int framebuffer, uint slots, int width, int height, int[] reads) =>
        RequireDevice().BeginNativePass(new NativePassDescription
        {
            Name = name, FramebufferId = framebuffer, ColorSlots = slots, Reads = reads,
            Flags = PassFlags.None, ViewportWidth = width, ViewportHeight = height,
        });
    private void WriteNativeMatrix(NativePipeline pipeline, NativeUniform uniform, float[] values) =>
        RequireDevice().WriteNative(pipeline, uniform, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(values)));

    /// <summary>
    /// The world reprojection the TAA resolve and the sky motion pass both state, built one way so
    /// they agree exactly: this frame's world projection jittered for a
    /// <paramref name="width" /> x <paramref name="height" /> target, times the origin camera matrix,
    /// inverted; and the previous frame's unjittered projection times its camera matrix.
    /// </summary>
    /// <returns>The inverse jittered view-projection (null when singular; each caller keeps its own fallback) and the previous view-projection.</returns>
    private static (float[]? Inverse, float[] Previous) JitteredReprojection(TemporalFrameState frame, int width, int height)
    {
        float[] projectionJittered = frame.CopyRasterProjection(EnumTemporalView.World, width, height);
        float[] viewProj = Mat4f.Mul(new float[16], projectionJittered, frame.CameraMatrixOrigin);
        float[]? inverse = Mat4f.Invert(new float[16], viewProj);
        float[] previous = Mat4f.Mul(new float[16], frame.GetPrevProjection(EnumTemporalView.World), frame.PrevCameraMatrixOrigin);
        return (inverse, previous);
    }

    /// <summary>Resolves scene color into the current history target when temporal inputs and target readiness permit, then records the resolved output.</summary>
    /// <param name="temporal">Matching camera/history owner for this real frame.</param>
    /// <returns>True when this frame resolved to native TAA history; false when readiness or pass setup decline.</returns>
    internal bool RenderTaaResolve(GameTemporalOwner temporal)
    {
        RequireDevice();
        TaaResolvedThisFrame = false;
        TaaResolveCapture = null;
        var settings = postSettings ?? throw new InvalidOperationException("Post settings are not attached.");
        var snapshot = temporal.Snapshot();
        string? refusal = !settings.EffectiveTaa ? settings.TaaDisabled ? "TAA disabled for this session" : "TAA not selected" :
            !TaaTargetsReady ? "TAA targets unavailable" :
            snapshot.FrameId != device!.LatencyFrameId ? "temporal frame identity mismatch" :
            !snapshot.WorldCaptured ? "world camera not captured" :
            !snapshot.MotionValid ? temporal.MotionReadiness : null;
        if (refusal != null)
        { TaaReadiness = refusal; TaaHistoryValid = false; return false; }
        var buffers = platform!.FrameBuffers;
        FrameBufferRef? primary = NativePostTarget(buffers, PrimaryIndex);
        FrameBufferRef? write = NativePostTarget(buffers, (taaParity & 1) == 0 ? TaaHistoryIndexA : TaaHistoryIndexB);
        FrameBufferRef? read = NativePostTarget(buffers, (taaParity & 1) == 0 ? TaaHistoryIndexB : TaaHistoryIndexA);
        int motion = FrameState.MotionAttachment;
        if (primary?.ColorTextureIds is not { Length: >= 2 } || motion < 0 || primary.ColorTextureIds.Length <= motion ||
            primary.DepthTextureId <= 0 || write?.ColorTextureIds is not { Length: >= 3 } || read?.ColorTextureIds is not { Length: >= 3 })
        { TaaReadiness = "TAA colour, depth, motion or history target unavailable"; TaaHistoryValid = false; return false; }
        int program = OwnedProgram("taa-resolve", ref taaResolveProgram, ref taaResolveFailed);
        if (program <= 0) { TaaReadiness = "TAA resolve shader unavailable"; TaaHistoryValid = false; return false; }
        TemporalFrameState frame = temporal.State;
        (float[]? inverse, float[] previous) = JitteredReprojection(frame, write.Width, write.Height);
        bool reset = frame.Reset || !TaaHistoryValid || !frame.WasViewCaptured(EnumTemporalView.World) || inverse == null;
        inverse ??= Mat4f.Identity(new float[16]);
        if (Environment.GetEnvironmentVariable("VULKANSTORY_NATIVE_TAA_INPUTS") == "1")
            TaaResolveCapture = new
            {
                frameId = frame.RenderedFrameId, width = write.Width, height = write.Height,
                currentJitter = new[] { frame.JitterPx.X, frame.JitterPx.Y },
                previousJitter = new[] { frame.PrevJitterPx.X, frame.PrevJitterPx.Y },
                inverseViewProjectionJittered = inverse,
                previousViewProjection = previous,
                viewMatrix = (float[])frame.CameraMatrixOrigin.Clone(),
                currentProjection = (float[])frame.GetProjection(EnumTemporalView.World).Clone(),
                previousProjection = (float[])frame.GetPrevProjection(EnumTemporalView.World).Clone(),
                cameraDelta = new[] { frame.CameraPosDelta.X, frame.CameraPosDelta.Y, frame.CameraPosDelta.Z },
                resetHistory = reset, blendAlpha = .1f, varianceGamma = 1.25f,
                readFbo = read.FboId, writeFbo = write.FboId,
                readTextures = (int[])read.ColorTextureIds.Clone(), writeTextures = (int[])write.ColorTextureIds.Clone()
            };
        bool drawn = DrawTaaResolve(program, primary, write, read, frame, inverse, previous, reset);
        if (!drawn) { TaaReadiness = "TAA resolve draw declined"; TaaHistoryValid = false; return false; }
        taaResolvedColor = write.ColorTextureIds[0]; taaResolvedGlow = write.ColorTextureIds[1];
        TaaHistoryValid = true; taaParity ^= 1; TaaResolvedThisFrame = true;
        TaaReadiness = "ready";
        return true;
    }
    private bool DrawTaaResolve(int program, FrameBufferRef primary, FrameBufferRef write, FrameBufferRef read,
        TemporalFrameState frame, float[] inverse, float[] previous, bool reset)
    {
        var renderer = RequireDevice();
        int[] inputs = [primary.ColorTextureIds[0], primary.ColorTextureIds[1], primary.ColorTextureIds[FrameState.MotionAttachment],
            primary.DepthTextureId, read.ColorTextureIds[0], read.ColorTextureIds[1], read.ColorTextureIds[2]];
        const uint slots = 7u;
        NativePipeline? pipeline = NativePostPipeline(nativeTaaResolve, program, write.FboId, slots,
            NativeOpaqueBlend(slots), false, false, CompareOp.Less);
        if (pipeline == null) return StatedTaaResolve(program, write, inputs, frame, inverse, previous, reset);
        bool drawn = false;
        try
        {
            if (BeginNativeTargetPass("TaaResolve/" + write.FboId, write.FboId, slots, write.Width, write.Height, inputs))
            {
                var u = nativeTaaResolve.Uniforms;
                renderer.WriteNative(pipeline, u[0], write.Width, write.Height);
                renderer.WriteNative(pipeline, u[1], frame.JitterPx.X, frame.JitterPx.Y);
                renderer.WriteNative(pipeline, u[2], frame.PrevJitterPx.X, frame.PrevJitterPx.Y);
                WriteNativeMatrix(pipeline, u[3], inverse); WriteNativeMatrix(pipeline, u[4], previous);
                WriteNativeMatrix(pipeline, u[5], frame.CameraMatrixOrigin);
                renderer.WriteNative(pipeline, u[6], frame.CameraPosDelta.X, frame.CameraPosDelta.Y, frame.CameraPosDelta.Z);
                renderer.WriteNative(pipeline, u[7], reset ? 1 : 0);
                renderer.WriteNative(pipeline, u[8], .1f); renderer.WriteNative(pipeline, u[9], 1.25f);
                NativeTexture[] textures = inputs.Select((texture, index) => new NativeTexture(nativeTaaResolve.Samplers[index], texture)).ToArray();
                drawn = renderer.DrawNativeFullscreen(pipeline, textures);
            }
        }
        finally { renderer.EndNativePass(); FinishTaaPass(); }
        return drawn;
    }
    private bool StatedTaaResolve(int program, FrameBufferRef target, int[] inputs, TemporalFrameState frame,
        float[] inverse, float[] previous, bool reset)
    {
        var renderer = RequireDevice();
        ToggleBlend(false, EnumBlendMode.Standard); Stated.DepthTest = false;
        SetFramebuffer(target, keepViewport: false); StatedProgram = program;
        try
        {
            BindOwnedInputs(program, nativeTaaResolve.SamplerNames, inputs);
            renderer.SetUniform(program, OwnedUniform(program, "renderSize"), (float)target.Width, (float)target.Height);
            renderer.SetUniform(program, OwnedUniform(program, "jitterPx"), frame.JitterPx.X, frame.JitterPx.Y);
            renderer.SetUniform(program, OwnedUniform(program, "prevJitterPx"), frame.PrevJitterPx.X, frame.PrevJitterPx.Y);
            renderer.SetUniformMatrices(program, OwnedUniform(program, "invViewProjJittered"), 1, inverse);
            renderer.SetUniformMatrices(program, OwnedUniform(program, "prevViewProj"), 1, previous);
            renderer.SetUniformMatrices(program, OwnedUniform(program, "viewMatrix"), 1, frame.CameraMatrixOrigin);
            renderer.SetUniform(program, OwnedUniform(program, "cameraDelta"), frame.CameraPosDelta.X, frame.CameraPosDelta.Y, frame.CameraPosDelta.Z);
            renderer.SetUniform(program, OwnedUniform(program, "resetHistory"), reset ? 1 : 0);
            renderer.SetUniform(program, OwnedUniform(program, "blendAlpha"), .1f);
            renderer.SetUniform(program, OwnedUniform(program, "varianceGamma"), 1.25f);
            return DrawOwnedFullscreen("TaaResolve/" + target.FboId, 7u);
        }
        finally { StatedProgram = 0; FinishTaaPass(); }
    }
    private void BindOwnedInputs(int program, string[] names, int[] textures)
    {
        var renderer = RequireDevice();
        for (int index = 0; index < names.Length; index++)
        {
            renderer.SetSamplerUnit(program, names[index], index);
            Stated.BindTexture(index, textures[index]); Stated.BindSampler(index, 0);
        }
    }
    private bool DrawOwnedFullscreen(string name, uint slots, bool requirePipeline = false)
    {
        PassDeclaration? outer = StatedPass;
        StatedPass = new PassDeclaration { Name = name, FramebufferId = CurrentTargetId, ColorSlots = slots, Flags = PassFlags.None };
        try { return RecordStatedDraw(null, 1, null, null, 0, requirePipeline); }
        finally { StatedPass = outer; }
    }
    private void FinishTaaPass()
    {
        ToggleBlend(true, EnumBlendMode.Standard); Stated.DepthTest = true;
        LoadFramebuffer(EnumFrameBuffer.Primary);
    }
    internal int RenderTaaSharpen(int scene, bool fsrActive)
    {
        var renderer = RequireDevice();
        float sharpness = (postSettings ?? throw new InvalidOperationException("Post settings are not attached.")).Settings.TaaSharpness;
        if (!TaaResolvedThisFrame || sharpness <= 0f || fsrActive) return scene;
        FrameBufferRef? target = NativePostTarget(platform!.FrameBuffers, TaaSharpenIndex);
        if (target == null) return scene;
        int program = OwnedProgram("taa-sharpen", ref taaSharpenProgram, ref taaSharpenFailed);
        if (program <= 0) return scene;
        NativePipeline? pipeline = NativePostPipeline(nativeTaaSharpen, program, target.FboId, 1u,
            NativeOpaqueBlend(1u), false, false, CompareOp.Less);
        bool drawn = false;
        if (pipeline == null)
        {
            ToggleBlend(false, EnumBlendMode.Standard); Stated.DepthTest = false;
            SetFramebuffer(target, keepViewport: false); StatedProgram = program;
            try
            {
                BindOwnedInputs(program, nativeTaaSharpen.SamplerNames, [scene]);
                renderer.SetUniform(program, OwnedUniform(program, "inputTexelSize"), 1f / target.Width, 1f / target.Height);
                renderer.SetUniform(program, OwnedUniform(program, "sharpness"), Math.Clamp(sharpness, 0f, 1f));
                drawn = DrawOwnedFullscreen("TaaSharpen/" + target.FboId, 1u);
            }
            finally { StatedProgram = 0; FinishTaaPass(); }
        }
        else
        {
            try
            {
                if (BeginNativeTargetPass("TaaSharpen/" + target.FboId, target.FboId, 1u, target.Width, target.Height, [scene]))
                {
                    renderer.WriteNative(pipeline, nativeTaaSharpen.Uniforms[0], 1f / target.Width, 1f / target.Height);
                    renderer.WriteNative(pipeline, nativeTaaSharpen.Uniforms[1], Math.Clamp(sharpness, 0f, 1f));
                    drawn = renderer.DrawNativeFullscreen(pipeline, [new NativeTexture(nativeTaaSharpen.Samplers[0], scene)]);
                }
            }
            finally { renderer.EndNativePass(); FinishTaaPass(); }
        }
        return drawn ? target.ColorTextureIds[0] : scene;
    }
    internal int PostSceneTexture()
    {
        RequireDevice();
        if (UpscaledThisFrame && NativePostTarget(platform!.FrameBuffers, UpscaledSceneIndex) is { } upscaled)
            return upscaled.ColorTextureIds[0];
        return TaaResolvedThisFrame ? taaResolvedColor : FramebufferAt(EnumFrameBuffer.Primary).ColorTextureIds[0];
    }
    internal int PostGlowTexture()
    {
        RequireDevice();
        return TaaResolvedThisFrame ? taaResolvedGlow : FramebufferAt(EnumFrameBuffer.Primary).ColorTextureIds[1];
    }
    /// <summary>Invalidates retained native temporal program caches after shader reload.</summary>
    internal void ReloadTemporalPrograms()
    {
        var renderer = RequireDevice();
        if (taaResolveProgram > 0) renderer.DeleteProgram(taaResolveProgram);
        if (taaSharpenProgram > 0) renderer.DeleteProgram(taaSharpenProgram);
        taaResolveProgram = taaSharpenProgram = 0; taaResolveFailed = taaSharpenFailed = false;
        nativeTaaResolve.Pipeline = nativeTaaSharpen.Pipeline = null;
        ownedUniforms.Clear(); taaParity = 0; taaResolvedColor = taaResolvedGlow = 0;
        TaaResolvedThisFrame = TaaHistoryValid = false;
    }
}
