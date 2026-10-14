using System.Runtime.InteropServices;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.RayTracing;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private NrdDenoiser? rayNrd;
    private int rayNrdCompose, rayReconstructedColour;
    private bool rayReconstructedThisFrame, supportsNrdImages;
    private string rayReconstructionMode = "off", rayReconstructionFailure = "";
    private ulong rayReconstructionPreviousFrame = ulong.MaxValue;
    private int rayReconstructionWidth, rayReconstructionHeight;

    private void ReconstructRayFrame(in RayTracingFrame frame)
    {
        // TODO(RT): integrate AMD Ray Regeneration through its verified runtime API independently of this Vulkan/NRD path.
        // TODO(RT): synthesize layered/reflection guides for transparent transport before claiming complete scene reconstruction.
        rayReconstructedThisFrame = false;
        rayDlssThisFrame = false;
        if (frame.Reconstruction != rayReconstructionMode)
        {
            ReleaseRayReconstruction();
            rayReconstructionMode = frame.Reconstruction;
            rayReconstructionFailure = "";
            rayDlssFailure = "";
        }
        if (frame.Reconstruction == "off") return;
        if (rayReconstructionWidth != rayRenderer!.Width || rayReconstructionHeight != rayRenderer.Height)
        {
            rayReconstructionWidth = rayRenderer.Width; rayReconstructionHeight = rayRenderer.Height;
            rayReconstructionFailure = rayDlssFailure = "";
        }
        if (frame.Reconstruction == "dlss" && TryReconstructDlss(frame)) return;
        if (!supportsNrdImages)
        { rayReconstructionFailure = "NRD unformatted storage-image writes unavailable"; return; }
        if (rayReconstructionFailure.Length != 0) return;
        try
        {
            rayNrd ??= new NrdDenoiser(_context, _frames, _textures, ReleaseTexture);
            VulkanTexture? output = _textures.Get(rayReconstructedColour);
            if (output == null || output.Width != rayRenderer!.Width || output.Height != rayRenderer.Height)
            {
                if (rayReconstructedColour != 0) ReleaseTexture(rayReconstructedColour);
                rayReconstructedColour = 0;
                rayReconstructedColour = _textures.CreateStorage((uint)rayRenderer!.Width, (uint)rayRenderer.Height, Format.R32G32B32A32Sfloat);
            }
            if (rayNrdCompose == 0)
            {
                using Stream stream = typeof(RayTracingRenderer).Assembly.GetManifestResourceStream("VulkanStory.RayTracing.nrd-compose.comp")!;
                using var reader = new StreamReader(stream);
                rayNrdCompose = CreateComputeProgram(reader.ReadToEnd(), "ray-nrd-compose",
                    [new(0, ComputeSlotKind.Sampled), new(1, ComputeSlotKind.Sampled), new(2, ComputeSlotKind.Sampled),
                     new(3, ComputeSlotKind.Sampled), new(4, ComputeSlotKind.Sampled), new(5, ComputeSlotKind.Storage)], sizeof(float));
                if (rayNrdCompose == 0) throw new InvalidOperationException(GetError());
            }
            var current = frame with { Reset = frame.Reset || rayReconstructionPreviousFrame + 1 != LatencyFrameId };
            rayNrd.Denoise(Commands, rayRenderer!, current, LatencyFrameId, _barriers);
            _dynamicState.Invalidate();
            float range = frame.FarPlane;
            byte[] settings = MemoryMarshal.AsBytes(new ReadOnlySpan<float>(in range)).ToArray();
            if (!RecordComputePass(new ComputePassDeclaration
            {
                Name = "ray-nrd-compose", ProgramId = rayNrdCompose,
                Bindings = [new(0, rayNrd.Diffuse, ComputeAccess.Sampled), new(1, rayNrd.Specular, ComputeAccess.Sampled),
                    new(2, rayRenderer!.Texture(RayTracingRenderer.StableRadiance), ComputeAccess.Sampled),
                    new(3, rayRenderer.Texture(RayTracingRenderer.Colour), ComputeAccess.Sampled),
                    new(4, rayRenderer.Texture(RayTracingRenderer.ViewDepth), ComputeAccess.Sampled),
                    new(5, rayReconstructedColour, ComputeAccess.StorageWrite)],
                Dispatches = [ComputeDispatch.Covering(5, settings)],
            })) throw new InvalidOperationException(GetError());
            rayReconstructedThisFrame = true;
            rayReconstructionPreviousFrame = LatencyFrameId;
        }
        catch (Exception failure)
        {
            ReleaseRayReconstruction();
            rayReconstructionFailure = failure.Message;
            RenderTrace.Write("Ray reconstruction inactive: " + failure.Message);
        }
        finally { _dynamicState.Invalidate(); }
    }

    private void ReleaseRayReconstruction()
    {
        ReleaseDlssRayReconstruction();
        if (rayNrd != null)
        {
            // Unregister images while the device's descriptor/texture owners are alive;
            // their Vulkan allocations and the kernels retire on the same timeline.
            rayNrd.ReleaseImages();
            _frames.DeferDeletion(rayNrd);
        }
        rayNrd = null;
        if (rayReconstructedColour != 0) ReleaseTexture(rayReconstructedColour);
        rayReconstructedColour = 0;
        if (rayNrdCompose != 0) DeleteComputeProgram(rayNrdCompose);
        rayNrdCompose = 0; rayReconstructedThisFrame = false;
        rayReconstructionPreviousFrame = ulong.MaxValue;
    }
}
