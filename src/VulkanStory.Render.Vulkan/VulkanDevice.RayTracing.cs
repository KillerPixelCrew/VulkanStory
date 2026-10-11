using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.RayTracing;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private RayTracingScene? rayScene;
    private ulong raySceneFrame = ulong.MaxValue;
    private RayTracingRenderer? rayRenderer;
    private ulong rayDrawFrame = ulong.MaxValue;
    private uint[] rayAtlasSlots = [], rayAtlasFlags = [];
    internal string RayTracingReadiness { get; private set; } = "not requested";
    internal int RayTracingTexture(int kind) => kind == RayTracingRenderer.Colour && rayReconstructedThisFrame
        ? rayDlssThisFrame ? rayDlssOutput : rayReconstructedColour : rayRenderer?.Texture(kind) ?? 0;
    internal bool SupportsRayTracing { get; private set; }
    internal bool RaySceneCollectionEnabled { get; private set; }
    internal int RaySceneGeometryCount => rayScene?.GeometryCount ?? 0;
    internal uint RaySceneInstanceCount => rayScene?.LastInstanceCount ?? 0;
    internal RayTracingScene? RayScene => rayScene;

    internal void SetRaySceneCollection(bool enabled)
    {
        _frames.RequireResourceLifetime();
        if (!enabled) RayTracingReadiness = "not requested";
        if (enabled && !SupportsRayTracing) RayTracingReadiness = RayTracingCapabilityReason;
        enabled &= SupportsRayTracing;
        if (enabled == RaySceneCollectionEnabled) return;
        if (enabled) rayScene ??= new RayTracingScene(_context, _frames, _uploads, _shaderCompiler);
        else
        {
            ReleaseRayReconstruction();
            rayReconstructionMode = "off";
            rayReconstructionFailure = "";
            rayDlssFailure = "";
            rayScene?.Clear();
            if (rayRenderer != null)
            {
                rayRenderer.ReleaseImages();
                _frames.DeferDeletion(rayRenderer);
            }
            rayRenderer = null;
        }
        RaySceneCollectionEnabled = enabled;
        raySceneFrame = ulong.MaxValue;
        rayDrawFrame = ulong.MaxValue;
        RayTracingReadiness = enabled ? "waiting for scene geometry" : SupportsRayTracing ? "not requested" : RayTracingCapabilityReason;
    }

    internal void SetRayGeometry(ulong identity, RayTracingGeometryUpload upload)
    {
        _frames.RequireResourceLifetime();
        if (RaySceneCollectionEnabled) rayScene!.Set(identity, upload);
    }

    internal void SetDynamicRayGeometry(ulong identity, RayTracingGeometryUpload upload)
    {
        RequireFrameLifetime();
        if (RaySceneCollectionEnabled) rayScene!.SetDynamic(identity, upload, LatencyFrameId);
    }

    internal void SetSkinnedRayGeometry(ulong identity, in RayTracingSkinUpload upload)
    {
        RequireFrameLifetime();
        if (RaySceneCollectionEnabled) rayScene!.SetSkinned(identity, upload, LatencyFrameId);
    }

    internal void RemoveRayGeometry(ulong identity)
    {
        _frames.RequireResourceLifetime();
        rayScene?.Remove(identity);
    }

    internal void ClearRayScene()
    {
        _frames.RequireResourceLifetime();
        rayScene?.Clear();
        raySceneFrame = ulong.MaxValue;
        rayDrawFrame = ulong.MaxValue;
    }

    internal void RemoveRayWorld(ulong identity)
    {
        _frames.RequireResourceLifetime();
        rayScene?.RemoveWorld(identity);
        raySceneFrame = ulong.MaxValue;
        rayDrawFrame = ulong.MaxValue;
    }

    internal bool BuildRayScene(ulong worldIdentity, double cameraX, double cameraY, double cameraZ, int dimension)
    {
        RequireFrameLifetime();
        if (!RaySceneCollectionEnabled) return false;
        // Repeated post callbacks in one frame must not overwrite host-visible
        // inputs already referenced by its recorded build and traversal commands.
        if (raySceneFrame == LatencyFrameId) return rayScene!.LastInstanceCount != 0;
        var commands = Commands;
        _targets.EndPass(commands);
        _barriers.Flush(commands);
        bool built = rayScene!.Build(commands, LatencyFrameId, worldIdentity, cameraX, cameraY, cameraZ, dimension);
        raySceneFrame = LatencyFrameId;
        return built;
    }

    internal bool TraceRayScene(in RayTracingFrame frame, ReadOnlySpan<int> atlasTextures, int width, int height)
    {
        RequireFrameLifetime();
        if (!RaySceneCollectionEnabled || rayScene == null) return false;
        if (!frame.HasMatrices || rayScene.LastInstanceCount == 0)
        { RayTracingReadiness = "camera or scene geometry unavailable"; return false; }
        if (rayDrawFrame == LatencyFrameId) return true;
        BindlessTextureTable bindless = _bindless ?? throw new InvalidOperationException("Ray tracing requires the initialized texture table.");
        var commands = Commands;
        _targets.EndPass(commands);
        if (rayAtlasSlots.Length != atlasTextures.Length)
        {
            rayAtlasSlots = new uint[atlasTextures.Length]; rayAtlasFlags = new uint[atlasTextures.Length];
        }
        for (int i = 0; i < atlasTextures.Length; i++)
        {
            VulkanTexture? texture = _textures.Get(atlasTextures[i]);
            if (texture == null || texture.Released)
            { RayTracingReadiness = "terrain atlas not ready"; return false; }
            _textures.Require(_barriers, commands, texture, ResourceUsage.SampleRayTracing);
            rayAtlasSlots[i] = bindless.Resolve(texture, TextureKind.Texture2D, texture.State);
            rayAtlasFlags[i] = texture.Format is Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb ? 0u : 1u;
        }
        if (!rayScene.MapAtlasSlots(rayAtlasSlots, rayAtlasFlags))
        { RayTracingReadiness = "terrain atlas descriptors unavailable"; return false; }
        _barriers.Flush(commands);
        rayRenderer ??= new RayTracingRenderer(_context, _frames, _textures, bindless, _shaderCompiler, ReleaseTexture);
        if (!rayRenderer.Draw(commands, rayScene, frame, width, height, LatencyFrameId, _barriers))
        { RayTracingReadiness = "ray frame resources unavailable"; return false; }
        rayDrawFrame = LatencyFrameId;
        ReconstructRayFrame(frame);
        RayTracingReadiness = rayDlssThisFrame ? "traced current scene; DLSS RR at render resolution"
            : rayReconstructedThisFrame ? "traced current scene; NRD opaque reconstruction" + (rayDlssFailure.Length != 0 ? "; DLSS RR inactive: " + rayDlssFailure : "")
            : "traced current scene; " + (frame.Reconstruction == "off" ? "reconstruction off" : "reconstruction inactive: " + rayReconstructionFailure);
        return true;
    }
}
