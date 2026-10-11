using VulkanStory.Contracts;
using System.Runtime.CompilerServices;
using System.Numerics;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.RayTracing;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private sealed record RayWorld(ulong Identity);
    private readonly ConditionalWeakTable<object, RayWorld> rayWorlds = new();
    private long nextRayWorld;
    internal ulong RayWorldIdentity(object world)
    {
        RequireLifecycleDevice();
        if (rayWorlds.TryGetValue(world, out RayWorld? known)) return known.Identity;
        var created = new RayWorld(checked((ulong)Interlocked.Increment(ref nextRayWorld)));
        rayWorlds.Add(world, created);
        return created.Identity;
    }
    internal bool RaySceneCollectionEnabled => RequireDevice().RaySceneCollectionEnabled;
    internal void SetRayGeometry(ulong identity, RayTracingGeometryUpload upload) => RequireDevice().SetRayGeometry(identity, upload);
    internal void RemoveRayGeometry(ulong identity) => RequireDevice().RemoveRayGeometry(identity);
    internal void RemoveRayWorld(object world)
    {
        ulong identity = RayWorldIdentity(world);
        RequireDevice().RemoveRayWorld(identity);
        RemoveRayActors(identity);
    }
    internal bool RayTracedThisFrame { get; set; }
    private int[] rayAtlases = [];
    private static readonly AccessTools.FieldRef<DefaultShaderUniforms, float> RayDaylight =
        AccessTools.FieldRefAccess<DefaultShaderUniforms, float>("SkyDaylight");
    private readonly float[] rayInverseProjection = new float[16], rayCurrentClip = new float[16], rayPreviousClip = new float[16];
    private readonly float[] rayProjection = new float[16], rayView = new float[16], rayPreviousProjection = new float[16], rayPreviousView = new float[16];

    internal bool RenderPathTracing(ClientMain client, GameTemporalFrame temporal, RendererSettings settings)
    {
        var renderer = RequireDevice();
        RayTracedThisFrame = false;
        if (!renderer.RaySceneCollectionEnabled || !temporal.HasCamera || temporal.FrameId != renderer.LatencyFrameId) return false;
        var primary = FramebufferAt(EnumFrameBuffer.Primary);
        temporal.Projection.Span.CopyTo(rayProjection); temporal.View.Span.CopyTo(rayView);
        temporal.PreviousProjection.Span.CopyTo(rayPreviousProjection); temporal.PreviousView.Span.CopyTo(rayPreviousView);
        if (Mat4f.Invert(rayInverseProjection, rayProjection) == null) return false;
        Mat4f.Mul(rayCurrentClip, rayProjection, rayView);
        Mat4f.Mul(rayPreviousClip, rayPreviousProjection, rayPreviousView);
        var ambient = client.AmbientManager;
        var light = client.shUniforms.LightPosition3D;
        var fog = ambient.BlendedFogColor;
        var colour = ambient.BlendedAmbientColor;
        Vector3 sky = new(colour.X, colour.Y, colour.Z);
        Vector3 horizon = new(fog.X, fog.Y, fog.Z);
        var frame = new RayTracingFrame(rayInverseProjection, temporal.Provider.InverseView,
            rayPreviousClip, rayCurrentClip,
            new Vector3(temporal.CameraDeltaX, temporal.CameraDeltaY, temporal.CameraDeltaZ), temporal.Provider.FarPlane,
            new Vector2(temporal.Provider.JitterX, temporal.Provider.JitterY),
            new Vector3(light.X, light.Y, light.Z), sky,
            Math.Max(0f, RayDaylight(client.shUniforms) + client.shUniforms.SunlightExtraBrightness),
            sky, horizon, (uint)renderer.LatencyFrameId, settings.PathTracingSamples, settings.PathTracingBounces,
            temporal.Provider.Reset, rayView, rayProjection, rayPreviousView, rayPreviousProjection,
            temporal.Provider.DeltaTimeMs, settings.RayReconstruction);
        RayTracedThisFrame = renderer.TraceRayScene(frame, rayAtlases, primary.Width, primary.Height);
        return RayTracedThisFrame;
    }

    private int RaySceneColour => RequireDevice().RayTracingTexture(RayTracingRenderer.Colour);
    private int RaySceneGlow => RequireDevice().RayTracingTexture(RayTracingRenderer.Glow);
    internal int RaySceneDepth => RequireDevice().RayTracingTexture(RayTracingRenderer.DeviceDepth);
    internal int RaySceneMotion => RequireDevice().RayTracingTexture(RayTracingRenderer.Motion);
}
