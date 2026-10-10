using VulkanStory.Render.Vulkan;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Native TAA stores individual samples and transports its finite window each frame.
internal sealed partial class GameGraphicsAdapter
{
    private static readonly string[] TaaWindowUniformNames =
        ["renderSize", "jitterPx", "prevJitterPx", "invViewProjJittered", "prevViewProj", "viewMatrix", "cameraDelta",
            "resetHistory", "stage"];
    private readonly NativeFullscreenPass nativeTaaCache = new("taa-cache", TaaWindowUniformNames,
        ["sceneTex", "glowTex", "depthTex", "motionTex", "historyDepth", "historyCount",
            "oldScene0", "oldGlow0", "oldScene1", "oldGlow1", "oldScene2", "oldGlow2",
            "checkScene0", "checkScene1", "checkScene2", "checkScene3"]);
    private readonly NativeFullscreenPass nativeTaaResolve = new("taa-resolve",
        TaaWindowUniformNames,
        ["sample0", "sample1", "sample2", "sample3", "sample4", "sample5", "sample6", "oldSample6",
            "depthTex", "motionTex", "historyDepth", "historyCount", "newCount"]);
    private readonly NativeFullscreenPass nativeTaaSharpen = new("taa-sharpen",
        ["inputTexelSize", "sharpness"], ["inputScene"]);
    private int taaCacheProgram, taaResolveProgram, taaSharpenProgram, taaParity;
    private bool taaCacheFailed, taaResolveFailed, taaSharpenFailed;
    internal string TaaReadiness { get; private set; } = "not resolved";
    private int taaResolvedColor, taaResolvedGlow;
    private readonly Dictionary<(int Program, string Name), int> ownedUniforms = new();
    internal object? TaaResolveCapture { get; private set; }
    internal int NextTaaReadIndex => (taaParity & 1) == 0 ? TaaHistoryIndexB : TaaHistoryIndexA;
    internal int ResolvedTaaWriteIndex => (taaParity & 1) == 0 ? TaaHistoryIndexB : TaaHistoryIndexA;
    internal FrameBufferRef[] TaaSampleReadTargets => taaSampleBanks is { } banks
        ? [banks[(taaParity ^ 1) & 1].Low, banks[(taaParity ^ 1) & 1].High] : [];
    // Like the public history index, this expression addresses the written bank after publication flips parity.
    internal FrameBufferRef[] TaaSampleWrittenTargets => TaaSampleReadTargets;

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
        if (!EnsureTaaSampleBanks(write.Width, write.Height)) return false;
        int cacheProgram = OwnedProgram("taa-cache", ref taaCacheProgram, ref taaCacheFailed);
        int program = OwnedProgram("taa-resolve", ref taaResolveProgram, ref taaResolveFailed);
        if (cacheProgram <= 0 || program <= 0)
        { TaaReadiness = "TAA sample transport or resolve shader unavailable"; InvalidateTaaSampleWindow(); return false; }
        TaaSampleBank newBank = taaSampleBanks![taaParity & 1];
        TaaSampleBank oldBank = taaSampleBanks![(taaParity ^ 1) & 1];
        TemporalFrameState frame = temporal.State;
        (float[]? inverse, float[] previous) = JitteredReprojection(frame, write.Width, write.Height);
        bool matchingWindow = frame.JitterPhaseCount == 8;
        bool reset = frame.Reset || !TaaHistoryValid || !matchingWindow ||
            !frame.WasViewCaptured(EnumTemporalView.World) || inverse == null;
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
                resetHistory = reset, windowSize = 8, jitterPhaseCount = frame.JitterPhaseCount,
                readFbo = read.FboId, writeFbo = write.FboId,
                readTextures = (int[])read.ColorTextureIds.Clone(), writeTextures = (int[])write.ColorTextureIds.Clone(),
                oldSamples = new { lowFbo = oldBank.Low.FboId, highFbo = oldBank.High.FboId,
                    scene = (int[])oldBank.SceneSamples.Clone(), glow = (int[])oldBank.GlowSamples.Clone(), count = oldBank.CountTexture },
                newSamples = new { lowFbo = newBank.Low.FboId, highFbo = newBank.High.FboId,
                    scene = (int[])newBank.SceneSamples.Clone(), glow = (int[])newBank.GlowSamples.Clone(), count = newBank.CountTexture }
            };
        bool drawn = DrawTaaSampleWindow(cacheProgram, program, primary, write, read,
            newBank, oldBank, frame, inverse, previous, reset);
        if (!drawn) { TaaReadiness = "TAA sample transport or resolve draw declined"; InvalidateTaaSampleWindow(); return false; }
        taaResolvedColor = write.ColorTextureIds[0]; taaResolvedGlow = write.ColorTextureIds[1];
        TaaHistoryValid = matchingWindow; taaParity ^= 1; TaaResolvedThisFrame = true;
        TaaReadiness = "ready";
        return true;
    }
    private bool DrawTaaSampleWindow(int cacheProgram, int resolveProgram,
        FrameBufferRef primary, FrameBufferRef write, FrameBufferRef read,
        TaaSampleBank newBank, TaaSampleBank oldBank,
        TemporalFrameState frame, float[] inverse, float[] previous, bool reset)
    {
        int[] lowInputs = CacheInputs(0);
        int[] highInputs = CacheInputs(1);
        int[] colorInputs = ResolveInputs(newBank.SceneSamples, oldBank.SceneSamples);
        int[] glowInputs = ResolveInputs(newBank.GlowSamples, oldBank.GlowSamples);
        try
        {
            return DrawTaaWindowStage(nativeTaaCache, cacheProgram, newBank.Low, 255u, lowInputs,
                       frame, inverse, previous, reset, 0) &&
                   DrawTaaWindowStage(nativeTaaCache, cacheProgram, newBank.High, 127u, highInputs,
                       frame, inverse, previous, reset, 1) &&
                   DrawTaaWindowStage(nativeTaaResolve, resolveProgram, write, 5u, colorInputs,
                       frame, inverse, previous, reset, 0) &&
                   DrawTaaWindowStage(nativeTaaResolve, resolveProgram, write, 2u, glowInputs,
                       frame, inverse, previous, reset, 1);
        }
        catch
        {
            InvalidateTaaSampleWindow();
            throw;
        }

        int[] CacheInputs(int stage)
        {
            int start = stage == 0 ? 0 : 3;
            int[] checks = stage == 0 ? [3, 4, 5, 6] : [0, 1, 2, 6];
            return [primary.ColorTextureIds[0], primary.ColorTextureIds[1], primary.DepthTextureId,
                primary.ColorTextureIds[FrameState.MotionAttachment], read.ColorTextureIds[2], oldBank.CountTexture,
                oldBank.SceneSamples[start], oldBank.GlowSamples[start],
                oldBank.SceneSamples[start + 1], oldBank.GlowSamples[start + 1],
                oldBank.SceneSamples[start + 2], oldBank.GlowSamples[start + 2],
                oldBank.SceneSamples[checks[0]], oldBank.SceneSamples[checks[1]],
                oldBank.SceneSamples[checks[2]], oldBank.SceneSamples[checks[3]]];
        }

        int[] ResolveInputs(int[] current, int[] old) =>
            [current[0], current[1], current[2], current[3], current[4], current[5], current[6], old[6],
                primary.DepthTextureId, primary.ColorTextureIds[FrameState.MotionAttachment],
                read.ColorTextureIds[2], oldBank.CountTexture, newBank.CountTexture];
    }

    private bool DrawTaaWindowStage(NativeFullscreenPass pass, int program, FrameBufferRef target,
        uint slots, int[] inputs, TemporalFrameState frame, float[] inverse, float[] previous, bool reset, int stage)
    {
        var renderer = RequireDevice();
        using VulkanDevice.GpuSection gpuSection = renderer.BeginGpuSection(
            pass.PassName == "taa-cache" ? (stage == 0 ? "taa_cache_low" : "taa_cache_high") :
            (stage == 0 ? "taa_resolve_color" : "taa_resolve_glow"));
        NativePipeline? pipeline = NativePostPipeline(pass, program, target.FboId, slots,
            NativeOpaqueBlend(slots), false, false, CompareOp.Less);
        if (pipeline == null) return StatedTaaWindowStage(pass, program, target, slots, inputs,
            frame, inverse, previous, reset, stage);
        bool drawn = false;
        try
        {
            if (BeginNativeTargetPass(pass.PassName + "/" + stage + "/" + target.FboId,
                    target.FboId, slots, target.Width, target.Height, inputs))
            {
                var u = pass.Uniforms;
                renderer.WriteNative(pipeline, u[0], target.Width, target.Height);
                renderer.WriteNative(pipeline, u[1], frame.JitterPx.X, frame.JitterPx.Y);
                renderer.WriteNative(pipeline, u[2], frame.PrevJitterPx.X, frame.PrevJitterPx.Y);
                WriteNativeMatrix(pipeline, u[3], inverse); WriteNativeMatrix(pipeline, u[4], previous);
                WriteNativeMatrix(pipeline, u[5], frame.CameraMatrixOrigin);
                renderer.WriteNative(pipeline, u[6], frame.CameraPosDelta.X, frame.CameraPosDelta.Y, frame.CameraPosDelta.Z);
                renderer.WriteNative(pipeline, u[7], reset ? 1 : 0);
                renderer.WriteNative(pipeline, u[8], stage);
                NativeTexture[] textures = inputs.Select((texture, index) => new NativeTexture(pass.Samplers[index], texture)).ToArray();
                drawn = renderer.DrawNativeFullscreen(pipeline, textures);
            }
        }
        finally { renderer.EndNativePass(); FinishTaaPass(); }
        return drawn;
    }
    private bool StatedTaaWindowStage(NativeFullscreenPass pass, int program, FrameBufferRef target,
        uint slots, int[] inputs, TemporalFrameState frame,
        float[] inverse, float[] previous, bool reset, int stage)
    {
        var renderer = RequireDevice();
        ToggleBlend(false, EnumBlendMode.Standard); Stated.DepthTest = false;
        SetFramebuffer(target, keepViewport: false); StatedProgram = program;
        try
        {
            BindOwnedInputs(program, pass.SamplerNames, inputs);
            renderer.SetUniform(program, OwnedUniform(program, "renderSize"), (float)target.Width, (float)target.Height);
            renderer.SetUniform(program, OwnedUniform(program, "jitterPx"), frame.JitterPx.X, frame.JitterPx.Y);
            renderer.SetUniform(program, OwnedUniform(program, "prevJitterPx"), frame.PrevJitterPx.X, frame.PrevJitterPx.Y);
            renderer.SetUniformMatrices(program, OwnedUniform(program, "invViewProjJittered"), 1, inverse);
            renderer.SetUniformMatrices(program, OwnedUniform(program, "prevViewProj"), 1, previous);
            renderer.SetUniformMatrices(program, OwnedUniform(program, "viewMatrix"), 1, frame.CameraMatrixOrigin);
            renderer.SetUniform(program, OwnedUniform(program, "cameraDelta"), frame.CameraPosDelta.X, frame.CameraPosDelta.Y, frame.CameraPosDelta.Z);
            renderer.SetUniform(program, OwnedUniform(program, "resetHistory"), reset ? 1 : 0);
            renderer.SetUniform(program, OwnedUniform(program, "stage"), stage);
            return DrawOwnedFullscreen(pass.PassName + "/" + stage + "/" + target.FboId,
                slots, requirePipeline: true, reads: inputs);
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
    private bool DrawOwnedFullscreen(string name, uint slots, bool requirePipeline = false, int[]? reads = null)
    {
        PassDeclaration? outer = StatedPass;
        StatedPass = new PassDeclaration { Name = name, FramebufferId = CurrentTargetId, ColorSlots = slots,
            Reads = reads ?? [], Flags = PassFlags.None };
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
        if (taaCacheProgram > 0) renderer.DeleteProgram(taaCacheProgram);
        if (taaResolveProgram > 0) renderer.DeleteProgram(taaResolveProgram);
        if (taaSharpenProgram > 0) renderer.DeleteProgram(taaSharpenProgram);
        taaCacheProgram = taaResolveProgram = taaSharpenProgram = 0;
        taaCacheFailed = taaResolveFailed = taaSharpenFailed = false;
        nativeTaaCache.Pipeline = nativeTaaResolve.Pipeline = nativeTaaSharpen.Pipeline = null;
        ownedUniforms.Clear(); taaParity = 0; taaResolvedColor = taaResolvedGlow = 0;
        InvalidateTaaSampleWindow();
    }
}
