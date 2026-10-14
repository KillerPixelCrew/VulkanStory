using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.RayTracing;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private NgxSession? rayNgxSession; // borrowed from the process's existing NGX owner
    private NgxDlssFeature? rayDlss;
    private int rayDlssOutput, rayDlssMotion;
    private bool rayDlssThisFrame;
    private string rayDlssFailure = "";
    internal void AttachRayNgxSession(NgxSession session) => rayNgxSession = session;
    internal void RetireRayReconstruction() => ReleaseRayReconstruction();

    private bool TryReconstructDlss(in RayTracingFrame frame)
    {
        rayDlssThisFrame = false;
        if (rayDlssFailure.Length != 0) return false;
        try
        {
            if (!NgxLifetime.Initialized || NgxLifetime.Spent || rayNgxSession == null)
                throw new NotSupportedException("the shared Vulkan NGX runtime is unavailable");
            uint width = (uint)rayRenderer!.Width, height = (uint)rayRenderer.Height;
            // TODO(RT): add native presentation-resolution RR output and quality modes with matching downstream target sizes.
            var settings = new NgxDlssSettings(width, height, width, height, NgxPerfQuality.Dlaa, NgxDlssSettings.ContractFlags);
            bool first = rayDlss == null || !rayDlss.Matches(settings);
            if (first)
            {
                ReleaseDlssRayReconstruction();
                NgxResult supported = rayNgxSession.FeatureRequirements((nint)_context.Instance.Handle,
                    (nint)_context.PhysicalDevice.Handle, NgxFeature.RayReconstruction, out var support, out _, out _);
                if (!NgxInterop.Succeeded(supported) || support != NgxFeatureSupport.Supported)
                    throw new NotSupportedException("NGX RR support query: " + NgxInterop.Describe(supported) + "/" + support);
                NgxResult caps = NgxInterop.GetCapabilityParameters(out nint capability);
                if (!NgxInterop.Succeeded(caps) || capability == 0)
                    throw new NotSupportedException("NGX RR capabilities: " + NgxInterop.Describe(caps));
                try
                {
                    NgxResult available = new NgxParameters(capability).GetUInt("SuperSamplingDenoising.Available", out uint value);
                    if (!NgxInterop.Succeeded(available) || value == 0)
                        throw new NotSupportedException("NGX reports Ray Reconstruction unavailable");
                }
                finally { NgxInterop.DestroyParameters(capability); }
                _targets.EndRendering(Commands);
                NgxResult created = NgxDlssFeature.Create((nint)_context.Device.Handle, Commands, settings,
                    out rayDlss, NgxFeature.RayReconstruction);
                if (!NgxInterop.Succeeded(created) || rayDlss == null)
                    throw new InvalidOperationException("NGX RR creation: " + NgxInterop.Describe(created));
                NgxLifetime.FeatureCreated();
                rayDlssOutput = _textures.CreateStorage(width, height, Format.R16G16B16A16Sfloat);
                rayDlssMotion = CreateUpscaleTexture((int)width, (int)height, Format.R16G16Sfloat, storage: false);
                if (_textures.Get(rayDlssOutput)!.Format != Format.R16G16B16A16Sfloat)
                    throw new NotSupportedException("DLSS RR output format unavailable");
            }
            VulkanTexture sourceMotion = _textures.Get(rayRenderer.Texture(RayTracingRenderer.Motion))!;
            if (!PrepareUpscalerMotion(sourceMotion, _textures.Get(rayDlssMotion)!, width, height, Commands))
                throw new InvalidOperationException("DLSS RR motion conversion unavailable");
            Span<int> ids = stackalloc int[8]
            {
                rayRenderer.Texture(RayTracingRenderer.Colour), rayDlssOutput,
                rayRenderer.Texture(RayTracingRenderer.DeviceDepth), rayDlssMotion,
                rayRenderer.Texture(RayTracingRenderer.NormalRoughness), rayRenderer.Texture(RayTracingRenderer.DiffuseAlbedo),
                rayRenderer.Texture(RayTracingRenderer.SpecularAlbedo), rayRenderer.Texture(RayTracingRenderer.SpecularDistance),
            };
            Span<NgxResourceVk> resources = stackalloc NgxResourceVk[8];
            for (int i = 0; i < ids.Length; i++)
            {
                VulkanTexture texture = _textures.Get(ids[i]) ?? throw new InvalidOperationException("RR guide missing");
                _textures.Require(_barriers, Commands, texture, i == 1 ? ResourceUsage.StorageWriteExternal : ResourceUsage.SampleExternal);
                resources[i] = NgxResourceVk.Texture(texture, readWrite: i == 1);
            }
            _barriers.Flush(Commands);
            NgxResult evaluated = rayDlss!.EvaluateRayReconstruction(Commands, resources, frame,
                first || frame.Reset || rayReconstructionPreviousFrame + 1 != LatencyFrameId);
            if (!NgxInterop.Succeeded(evaluated))
                throw new InvalidOperationException("NGX RR evaluate: " + NgxInterop.Describe(evaluated));
            rayDlssThisFrame = true;
            rayReconstructedThisFrame = true;
            rayReconstructionPreviousFrame = LatencyFrameId;
            return true;
        }
        catch (Exception failure)
        {
            ReleaseDlssRayReconstruction();
            rayDlssFailure = failure.Message;
            RenderTrace.Write("DLSS RR inactive; trying NRD: " + failure.Message);
            return false;
        }
        finally { _dynamicState.Invalidate(); }
    }

    private void ReleaseDlssRayReconstruction()
    {
        if (rayDlss != null)
        {
            _frames.DeferDeletion(rayDlss);
            NgxLifetime.FeatureRetired();
            rayDlss = null;
        }
        if (rayDlssOutput != 0) ReleaseTexture(rayDlssOutput);
        if (rayDlssMotion != 0) ReleaseTexture(rayDlssMotion);
        rayDlssOutput = rayDlssMotion = 0;
        rayDlssThisFrame = false;
    }
}
