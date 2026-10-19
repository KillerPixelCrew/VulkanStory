using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game.Performance;

/// <summary>Pairs the stock pool's accepted draw ranges with producer bounds for GPU occlusion.</summary>
internal static class GpuTerrainDraws
{
    private static bool armed, scope;
    private static MeshDataPool? pool;
    private static bool collecting, complete;
    private static readonly List<TerrainDrawBounds> bounds = new();
    private static readonly List<(int First, int Count)> ranges = new();
    internal static bool Collecting => collecting;

    private static MethodInfo Cull() => AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull),
        [typeof(FrustumCulling), typeof(EnumFrustumCullMode)])!;
    private static MethodInfo Visible() => AccessTools.Method(typeof(ModelDataPoolLocation), nameof(ModelDataPoolLocation.IsVisible),
        [typeof(EnumFrustumCullMode), typeof(FrustumCulling)])!;
    private static MethodInfo Remove() => AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation),
        [typeof(ModelDataPoolLocation)])!;

    internal static PerformanceFeature Create() => new("gpu-terrain-draws", settings => settings.GpuTerrainDrawPreparation, () =>
    {
        if (Cull() == null || Visible() == null || Remove() == null ||
            PatchProcessor.GetOriginalInstructions(Cull()).Count(instruction => instruction.Calls(Visible())) != 1)
            throw new InvalidOperationException("Terrain pool visibility/range producer changed.");
    }, harmony =>
    {
        harmony.Patch(Cull(), prefix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(BeforeCull)),
            postfix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(AfterCull)),
            finalizer: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(FinishCull)));
        harmony.Patch(Visible(), postfix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(AfterVisible)) { priority = Priority.Last });
        harmony.Patch(Remove(), prefix: new HarmonyMethod(typeof(TerrainOcclusionCull), nameof(TerrainOcclusionCull.InvalidateDepth)));
        armed = true;
    }, Uninstall: () => { armed = false; EndScope(); }, WorldLeft: EndScope);

    internal static void BeginScope(bool available)
    {
        EndScope();
        scope = armed && available && PerformancePatchSet.Current is
            { TerrainOcclusionCull: true, GpuTerrainDrawPreparation: true };
    }

    internal static void EndScope()
    {
        scope = collecting = complete = false;
        pool = null; bounds.Clear(); ranges.Clear();
    }

    private static void BeforeCull(MeshDataPool __instance, EnumFrustumCullMode frustumCullMode)
    {
        pool = null; bounds.Clear(); ranges.Clear(); complete = false;
        collecting = scope && frustumCullMode == EnumFrustumCullMode.CullNormal;
        if (collecting) pool = __instance;
    }

    private static void AfterCull() { complete = collecting; collecting = false; }
    private static Exception? FinishCull(Exception? __exception)
    {
        collecting = false;
        if (__exception != null) { pool = null; complete = false; }
        return __exception;
    }

    private static void AfterVisible(ModelDataPoolLocation __instance, bool __result)
    {
        if (!collecting || !__result) return;
        // Retain the stock frustum, range, LOD and mod-provided visibility result.
        if (bounds.Count >= 4096) { complete = false; collecting = false; pool = null; return; }
        Sphere sphere = __instance.FrustumCullSphere;
        double x = sphere.radius / Math.Sqrt(3), y = sphere.radiusY / Math.Sqrt(3), z = sphere.radiusZ / Math.Sqrt(3);
        bounds.Add(new TerrainDrawBounds(sphere.x - x, sphere.y - y, sphere.z - z,
            sphere.x + x, sphere.y + y, sphere.z + z));
        ranges.Add((__instance.IndicesStart, __instance.IndicesEnd - __instance.IndicesStart));
    }

    internal static ReadOnlySpan<TerrainDrawBounds> BoundsFor(int[] starts, int[] sizes, int count)
    {
        if (!scope || !complete || pool == null || !ReferenceEquals(starts, pool.indicesStartsByte) ||
            !ReferenceEquals(sizes, pool.indicesSizes) || count != bounds.Count || starts.Length / 2 < count || sizes.Length < count)
            return default;
        for (int i = 0; i < count; i++)
            if (starts[i * 2 + 1] != 0 || (uint)starts[i * 2] != (uint)ranges[i].First * 4u || sizes[i] != ranges[i].Count)
                return default;
        return CollectionsMarshal.AsSpan(bounds);
    }
}
