using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Retained NativePostFinal composition: only scene color is written; glow is
// sampled outside the attachment scope. Baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
internal sealed partial class GameGraphicsAdapter
{
    private readonly NativeFullscreenPass nativeFinal = new("final",
        ["ambientBloomLevel", "optimumSsaoInScene", "optimumAoDebug", "invFrameSizeIn", "gammaLevel", "extraGamma",
            "contrastLevel", "brightnessLevel", "sepiaLevel", "windWaveCounter", "glitchEffectStrength",
            "sunPosScreenIn", "sunPos3dIn", "playerViewVector", "damageVignetting", "damageVignettingSide", "frostVignetting"],
        ["primaryScene", "glowParts", "bloomParts", "godrayParts", "ssaoScene"]);
    /// <summary>Composes scene post outputs into the presentation target and records whether the upscaled composite is ready.</summary>
    internal void RenderFinalComposition()
    {
        var renderer = RequireDevice();
        if (!GameFramebufferBindings.OffscreenEnabled(platform!)) return;
        var buffers = platform!.FrameBuffers;
        var primary = NativePostTarget(buffers, PrimaryIndex) ?? throw new InvalidOperationException("Final primary target is missing.");
        var composite = UpscaledThisFrame ? NativePostTarget(buffers, UpscaledSceneIndex) ?? primary : primary;
        var luma = NativePostTarget(buffers, LumaIndex) ?? throw new InvalidOperationException("Final luma target is missing.");
        var bloom = NativePostTarget(buffers, BlurVerticalLowResIndex) ?? throw new InvalidOperationException("Final bloom target is missing.");
        var rays = NativePostTarget(buffers, GodRaysIndex) ?? throw new InvalidOperationException("Final god-ray target is missing.");
        ShaderProgramFinal final = ShaderPrograms.Final;
        if (!NativeProgramUsable(final)) throw new InvalidOperationException("Final shader is not loaded.");
        bool ssao = GameFrameBindings.RenderSsao(platform);
        bool debug = AoSettings.Settings.AmbientOcclusionDebugView && ssao;
        int ao = ssao ? debug && AmbientOcclusionTexture != 0 ? AmbientOcclusionTexture :
            NativePostTarget(buffers, NativeSsaoBlurVerticalIndex)?.ColorTextureIds[0] ?? 0 : 0;
        int[] inputs = [luma.ColorTextureIds[0], PostGlowTexture(), bloom.ColorTextureIds[0], rays.ColorTextureIds[0], ao];
        uint slots = UpscaledThisFrame ? 1u : ~(1u << 1);
        var reads = new List<int> { inputs[0], inputs[1], inputs[2], inputs[3] };
        if (ao > 0 && !reads.Contains(ao)) reads.Add(ao);
        SetFramebuffer(composite, keepViewport: !UpscaledThisFrame);
        Stated.SetDrawBuffers(composite.FboId, 1u); Stated.DepthTest = false;
        ToggleBlend(true, Vintagestory.API.Client.EnumBlendMode.Standard);
        NativePipeline? pipeline = NativePostPipeline(nativeFinal, final, composite.FboId, slots,
            NativeFinalBlend(slots), false, false, CompareOp.Less);
        bool drawn = false;
        try
        {
            if (pipeline == null)
            {
                StatedProgram = final.ProgramId;
                BindOwnedInputs(final.ProgramId, nativeFinal.SamplerNames, inputs);
                WriteFinalUniforms(null, final.ProgramId, composite.Width, composite.Height, ssao, debug);
                drawn = DrawOwnedFullscreen("FinalComposition/0", slots, requirePipeline: true);
                StatedProgram = 0;
            }
            else if (renderer.BeginNativePass(new NativePassDescription
            {
                Name = "FinalComposition/0", FramebufferId = composite.FboId, ColorSlots = slots,
                Reads = reads.ToArray(), Flags = PassFlags.None,
            }))
            {
                WriteFinalUniforms(pipeline, final.ProgramId, composite.Width, composite.Height, ssao, debug);
                var textures = new NativeTexture[inputs.Length];
                for (int index = 0; index < inputs.Length; index++) textures[index] = new NativeTexture(nativeFinal.Samplers[index], inputs[index]);
                drawn = renderer.DrawNativeFullscreen(pipeline, textures, requirePipeline: true);
            }
        }
        finally
        {
            renderer.EndNativePass();
            if (pipeline == null) StatedProgram = 0;
            Stated.SetDrawBuffers(composite.FboId, ssao ? 15u : 3u);
        }
        UpscaledCompositeReady = UpscaledThisFrame && drawn;
        if (!drawn) throw new InvalidOperationException("Required final composition did not draw: " +
            (pipeline == null ? lastStatedDrawRefusal : renderer.NativeDrawRefusal));
    }
    private static AttachmentBlend[] NativeFinalBlend(uint slots)
    {
        AttachmentBlend[] blend = NativeOpaqueBlend(slots);
        if (blend.Length > 0) blend[0].Enabled = true;
        for (int index = 1; index < blend.Length; index++) blend[index].WriteMask = 0;
        return blend;
    }
    private void WriteFinalUniforms(NativePipeline? pipeline, int program, int width, int height, bool ssao, bool debug)
    {
        var renderer = RequireDevice();
        void Scalar(int index, float value)
        {
            if (pipeline != null) renderer.WriteNative(pipeline, nativeFinal.Uniforms[index], value);
            else renderer.SetUniform(program, OwnedUniform(program, nativeFinal.UniformNames[index]), value);
        }
        void Integer(int index, int value)
        {
            if (pipeline != null) renderer.WriteNative(pipeline, nativeFinal.Uniforms[index], value);
            else renderer.SetUniform(program, OwnedUniform(program, nativeFinal.UniformNames[index]), value);
        }
        void Vector(int index, Vec3f value)
        {
            if (pipeline != null) WriteNativeVec3(pipeline, nativeFinal.Uniforms[index], value);
            else renderer.SetUniform(program, OwnedUniform(program, nativeFinal.UniformNames[index]), value.X, value.Y, value.Z);
        }
        Scalar(0, NativeAmbientBloomLevel());
        Integer(1, AmbientOcclusionInScene || !ssao ? 1 : 0);
        Integer(2, debug ? AmbientOcclusionInScene ? 2 : 1 : 0);
        if (pipeline != null) renderer.WriteNative(pipeline, nativeFinal.Uniforms[3], 1f / width, 1f / height);
        else renderer.SetUniform(program, OwnedUniform(program, "invFrameSizeIn"), 1f / width, 1f / height);
        Scalar(4, ClientSettings.GammaLevel); Scalar(5, ClientSettings.ExtraGammaLevel);
        Scalar(6, PostUniforms.ExtraContrastLevel);
        Scalar(7, ClientSettings.BrightnessLevel + Math.Max(0f, PostUniforms.DropShadowIntensity * 2f - 1.66f) / 3f);
        Scalar(8, PostUniforms.SepiaLevel + PostUniforms.ExtraSepia);
        Scalar(9, PostUniforms.WindWaveCounter); Scalar(10, PostUniforms.GlitchStrength);
        if (GameFrameBindings.RenderGodRays(platform!))
        { Vector(11, PostUniforms.SunPositionScreen); Vector(12, PostUniforms.SunPosition3D); Vector(13, PostUniforms.PlayerViewVector); }
        Scalar(14, PostUniforms.DamageVignetting); Scalar(15, PostUniforms.DamageVignettingSide); Scalar(16, PostUniforms.FrostVignetting);
    }
}
