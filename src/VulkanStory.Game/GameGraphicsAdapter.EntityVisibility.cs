using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal readonly record struct EntityVisibilityScope(EntityVisibilityInput? Previous, bool PreviousShadowProducers);
    private static readonly AccessTools.FieldRef<ClientMain, Dictionary<long, EntityRenderer>> CullEntityRenderers =
        AccessTools.FieldRefAccess<ClientMain, Dictionary<long, EntityRenderer>>("EntityRenderers");
    private static readonly AccessTools.FieldRef<ClientMain, FrustumCulling> CullEntityFrustum =
        AccessTools.FieldRefAccess<ClientMain, FrustumCulling>("frustumCuller");
    private static readonly AccessTools.FieldRef<FrustumCulling, Vintagestory.API.Client.Plane[]> CullEntityPlanes =
        AccessTools.FieldRefAccess<FrustumCulling, Vintagestory.API.Client.Plane[]>("frustum");
    private readonly Dictionary<Type, (ulong Frame, bool Supported)> entityCullPolicies = new();
    private TerrainFrustumPlane[] entityCullPlaneSnapshot = [];

    internal static void ValidateEntityVisibilityBindings()
    {
        _ = CullEntityRenderers; _ = CullEntityFrustum; _ = CullEntityPlanes;
        if (AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityRenderer.BeforeRender), [typeof(float)]) == null)
            throw new MissingMethodException("Stock entity visibility callback changed.");
        if (AccessTools.Method(typeof(EntityItemRenderer), "LoadModelMatrix",
                [typeof(ItemRenderInfo), typeof(bool), typeof(float)]) == null)
            throw new MissingMethodException("Stock dropped-item model transform changed.");
    }

    private bool SupportedEntityRenderer(EntityRenderer renderer)
    {
        var type = renderer.GetType();
        if (renderer.entity is EntityPlayer || (renderer is not EntityShapeRenderer && type != typeof(EntityItemRenderer)) ||
            !Performance.GpuEntityDraws.OwnsRenderer(type)) return false;
        // Device identity advances even when temporal reconstruction is disabled.
        ulong frame = device?.LatencyFrameId ?? 0;
        if (frame != 0 && entityCullPolicies.TryGetValue(type, out var saved) && saved.Frame == frame) return saved.Supported;
        bool supported = true;
        foreach (string name in new[] { nameof(EntityRenderer.BeforeRender), nameof(EntityRenderer.DoRender3DOpaque),
                     nameof(EntityRenderer.DoRender3DOpaqueBatched), nameof(EntityRenderer.DoRender3DAfterOIT) })
        {
            var method = AccessTools.Method(type, name, [typeof(float), typeof(bool)]) ??
                AccessTools.Method(type, name, [typeof(float)]);
            supported &= method != null && !ForeignEntityHooks(method);
        }
        var before = AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender", [typeof(float)]);
        supported &= before != null && !ForeignEntityHooks(before);
        if (type == typeof(EntityItemRenderer))
        {
            var model = AccessTools.Method(type, "LoadModelMatrix");
            supported &= model != null && !ForeignEntityHooks(model);
        }
        entityCullPolicies[type] = (frame, supported);
        return supported;
    }

    private static bool ForeignEntityHooks(MethodInfo method) =>
        Harmony.GetPatchInfo(method)?.Owners.Any(owner => !Performance.PerformancePatchSet.IsOwnOwner(owner)) == true;

    internal bool CanGpuCullEntity(Entity entity)
    {
        if (!Performance.GpuEntityDraws.Armed || Performance.PerformancePatchSet.Current is not
            { EntityOcclusionCull: true, GpuEntityDrawPreparation: true } || device?.CanPrepareTerrainDraws != true ||
            aoTemporal?.CurrentClient is not ClientMain client ||
            !CullEntityRenderers(client).TryGetValue(entity.EntityId, out var renderer)) return false;
        return ReferenceEquals(renderer.entity, entity) && SupportedEntityRenderer(renderer);
    }

    internal bool CanGpuCullFalling(EntityBlockFalling entity, FrustumCulling culler)
    {
        // This gate runs BEFORE removing the CPU sphere test. A depth source
        // alone cannot identify the caller's stage, target or coordinate space.
        if (!Performance.GpuEntityDraws.Armed || Performance.PerformancePatchSet.Current is not
            { EntityOcclusionCull: true, GpuEntityDrawPreparation: true } || device?.CanPrepareTerrainDraws != true ||
            modRenderStage != EnumRenderStage.Opaque || CurrentModPass != null ||
            currentFramebuffer == null || platform?.FrameBuffers is not { Count: > 0 } buffers ||
            !ReferenceEquals(currentFramebuffer, buffers[0]) ||
            currentFramebuffer.DepthTextureId != device.OcclusionDepth.SourceDepthTexture ||
            aoTemporal?.CurrentClient is not ClientMain client || !ReferenceEquals(CullEntityFrustum(client), culler) ||
            client.EntityPlayer is not { } player || player.Pos.Dimension != 0 || entity.Pos.Dimension != 0 ||
            entity.Pos.Y != entity.Pos.InternalY || player.CameraPos is not { } origin ||
            Performance.TerrainOcclusionCull.CameraNow() is not { } camera ||
            camera.X != (device.TerrainEyeIndex >= 0 ? client.MainCamera.CameraEyePos.X : origin.X) ||
            camera.Y != (device.TerrainEyeIndex >= 0 ? client.MainCamera.CameraEyePos.Y : origin.Y) ||
            camera.Z != (device.TerrainEyeIndex >= 0 ? client.MainCamera.CameraEyePos.Z : origin.Z) ||
            !double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(origin.Z) ||
            !double.IsFinite(camera.X) || !double.IsFinite(camera.Y) || !double.IsFinite(camera.Z) ||
            !double.IsFinite(entity.Pos.X) || !double.IsFinite(entity.Pos.Y) || !double.IsFinite(entity.Pos.Z) ||
            !double.IsFinite(entity.FrustumSphereRadius) || entity.FrustumSphereRadius < 0 ||
            entity.GetType() != typeof(EntityBlockFalling) ||
            ShaderProgramBase.CurrentShaderProgram is not { } program || !ReferenceEquals(program, ShaderPrograms.Standard) ||
            ShaderOverrides.IsOverridden("standard") || entity.meshRef is not { Disposed: false } mesh ||
            mesh.meshrefs is not { Length: > 0 } parts) return false;
        foreach (var part in parts)
            if (part is not VAO { Disposed: false, VaoId: > 0 } vao ||
                !Meshes.TryGetValue(vao, out var owner) || !ReferenceEquals(owner.Adapter, this) ||
                !device.CanPrepareRigidEntityMesh(owner.Handle, program.ProgramId)) return false;
        return true;
    }

    internal EntityVisibilityScope EnterFallingVisibilityScope(object renderer, EntityBlockFalling entity)
    {
        var gpu = RequireDevice();
        var previous = new EntityVisibilityScope(gpu.EntityVisibility, gpu.HoistEntityShadowProducers);
        gpu.EntityVisibility = null;
        if (!Performance.GpuEntityDraws.OwnsFallingFrame(renderer) ||
            aoTemporal?.CurrentClient is not ClientMain client || !CanGpuCullFalling(entity, CullEntityFrustum(client)) ||
            !CaptureEntityCullPlanes(client)) return previous;
        var origin = client.EntityPlayer.CameraPos;
        // ModelMat uses Pos.Y, not InternalY. Admission above refuses dimensions
        // where these origins differ; the final per-mesh shader shadow supplies
        // selection-box translation, bobbing and rotation without reconstruction.
        // Stereo view translation is already in the stock view matrix; bounds
        // keep the model's centre origin while the batch retains physical-eye depth.
        gpu.EntityVisibility = new EntityVisibilityInput(default, entityCullPlaneSnapshot,
            origin.X, origin.Y, origin.Z, -1);
        return previous;
    }

    private bool CaptureEntityCullPlanes(ClientMain client)
    {
        var source = CullEntityPlanes(CullEntityFrustum(client));
        if (source.Length != 6) return false;
        bool changed = entityCullPlaneSnapshot.Length != 6;
        for (int i = 0; i < 6; i++)
        {
            var p = source[i];
            if (!double.IsFinite(p.normalX) || !double.IsFinite(p.normalY) ||
                !double.IsFinite(p.normalZ) || !double.IsFinite(p.D)) return false;
            changed |= entityCullPlaneSnapshot.Length != 6 ||
                entityCullPlaneSnapshot[i] != new TerrainFrustumPlane(p.normalX, p.normalY, p.normalZ, p.D);
        }
        if (changed)
        {
            var snapshot = new TerrainFrustumPlane[6];
            for (int i = 0; i < 6; i++) snapshot[i] = new(source[i].normalX, source[i].normalY, source[i].normalZ, source[i].D);
            entityCullPlaneSnapshot = snapshot;
        }
        return true;
    }

    internal EntityVisibilityScope EnterEntityVisibilityScope(EntityRenderer renderer, bool shadow)
    {
        var device = RequireDevice();
        var previous = new EntityVisibilityScope(device.EntityVisibility, device.HoistEntityShadowProducers);
        device.EntityVisibility = null;
        if (residentEntityScope.Renderer != null)
        {
            device.HoistEntityShadowProducers = false;
            return previous;
        }
        // Shadow draws keep their original path. Only the producers of an entity whose
        // main draws are prepared may record ahead of the frame commands (renderer rules).
        device.HoistEntityShadowProducers = shadow && CanGpuCullEntity(renderer.entity);
        if (shadow || !CanGpuCullEntity(renderer.entity) || aoTemporal?.CurrentClient is not ClientMain client ||
            Performance.TerrainOcclusionCull.CameraNow() is not { } camera) return previous;
        if (!CaptureEntityCullPlanes(client)) return previous;
        var entity = renderer.entity;
        if (renderer.GetType() == typeof(EntityItemRenderer))
        {
            // Item mesh and final model transform are only known at the real draw seam.
            // Any unsupported draw keeps its original direct submission, unclipped.
            if (client.EntityPlayer?.CameraPos is not { } modelOrigin ||
                !double.IsFinite(modelOrigin.X) || !double.IsFinite(modelOrigin.Y) || !double.IsFinite(modelOrigin.Z)) return previous;
            device.EntityVisibility = new EntityVisibilityInput(default, entityCullPlaneSnapshot,
                modelOrigin.X, modelOrigin.Y, modelOrigin.Z, -1);
            return previous;
        }
        double x = entity.Pos.X, y = entity.Pos.InternalY, z = entity.Pos.Z;
        // Enclose both the original float frustum centre and the actual world centre,
        // plus movement/interpolation between preparation and rendering.
        double rounding = Math.Max(Math.Max(Math.Abs(x - (float)x), Math.Abs(y - (float)y)), Math.Abs(z - (float)z));
        double r = entity.FrustumSphereRadius + 1.5 + rounding;
        if (!double.IsFinite(r) || r < 0) return previous;
        device.EntityVisibility = new EntityVisibilityInput(new TerrainDrawBounds(x - r, y - r, z - r,
            x + r, y + r, z + r, true), entityCullPlaneSnapshot, camera.X, camera.Y, camera.Z);
        return previous;
    }

    internal void ExitEntityVisibilityScope(EntityVisibilityScope previous)
    {
        var device = RequireDevice();
        device.EntityVisibility = previous.Previous;
        device.HoistEntityShadowProducers = previous.PreviousShadowProducers;
    }

    internal EntityVisibilityScope EnterEntityMeshVisibility()
    {
        var renderer = RequireDevice();
        var previous = new EntityVisibilityScope(renderer.EntityVisibility, renderer.HoistEntityShadowProducers);
        if (renderer.EntityVisibility is not { RigidProgram: -1 } visibility) return previous;
        // Exact stock program identity and unmodified selected assets, for native and
        // translated standard alike. Missing uniforms never identify a rigid shader.
        var program = ShaderProgramBase.CurrentShaderProgram;
        renderer.EntityVisibility = program != null && ReferenceEquals(program, ShaderPrograms.Standard) &&
            !ShaderOverrides.IsOverridden("standard") &&
            aoTemporal?.CurrentClient?.EntityPlayer?.CameraPos is { } origin &&
            origin.X == visibility.CameraX && origin.Y == visibility.CameraY && origin.Z == visibility.CameraZ
            ? visibility with { RigidProgram = program.ProgramId } : null;
        return previous;
    }
}
