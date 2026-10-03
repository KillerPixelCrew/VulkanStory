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
        ["renderSize", "jitterPx", "invViewProjJittered", "prevViewProj", "viewMatrix", "cameraDelta",
            "resetHistory", "blendAlpha", "varianceGamma"],
        ["sceneTex", "glowTex", "motionTex", "depthTex", "historyColor", "historyGlow", "historyDepth"]);
    private readonly NativeFullscreenPass nativeTaaSharpen = new("taa-sharpen",
        ["inputTexelSize", "sharpness"], ["inputScene"]);
    private int taaResolveProgram, taaSharpenProgram, taaParity;
    private bool taaResolveFailed, taaSharpenFailed;
    private int taaResolvedColor, taaResolvedGlow;
    private readonly Dictionary<(int Program, string Name), int> ownedUniforms = new();

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

    internal bool RenderTaaResolve(GameTemporalOwner temporal)
    {
        RequireDevice();
        TaaResolvedThisFrame = false;
        var settings = postSettings ?? throw new InvalidOperationException("Post settings are not attached.");
        var snapshot = temporal.Snapshot();
        if (!TaaTargetsReady || !settings.EffectiveTaa || !snapshot.MotionValid ||
            snapshot.FrameId != device!.LatencyFrameId)
        { TaaHistoryValid = false; return false; }
        var buffers = platform!.FrameBuffers;
        FrameBufferRef? primary = NativePostTarget(buffers, PrimaryIndex);
        FrameBufferRef? write = NativePostTarget(buffers, (taaParity & 1) == 0 ? TaaHistoryIndexA : TaaHistoryIndexB);
        FrameBufferRef? read = NativePostTarget(buffers, (taaParity & 1) == 0 ? TaaHistoryIndexB : TaaHistoryIndexA);
        int motion = FrameState.MotionAttachment;
        if (primary?.ColorTextureIds is not { Length: >= 2 } || motion < 0 || primary.ColorTextureIds.Length <= motion ||
            primary.DepthTextureId <= 0 || write?.ColorTextureIds is not { Length: >= 3 } || read?.ColorTextureIds is not { Length: >= 3 })
        { TaaHistoryValid = false; return false; }
        int program = OwnedProgram("taa-resolve", ref taaResolveProgram, ref taaResolveFailed);
        if (program <= 0) { TaaHistoryValid = false; return false; }
        TemporalFrameState frame = temporal.State;
        float[] projection = frame.GetProjection(EnumTemporalView.World);
        var jittered = new double[16];
        for (int index = 0; index < 16; index++) jittered[index] = projection[index];
        TemporalMath.ApplyProjectionJitter(jittered, frame.JitterPx.X, frame.JitterPx.Y, write.Width, write.Height);
        var projectionJittered = new float[16];
        for (int index = 0; index < 16; index++) projectionJittered[index] = (float)jittered[index];
        float[] viewProj = Mat4f.Mul(new float[16], projectionJittered, frame.CameraMatrixOrigin);
        float[] inverse = Mat4f.Invert(new float[16], viewProj);
        float[] previous = Mat4f.Mul(new float[16], frame.GetPrevProjection(EnumTemporalView.World), frame.PrevCameraMatrixOrigin);
        bool reset = frame.Reset || !TaaHistoryValid || !frame.WasViewCaptured(EnumTemporalView.World) || inverse == null;
        inverse ??= Mat4f.Identity(new float[16]);
        bool drawn = DrawTaaResolve(program, primary, write, read, frame, inverse, previous, reset);
        if (!drawn) { TaaHistoryValid = false; return false; }
        taaResolvedColor = write.ColorTextureIds[0]; taaResolvedGlow = write.ColorTextureIds[1];
        TaaHistoryValid = true; taaParity ^= 1; TaaResolvedThisFrame = true;
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
                WriteNativeMatrix(pipeline, u[2], inverse); WriteNativeMatrix(pipeline, u[3], previous);
                WriteNativeMatrix(pipeline, u[4], frame.CameraMatrixOrigin);
                renderer.WriteNative(pipeline, u[5], frame.CameraPosDelta.X, frame.CameraPosDelta.Y, frame.CameraPosDelta.Z);
                renderer.WriteNative(pipeline, u[6], reset ? 1 : 0);
                renderer.WriteNative(pipeline, u[7], .1f); renderer.WriteNative(pipeline, u[8], 1.25f);
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
