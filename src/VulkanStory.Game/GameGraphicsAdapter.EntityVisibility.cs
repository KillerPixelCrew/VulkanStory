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
    private static readonly AccessTools.FieldRef<ClientMain, FrustumCulling> CullEntityFrustum =
        AccessTools.FieldRefAccess<ClientMain, FrustumCulling>("frustumCuller");
    private static readonly AccessTools.FieldRef<FrustumCulling, Vintagestory.API.Client.Plane[]> CullEntityPlanes =
        AccessTools.FieldRefAccess<FrustumCulling, Vintagestory.API.Client.Plane[]>("frustum");
    private readonly Dictionary<Type, (ulong Frame, bool Supported)> entityCullPolicies = new();
    private TerrainFrustumPlane[] entityCullPlaneSnapshot = [];

    private static bool StockEntityRendererAssembly(Assembly assembly) =>
        assembly == typeof(EntityShapeRenderer).Assembly || assembly == typeof(EntityItemRenderer).Assembly ||
        assembly == typeof(EntityBlockFalling).Assembly;

    private bool SupportedEntityRenderer(EntityRenderer renderer)
    {
        var type = renderer.GetType();
        if (renderer.entity is EntityPlayer || (renderer is not EntityShapeRenderer && type != typeof(EntityItemRenderer)) ||
            !StockEntityRendererAssembly(type.Assembly)) return false;
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

}
