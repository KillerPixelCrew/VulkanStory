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
    internal readonly record struct EntityVisibilityScope(EntityVisibilityInput? Previous);
    private static readonly AccessTools.FieldRef<ClientMain, Dictionary<long, EntityRenderer>> CullEntityRenderers =
        AccessTools.FieldRefAccess<ClientMain, Dictionary<long, EntityRenderer>>("EntityRenderers");
    private static readonly AccessTools.FieldRef<ClientMain, FrustumCulling> CullEntityFrustum =
        AccessTools.FieldRefAccess<ClientMain, FrustumCulling>("frustumCuller");
    private static readonly AccessTools.FieldRef<FrustumCulling, Vintagestory.API.Client.Plane[]> CullEntityPlanes =
        AccessTools.FieldRefAccess<FrustumCulling, Vintagestory.API.Client.Plane[]>("frustum");
    private readonly Dictionary<Type, (long Frame, bool Supported)> entityCullPolicies = new();
    private TerrainFrustumPlane[] entityCullPlaneSnapshot = [];

    internal static void ValidateEntityVisibilityBindings()
    {
        _ = CullEntityRenderers; _ = CullEntityFrustum; _ = CullEntityPlanes;
        if (AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityRenderer.BeforeRender), [typeof(float)]) == null)
            throw new MissingMethodException("Stock entity visibility callback changed.");
    }

    private bool SupportedEntityRenderer(EntityRenderer renderer)
    {
        var type = renderer.GetType();
        if (renderer.entity is EntityPlayer || renderer is not EntityShapeRenderer ||
            !Performance.GpuEntityDraws.OwnsRenderer(type)) return false;
        long frame = aoTemporal?.State.FrameIndex ?? -1;
        if (entityCullPolicies.TryGetValue(type, out var saved) && saved.Frame == frame) return saved.Supported;
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

    internal EntityVisibilityScope EnterEntityVisibilityScope(EntityRenderer renderer, bool shadow)
    {
        var device = RequireDevice();
        var previous = new EntityVisibilityScope(device.EntityVisibility);
        device.EntityVisibility = null;
        if (shadow || !CanGpuCullEntity(renderer.entity) || aoTemporal?.CurrentClient is not ClientMain client ||
            Performance.TerrainOcclusionCull.CameraNow() is not { } camera) return previous;
        var source = CullEntityPlanes(CullEntityFrustum(client));
        if (source.Length != 6) return previous;
        bool changed = entityCullPlaneSnapshot.Length != 6;
        for (int i = 0; i < 6; i++)
        {
            var plane = source[i];
            if (!double.IsFinite(plane.normalX) || !double.IsFinite(plane.normalY) ||
                !double.IsFinite(plane.normalZ) || !double.IsFinite(plane.D)) return previous;
            changed |= entityCullPlaneSnapshot.Length != 6 ||
                entityCullPlaneSnapshot[i] != new TerrainFrustumPlane(plane.normalX, plane.normalY, plane.normalZ, plane.D);
        }
        if (changed)
        {
            var snapshot = new TerrainFrustumPlane[6];
            for (int i = 0; i < 6; i++) snapshot[i] = new(source[i].normalX, source[i].normalY, source[i].normalZ, source[i].D);
            entityCullPlaneSnapshot = snapshot;
        }
        var entity = renderer.entity;
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

    internal void ExitEntityVisibilityScope(EntityVisibilityScope previous) => RequireDevice().EntityVisibility = previous.Previous;
}
