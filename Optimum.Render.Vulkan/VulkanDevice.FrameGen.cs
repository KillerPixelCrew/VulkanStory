using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

/// <summary>
/// The images one DLSS-G evaluate reads and writes, as backend texture ids. A
/// negative id is "not provided" for the three optional inputs and outputs.
/// </summary>
/// <param name="Backbuffer">The composed display image, UI included (<c>pBackbuffer</c>).</param>
/// <param name="Depth">The render-resolution depth DLSS SR was given, or the display one without SR.</param>
/// <param name="MotionVectors">The render-resolution motion DLSS SR was given.</param>
/// <param name="Hudless">The display image before the UI (<c>pHudless</c>); optional.</param>
/// <param name="Ui">Premultiplied UI colour and alpha (<c>pUI</c>); optional.</param>
/// <param name="OutputInterpolated">
/// The generated frame. Display size, the backbuffer's format, STORAGE usage.
/// </param>
/// <param name="OutputReal">
/// The retained real frame (<c>OutputReal</c>, which the guide calls "the recommended
/// method": "The algorithm will then copy the input backbuffer to this texture").
/// Same requirements as <paramref name="OutputInterpolated" />; optional.
/// </param>
internal readonly record struct FrameGenerationImages(
    int Backbuffer,
    int Depth,
    int MotionVectors,
    int Hudless,
    int Ui,
    int OutputInterpolated,
    int OutputReal);

