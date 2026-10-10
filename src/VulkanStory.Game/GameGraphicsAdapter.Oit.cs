using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

// Retained OIT resource/accumulation/merge bodies; no injected static IDs.
// Baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
internal sealed partial class GameGraphicsAdapter
{
    private int oitReveal, oitAccumulation;
    private FrameBufferRef? oitTransparent;
    private bool oitDisabled;
    private ICoreClientAPI? oitApi;
    private readonly NativeFullscreenPass nativeOitMerge = new("transparentcompose", [],
        ["accumulation", "revealage", "inGlow", "OITreveal", "OITaccumulation"]);
    /// <summary>Allocates or selects weighted-transparency targets for the current original client API and scene size.</summary>
    /// <param name="api">Original client API used to bind and draw the transparency path.</param>
    internal void BeginOit(ICoreClientAPI api)
    {
        var renderer = RequireDevice();
        if (oitDisabled) return;
        IShaderProgram? previous = null;
        try
        {
            FrameBufferRef transparent = FramebufferAt(EnumFrameBuffer.Transparent);
            IShaderProgram program = api.Shader.GetProgram(9);
            IShaderProgram cloud = api.Shader.GetProgramByName("cloudvolumetric");
            if (program == null || program.Disposed || cloud == null || cloud.Disposed)
                throw new InvalidOperationException("OIT shaders are unavailable.");
            previous = api.Render.CurrentActiveShader;
            if (previous is { Disposed: false }) previous.Stop();
            program.Use(); program.Uniform("OITreveal", 6);
            renderer.SetSamplerUnit(program.ProgramId, "OITaccumulation", 7); program.Stop();
            cloud.Use(); cloud.Uniform("liquidDepth", 4); cloud.Stop();
            if (previous is { Disposed: false }) previous.Use();
            if (oitReveal == 0 || !ReferenceEquals(oitTransparent, transparent)) RebuildOit(transparent);
            Stated.SetDrawBuffers(transparent.FboId, 63u);
            foreach (int slot in new[] { 0, 1 })
                Stated.SetSlotBlend(slot, 32774, 774, 0, 774, 0);
            foreach (int slot in new[] { 3, 4, 5 })
                Stated.SetSlotBlend(slot, 32774, 1, 1, 1, 1);
            renderer.ClearNativeColor(transparent.FboId, 0, 1f, 1f, 1f, 1f);
            renderer.ClearNativeColor(transparent.FboId, 1, 1f, 1f, 1f, 1f);
            foreach (int slot in new[] { 3, 4, 5 }) renderer.ClearNativeColor(transparent.FboId, slot, 0f, 0f, 0f, 0f);
            oitApi = api;
        }
        catch (Exception failure)
        {
            oitDisabled = true;
            platform!.Logger.Warning("VulkanStory: OIT renderer disabled: {0}", failure.Message);
            ReleaseOit();
            try
            {
                IShaderProgram? active = api.Render.CurrentActiveShader;
                if (active is { Disposed: false } && !ReferenceEquals(active, previous)) active.Stop();
                if (previous is { Disposed: false }) previous.Use();
            }
            finally { LoadFramebuffer(EnumFrameBuffer.Transparent); }
        }
    }
    private void RebuildOit(FrameBufferRef transparent)
    {
        var renderer = RequireDevice();
        ReleaseOit();
        oitTransparent = transparent;
        oitReveal = renderer.CreateTexture2D(transparent.Width, transparent.Height,
            TextureInternalFormat.Rgba8, TexturePixelFormat.Rgba, IntPtr.Zero, false);
        SetupTextureSampler(oitReveal, 9728, 33071);
        oitAccumulation = renderer.CreateTexture2DArray(transparent.Width, transparent.Height, 3,
            TextureInternalFormat.Rgba16f, TexturePixelFormat.Rgba);
        SetupTextureSampler(oitAccumulation, 9728, 33071);
        int handle = LiveFramebufferHandle(transparent);
        renderer.AttachTexture(handle, FramebufferAttachment.ColorAttachment0, oitReveal, 0);
        for (int layer = 0; layer < 3; layer++)
            renderer.AttachTexture(handle, (FramebufferAttachment)(36067 + layer), oitAccumulation, layer);
        RefreshCommonFrameWorkspaceOwnership();
    }
    internal void BindOit()
    {
        RequireDevice();
        if (oitDisabled || oitReveal == 0 || oitAccumulation == 0) return;
        Stated.BindTexture(6, oitReveal); Stated.BindTexture(7, oitAccumulation);
    }
    /// <summary>Releases current weighted-transparency pass targets and its borrowed API association.</summary>
    internal void ReleaseOit()
    {
        if (oitAccumulation > 0) device?.DeleteTexture(oitAccumulation);
        if (oitReveal > 0) device?.DeleteTexture(oitReveal);
        oitAccumulation = oitReveal = 0; oitTransparent = null;
        oitApi = null;
        RefreshCommonFrameWorkspaceOwnership();
    }
    internal void ReleaseOitFor(ICoreClientAPI api) { RequireDevice(); if (ReferenceEquals(api, oitApi)) ReleaseOit(); }
    /// <summary>Merges weighted transparency into the current scene through native or retained composition.</summary>
    internal void MergeTransparent()
    {
        var renderer = RequireDevice();
        if (!GameFramebufferBindings.OffscreenEnabled(platform!)) return;
        FrameBufferRef primary = FramebufferAt(EnumFrameBuffer.Primary), transparent = FramebufferAt(EnumFrameBuffer.Transparent);
        var compose = Vintagestory.Client.NoObf.ShaderPrograms.Transparentcompose;
        if (!NativeProgramUsable(compose) || transparent.ColorTextureIds.Length < 3)
            throw new InvalidOperationException("Transparent composition inputs are unavailable.");
        SetFramebuffer(primary, keepViewport: true);
        Stated.DepthTest = false; ToggleBlend(true, EnumBlendMode.Standard);
        Stated.SetSlotBlend(0, 32774, 770, 771, 770, 771);
        int motion = FrameState.MotionAttachment;
        bool writable = AoSettings.EffectiveTemporalPipeline && motion >= 0 && primary.ColorTextureIds.Length > motion;
        uint slots = GameFrameBindings.RenderSsao(platform!) ? 15u : 3u;
        if (writable) slots |= 1u << motion;
        AttachmentBlend[] blend = NativeOpaqueBlend(slots);
        for (int index = 0; index < blend.Length; index++)
        {
            if (((slots >> index) & 1) == 0) continue;
            blend[index].Enabled = true;
            if (writable && index == motion)
            { blend[index].SrcColor = blend[index].DstColor = blend[index].SrcAlpha = blend[index].DstAlpha = BlendFactor.One; }
        }
        int[] inputs = [transparent.ColorTextureIds[0], transparent.ColorTextureIds[1], transparent.ColorTextureIds[2],
            oitDisabled ? 0 : oitReveal, oitDisabled ? 0 : oitAccumulation];
        NativePipeline? pipeline = NativePostPipeline(nativeOitMerge, compose, primary.FboId, slots, blend, false, false, CompareOp.Less);
        if (pipeline != null)
        {
            try
            {
                if (renderer.BeginNativePass(StatedViewportPass("MergeTransparent/0", primary.FboId, slots,
                        inputs.Where(value => value > 0).ToArray(), PassFlags.None)))
                {
                    var textures = new NativeTexture[inputs.Length];
                    for (int index = 0; index < inputs.Length; index++) textures[index] = new NativeTexture(nativeOitMerge.Samplers[index], inputs[index]);
                    renderer.DrawNativeFullscreen(pipeline, textures);
                }
            }
            finally { renderer.EndNativePass(); }
            return;
        }
        StatedProgram = compose.ProgramId; Stated.SetDrawBuffers(primary.FboId, slots);
        if (writable) Stated.SetSlotBlend(motion, 32774, 1, 1, 1, 1);
        try { BindOwnedInputs(compose.ProgramId, nativeOitMerge.SamplerNames, inputs); DrawOwnedFullscreen("MergeTransparent/0", slots); }
        finally
        {
            StatedProgram = 0; Stated.SetDrawBuffers(primary.FboId, GameFrameBindings.RenderSsao(platform!) ? 15u : 3u);
            if (writable) Stated.SetSlotBlend(motion, 32774, 1, 0, 1, 0);
        }
    }
}
