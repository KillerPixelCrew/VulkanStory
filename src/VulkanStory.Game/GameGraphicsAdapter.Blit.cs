using VulkanStory.Render.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

// Retained final blit branches; baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeFullscreenPass nativeBlit = new("blit", [], ["scene"]);
    private readonly NativeFullscreenPass nativeFsrEasu = new("fsr-easu", ["inputTexelSize"], ["inputScene"]);
    private readonly NativeFullscreenPass nativeFsrRcas = new("fsr-rcas", ["inputTexelSize"], ["inputScene"]);
    private readonly NativeFullscreenPass nativeTaaDebug = new("taa-debug", ["mode", "renderSize"], ["motionTex", "depthTex", "sceneTex"]);
    private int fsrEasuProgram, fsrRcasProgram, taaDebugProgram;
    private bool fsrEasuFailed, fsrRcasFailed, taaDebugFailed, fsrDisabled;
    private string? lastBlitRefusal;

    /// <summary>Copies the completed scene through the selected reconstruction/sharpening path into the default presentation target.</summary>
    internal void BlitPrimaryToDefault()
    {
        RequireDevice();
        if (!GameFramebufferBindings.OffscreenEnabled(platform!)) return;
        var primary = FramebufferAt(EnumFrameBuffer.Primary);
        var display = RequireFramebufferHost().PixelSize();
        int scene = UpscaledThisFrame && NativePostTarget(platform!.FrameBuffers, UpscaledSceneIndex) is { } upscaled
            ? upscaled.ColorTextureIds[0] : primary.ColorTextureIds[0];
        var settings = postSettings ?? throw new InvalidOperationException("Blit settings are not attached.");
        int motion = FrameState.MotionAttachment;
        if (settings.Settings.TaaDebugView != 0 && motion >= 0 && primary.ColorTextureIds.Length > motion)
        {
            int debug = OwnedProgram("taa-debug", ref taaDebugProgram, ref taaDebugFailed);
            if (debug > 0 && DrawBlit(nativeTaaDebug, debug, NativeDefaultTarget, display.Width, display.Height,
                [primary.ColorTextureIds[motion], primary.DepthTextureId, scene], primary.Width, primary.Height, settings.Settings.TaaDebugView))
            { FinishBlit(false); return; }
        }
        var target = NativePostTarget(platform!.FrameBuffers, FsrFramebufferIndex);
        bool fsr = !UpscaledThisFrame && !settings.UpscalerReplacesTaa && !fsrDisabled &&
            settings.Settings.RenderScale < 1f && target != null;
        if (fsr)
        {
            int easu = OwnedProgram("fsr-easu", ref fsrEasuProgram, ref fsrEasuFailed);
            int rcas = OwnedProgram("fsr-rcas", ref fsrRcasProgram, ref fsrRcasFailed);
            try
            {
                // Both passes are presentation's scene path, like the ordinary blit below: a cold
                // pipeline compiles blocking once rather than refusing as "still compiling", which
                // would have disabled FSR for the whole session. A refusal left here is a real one.
                if (easu > 0 && rcas > 0 && DrawBlit(nativeFsrEasu, easu, target!.FboId, target.Width, target.Height,
                    [scene], primary.Width, primary.Height, requirePipeline: true) && DrawBlit(nativeFsrRcas, rcas, NativeDefaultTarget,
                    display.Width, display.Height, [target.ColorTextureIds[0]], target.Width, target.Height, requirePipeline: true))
                { FinishBlit(true); return; }
                fsrDisabled = true;
                platform!.Logger.Warning("VulkanStory: FSR blit unavailable; using the ordinary blit for this session.");
            }
            catch (Exception failure)
            {
                RequireDevice().EndNativePass(); fsrDisabled = true;
                platform!.Logger.Warning("VulkanStory: FSR blit unavailable: {0}", failure.Message);
            }
        }
        // Sharpen only after final composition and late overlays; successful FSR
        // supplies RCAS above and never receives a second sharpening pass.
        if (!UpscaledThisFrame) scene = RenderTaaSharpen(scene, fsr && !fsrDisabled);
        ShaderProgramBlit blit = ShaderPrograms.Blit;
        if (!NativeProgramUsable(blit))
            throw new InvalidOperationException("The final scene blit shader is unusable: program=" + blit?.ProgramId +
                ", loadError=" + blit?.LoadError + ", disposed=" + blit?.Disposed);
        if (!DrawBlit(nativeBlit, blit.ProgramId, NativeDefaultTarget,
            display.Width, display.Height, [scene], primary.Width, primary.Height, requirePipeline: true))
            throw new InvalidOperationException("The final scene blit did not draw: " + lastBlitRefusal +
                "; program=" + blit.ProgramId + ", scene=" + scene + ", input=" + primary.Width + "x" + primary.Height +
                ", display=" + display.Width + "x" + display.Height);
        FinishBlit(fsr);
    }
    private bool DrawBlit(NativeFullscreenPass pass, int program, int target, int width, int height,
        int[] inputs, int inputWidth, int inputHeight, int? debugMode = null, bool requirePipeline = false)
    {
        lastBlitRefusal = null;
        var renderer = RequireDevice();
        NativePipeline? pipeline = NativePipelineFor(pass, program, target);
        if (pipeline != null)
        {
            try
            {
                if (!BeginNativeBlitPass("Blit/" + target, target, width, height, inputs))
                { lastBlitRefusal = "begin pass: " + renderer.NativeDrawRefusal; return false; }
                if (debugMode.HasValue)
                {
                    renderer.WriteNative(pipeline, pass.Uniforms[0], debugMode.Value);
                    renderer.WriteNative(pipeline, pass.Uniforms[1], inputWidth, inputHeight);
                }
                else if (pass.Uniforms.Length > 0) renderer.WriteNative(pipeline, pass.Uniforms[0], 1f / inputWidth, 1f / inputHeight);
                var textures = new NativeTexture[inputs.Length];
                for (int index = 0; index < inputs.Length; index++) textures[index] = new NativeTexture(pass.Samplers[index], inputs[index]);
                bool drawn = renderer.DrawNativeFullscreen(pipeline, textures, requirePipeline);
                if (!drawn) lastBlitRefusal = "native fullscreen: " + renderer.NativeDrawRefusal;
                return drawn;
            }
            finally { renderer.EndNativePass(); }
        }
        bool blend = Stated.BlendEnabled, depth = Stated.DepthTest, cull = Stated.CullEnabled, scissor = Stated.ScissorEnabled;
        if (target == NativeDefaultTarget) LoadFramebuffer(EnumFrameBuffer.Default);
        else SetFramebuffer(FramebufferAt((EnumFrameBuffer)FsrFramebufferIndex), keepViewport: false);
        Viewport(width, height); Stated.SetBlendEnabled(false); Stated.DepthTest = Stated.CullEnabled = Stated.ScissorEnabled = false;
        StatedProgram = program;
        try
        {
            BindOwnedInputs(program, pass.SamplerNames, inputs);
            if (debugMode.HasValue)
            {
                renderer.SetUniform(program, OwnedUniform(program, "mode"), debugMode.Value);
                renderer.SetUniform(program, OwnedUniform(program, "renderSize"), (float)inputWidth, (float)inputHeight);
            }
            else if (pass.Uniforms.Length > 0)
                renderer.SetUniform(program, OwnedUniform(program, "inputTexelSize"), 1f / inputWidth, 1f / inputHeight);
            bool drawn = DrawOwnedFullscreen("Blit/" + target, 1u, requirePipeline);
            if (!drawn) lastBlitRefusal = "stated fallback: " + (lastStatedDrawRefusal ?? "no stated draw recorded");
            return drawn;
        }
        finally
        {
            StatedProgram = 0; Stated.SetBlendEnabled(blend); Stated.DepthTest = depth;
            Stated.CullEnabled = cull; Stated.ScissorEnabled = scissor;
        }
    }
    private void FinishBlit(bool restoreBlend)
    {
        LoadFramebuffer(EnumFrameBuffer.Default);
        if (restoreBlend) ToggleBlend(true, EnumBlendMode.Standard);
    }
    internal void ReloadBlitPrograms()
    {
        var renderer = RequireDevice();
        foreach (int program in new[] { fsrEasuProgram, fsrRcasProgram, taaDebugProgram }) if (program > 0) renderer.DeleteProgram(program);
        fsrEasuProgram = fsrRcasProgram = taaDebugProgram = 0;
        fsrEasuFailed = fsrRcasFailed = taaDebugFailed = false;
        nativeFsrEasu.Pipeline = nativeFsrRcas.Pipeline = nativeTaaDebug.Pipeline = null;
    }
}