/// <summary>
/// DLSS frame generation on the device: the feature's lifetime and its evaluate, in
/// seam D2's shape (see <c>VulkanDevice.Dlss.cs</c> for what NGX leaves behind on our
/// command buffer and why the restore is only <see cref="DynamicStateCache.Invalidate" />).
///
/// <para><b>What the present thread may rely on.</b> After
/// <see cref="EvaluateFrameGeneration" /> returns Success, both outputs have been
/// written by NGX's dispatches <i>in this frame's command buffer</i> and a barrier
/// recorded after them moves both to TRANSFER_SRC_OPTIMAL, which the resource state
/// tracker records as a <see cref="ResourceUsage.TransferSrc" /> use. They are ready
/// to be blitted to a swapchain image once the Frame-timeline value that completes
/// this frame's submission is reached; nothing is readable before that.</para>
///
/// <para><b>Threading.</b> <see cref="_frameGeneration" /> and the counters are
/// touched by the render thread only (it records the evaluate, creates and retires the
/// feature); no member of this partial is shared with the present thread, which
/// receives texture ids and a timeline value, never the feature.</para>
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>The live DLSS-G feature, or null. Render thread only.</summary>
    private NgxDlssgFeature? _frameGeneration;

    /// <summary>Features created by <see cref="EvaluateFrameGeneration" />. Render thread only.</summary>
    internal int FrameGenerationFeaturesCreated { get; private set; }

    /// <summary>Features handed to the frame timeline. Render thread only.</summary>
    internal int FrameGenerationFeaturesRetired { get; private set; }

    /// <summary>What the last feature creation answered, for the stand-down log line.</summary>
    internal NgxResult LastFrameGenerationCreateResult { get; private set; }

    /// <summary>
    /// A texture's VkImage handle, so a test can say which of its images a layer
    /// message names (the layers print handles, not texture ids).
    /// </summary>
    internal ulong TextureImageForTests(int textureId) => _textures.Get(textureId)?.Image.Handle ?? 0;

    /// <summary>The live feature, for the tests that check its release result.</summary>
    internal NgxDlssgFeature? FrameGenerationFeatureForTests => _frameGeneration;

    /// <summary>The live feature's settings, or default when there is none.</summary>
    internal NgxDlssgSettings FrameGenerationSettings =>
        _frameGeneration != null ? _frameGeneration.Settings : default;

    /// <summary>
    /// Ensures a DLSS-G feature for the backbuffer's size and format and the UI
    /// recomposition choice, then evaluates one frame into the caller's output pair.
    ///
    /// <list type="number">
    /// <item>Any open rendering scope is closed: NGX's barriers and dispatches cannot
    /// run inside one.</item>
    /// <item>A feature created for a different size, format or recomposition choice
    /// is retired onto the frame timeline and a new one is created on this frame's
    /// command buffer (the guide: a changed backbuffer size needs a released and
    /// re-created feature).</item>
    /// <item>Inputs go to SHADER_READ_ONLY_OPTIMAL readable from compute
    /// (<see cref="ResourceUsage.SampleExternal" />), outputs to GENERAL as storage
    /// writes (<see cref="ResourceUsage.StorageWriteExternal" />, which already covers
    /// NGX's first-evaluate clear of an output), in one barrier command.</item>
    /// <item>EvaluateFeature records NGX's dispatches.</item>
    /// <item>Both outputs move to TRANSFER_SRC_OPTIMAL, the layout the present thread
    /// blits from; NGX restores the layouts it changed before it returns (DLSS
    /// Programming Guide §3.4), so the tracker's GENERAL is still true at that
    /// barrier.</item>
    /// <item>The dynamic state cache is invalidated.</item>
    /// </list>
    ///
    /// Anything other than Success means no generated frame this frame: the caller
    /// presents the real frame alone. A creation failure is reported through
    /// <see cref="LastFrameGenerationCreateResult" /> and repeats every frame it is
    /// asked for, so the caller stands frame generation down on the first one rather
    /// than asking again.
    /// </summary>
    internal NgxResult EvaluateFrameGeneration(
        in FrameGenerationImages images, in NgxDlssgEvaluation frame, bool userInterfaceRecomposition = true)
    {
        if (!_frameActive) return NgxResult.FailNotInitialized;

        VulkanTexture? backbuffer = _textures.Get(images.Backbuffer);
        VulkanTexture? depth = _textures.Get(images.Depth);
        VulkanTexture? motion = _textures.Get(images.MotionVectors);
        VulkanTexture? interpolated = _textures.Get(images.OutputInterpolated);
        VulkanTexture? hudless = images.Hudless >= 0 ? _textures.Get(images.Hudless) : null;
        VulkanTexture? ui = images.Ui >= 0 ? _textures.Get(images.Ui) : null;
        VulkanTexture? real = images.OutputReal >= 0 ? _textures.Get(images.OutputReal) : null;
        if (backbuffer == null || depth == null || motion == null || interpolated == null ||
            (images.Hudless >= 0 && hudless == null) || (images.Ui >= 0 && ui == null) ||
            (images.OutputReal >= 0 && real == null))
        {
            return NgxResult.FailMissingInput;
        }

        // The guide: outputs are "the same size and format as pBackbuffer", and
        // backbuffer, HUDLess and UI share one resolution. A mismatch is refused here
        // rather than handed to the driver, where it is not an error code but
        // undefined reads inside NGX's dispatches.
        if (!SameSizeAndFormat(interpolated, backbuffer) || (real != null && !SameSizeAndFormat(real, backbuffer)) ||
            (hudless != null && !SameSize(hudless, backbuffer)) || (ui != null && !SameSize(ui, backbuffer)))
        {
            return NgxResult.FailInvalidParameter;
        }

        CommandBuffer commandBuffer = Commands;
        _targets.FlushAllPendingClears(commandBuffer);
        _targets.EndRendering(commandBuffer);

        var settings = new NgxDlssgSettings(
            backbuffer.Width, backbuffer.Height, backbuffer.Format, userInterfaceRecomposition);
        if (_frameGeneration == null || !_frameGeneration.Matches(settings))
        {
            // The old feature goes onto the frame timeline, never inline: an evaluate
            // recorded earlier in this frame or a frame still in flight may name it.
            RetireFrameGeneration();

            // VRAM estimation is skipped: it would need a new shim entry point (see
            // NgxDlssgFeature). A creation that runs out of memory fails like any other.
            NgxResult created = NgxDlssgFeature.Create(
                (IntPtr)_context.Device.Handle, commandBuffer, settings, out NgxDlssgFeature? feature);
            LastFrameGenerationCreateResult = created;
            _dynamicState.Invalidate();
            if (created != NgxResult.Success || feature == null)
            {
                if (RenderTrace.Enabled)
                {
                    RenderTrace.Write("dlssg create " + settings + ": " + NgxInterop.Describe(created));
                }
                return NgxInterop.Succeeded(created) ? NgxResult.FailUnableToInitializeFeature : created;
            }

            _frameGeneration = feature;
            FrameGenerationFeaturesCreated++;
            // The lifetime owner counts live features so it can refuse Shutdown1 while
            // this one could still name NGX state.
            NgxLifetime.FeatureCreated();
        }

        _textures.Require(_barriers, commandBuffer, backbuffer, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commandBuffer, depth, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commandBuffer, motion, ResourceUsage.SampleExternal);
        if (hudless != null) _textures.Require(_barriers, commandBuffer, hudless, ResourceUsage.SampleExternal);
        if (ui != null) _textures.Require(_barriers, commandBuffer, ui, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commandBuffer, interpolated, ResourceUsage.StorageWriteExternal);
        if (real != null) _textures.Require(_barriers, commandBuffer, real, ResourceUsage.StorageWriteExternal);
        _barriers.Flush(commandBuffer);

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("dlssg evaluate " + settings +
                " backbuffer=" + images.Backbuffer + " depth=" + images.Depth + " mv=" + images.MotionVectors +
                " hudless=" + images.Hudless + " ui=" + images.Ui +
                " interp=" + images.OutputInterpolated + " real=" + images.OutputReal + " reset=" + frame.Reset);
        }

        NgxResult result = _frameGeneration.Evaluate(
            commandBuffer,
            NgxResourceVk.Texture(backbuffer, readWrite: false),
            NgxResourceVk.Texture(depth, readWrite: false),
            NgxResourceVk.Texture(motion, readWrite: false),
            hudless != null ? NgxResourceVk.Texture(hudless, readWrite: false) : null,
            ui != null ? NgxResourceVk.Texture(ui, readWrite: false) : null,
            NgxResourceVk.Texture(interpolated, readWrite: true),
            real != null ? NgxResourceVk.Texture(real, readWrite: true) : null,
            frame);

        // Seam D2's restore, then the layout the present thread blits from - recorded
        // after NGX's dispatches in the same command buffer, so the tracker describes
        // both outputs as transfer sources from here on.
        _dynamicState.Invalidate();
        _textures.Require(_barriers, commandBuffer, interpolated, ResourceUsage.TransferSrc);
        if (real != null) _textures.Require(_barriers, commandBuffer, real, ResourceUsage.TransferSrc);
        _barriers.Flush(commandBuffer);
        return result;
    }

    /// <summary>
    /// Hands the live DLSS-G feature to the frame ring, which releases it once every
    /// frame that could name its handle has completed, and stops counting it as live.
    /// This is the frame-generation half of the <c>releaseFeatures</c> step
    /// <see cref="NgxLifetime.ShutDown" /> performs before its drain; it is also what a
    /// stand-down or a settings change calls. Never disposes inline.
    /// </summary>
    internal void RetireFrameGeneration()
    {
        NgxDlssgFeature? feature = _frameGeneration;
        if (feature == null) return;
        _frameGeneration = null;
        FrameGenerationFeaturesRetired++;
        NgxLifetime.FeatureRetired();
        _frames.DeferDeletion(feature);
    }

    private static bool SameSize(VulkanTexture a, VulkanTexture b) => a.Width == b.Width && a.Height == b.Height;

    private static bool SameSizeAndFormat(VulkanTexture a, VulkanTexture b) => SameSize(a, b) && a.Format == b.Format;
}
