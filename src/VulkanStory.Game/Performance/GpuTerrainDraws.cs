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
    private static bool gpuFrustum, snapshotValid, testFarPlane;
    private static FrustumCulling? activeCuller;
    private static int scopeThread;
    private static readonly AccessTools.FieldRef<FrustumCulling, Vintagestory.API.Client.Plane[]> FrustumPlanes =
        AccessTools.FieldRefAccess<FrustumCulling, Vintagestory.API.Client.Plane[]>("frustum");
    private static readonly AccessTools.FieldRef<FrustumCulling, BlockPos> Player =
        AccessTools.FieldRefAccess<FrustumCulling, BlockPos>("playerPos");
    private static readonly TerrainFrustumPlane[] planes = new TerrainFrustumPlane[6];
    private static readonly List<TerrainDrawBounds> bounds = new();
    private static readonly List<(int First, int Count)> ranges = new();
    private static MethodInfo? cullTarget, visibleTarget, removeTarget, rangeTarget, boxTarget;
    internal static bool Collecting => collecting && scopeThread == Environment.CurrentManagedThreadId;
    internal static ReadOnlySpan<TerrainFrustumPlane> Planes => gpuFrustum && snapshotValid && complete ? planes : default;

    private static MethodInfo Cull() => cullTarget ??= AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull),
        [typeof(FrustumCulling), typeof(EnumFrustumCullMode)])!;
    private static MethodInfo Visible() => visibleTarget ??= AccessTools.Method(typeof(ModelDataPoolLocation), nameof(ModelDataPoolLocation.IsVisible),
        [typeof(EnumFrustumCullMode), typeof(FrustumCulling)])!;
    private static MethodInfo Remove() => removeTarget ??= AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation),
        [typeof(ModelDataPoolLocation)])!;
    private static MethodInfo Range() => rangeTarget ??= AccessTools.Method(typeof(FrustumCulling), nameof(FrustumCulling.InFrustumAndRange),
        [typeof(Sphere), typeof(bool), typeof(int)])!;
    private static MethodInfo Box() => boxTarget ??= AccessTools.Method(typeof(FrustumCulling), nameof(FrustumCulling.InFrustum), [typeof(Sphere)])!;

    internal static PerformanceFeature Create() => new("gpu-terrain-draws", settings => settings.GpuTerrainDrawPreparation, () =>
    {
        if (Cull() == null || Visible() == null || Remove() == null || Range() == null || Box() == null ||
            PatchProcessor.GetOriginalInstructions(Cull()).Count(instruction => instruction.Calls(Visible())) != 1)
            throw new InvalidOperationException("Terrain pool visibility/range producer changed.");
    }, harmony =>
    {
        harmony.Patch(Cull(), prefix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(BeforeCull)),
            postfix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(AfterCull)),
            finalizer: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(FinishCull)));
        harmony.Patch(Visible(), prefix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(BeforeVisible)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(AfterVisible)) { priority = Priority.Last });
        harmony.Patch(Range(), prefix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(RangeWithoutPlanes)) { priority = Priority.First });
        harmony.Patch(Box(), prefix: new HarmonyMethod(typeof(GpuTerrainDraws), nameof(BoxWithoutPlanes)) { priority = Priority.First });
        harmony.Patch(Remove(), prefix: new HarmonyMethod(typeof(TerrainOcclusionCull), nameof(TerrainOcclusionCull.InvalidateDepth)));
        armed = true;
    }, Uninstall: () => { armed = false; EndScope(); }, WorldLeft: () => { EndScope(); ResidentTerrain.Reset(); });

    internal static void BeginScope(bool available)
    {
        EndScope();
        scope = armed && available && PerformancePatchSet.Current is
            { TerrainOcclusionCull: true, GpuTerrainDrawPreparation: true };
        scopeThread = Environment.CurrentManagedThreadId;
        // A mod replacing these policies owns its result. Only our stock-compatible
        // patches may share the plane bypass; bounds/range pairing still enables GPU occlusion.
        gpuFrustum = scope && !new[] { Cull(), Visible(), Range(), Box() }.Any(method =>
            Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true);
    }

    internal static void EndScope()
    {
        scope = collecting = complete = false;
        gpuFrustum = snapshotValid = false; activeCuller = null;
        pool = null; bounds.Clear(); ranges.Clear();
    }

    private static void BeforeCull(MeshDataPool __instance, FrustumCulling frustumCuller, EnumFrustumCullMode frustumCullMode)
    {
        if (scopeThread != Environment.CurrentManagedThreadId) return;
        pool = null; bounds.Clear(); ranges.Clear(); complete = false;
        collecting = scope && frustumCullMode == EnumFrustumCullMode.CullNormal;
        if (collecting) pool = __instance;
        activeCuller = frustumCuller; snapshotValid = false;
        if (Collecting && gpuFrustum && FrustumPlanes(frustumCuller) is { Length: 6 } source)
        {
            snapshotValid = true;
            for (int i = 0; i < 6; i++)
            {
                var plane = source[i];
                planes[i] = new TerrainFrustumPlane(plane.normalX, plane.normalY, plane.normalZ, plane.D);
                snapshotValid &= double.IsFinite(plane.normalX) && double.IsFinite(plane.normalY) &&
                    double.IsFinite(plane.normalZ) && double.IsFinite(plane.D);
            }
        }
    }

    private static void AfterCull()
    {
        if (scopeThread != Environment.CurrentManagedThreadId) return;
        complete = collecting; collecting = false;
    }
    private static Exception? FinishCull(Exception? __exception)
    {
        if (scopeThread != Environment.CurrentManagedThreadId) return __exception;
        collecting = false;
        if (__exception != null) { pool = null; complete = false; }
        return __exception;
    }

    private static void AfterVisible(ModelDataPoolLocation __instance, bool __result)
    {
        if (!Collecting || !__result) return;
        if (ResidentTerrain.IsResident(__instance)) return;
        // Float extents match Plane.AABBisOutside; world coordinates remain double until upload.
        if (bounds.Count >= 4096) { complete = false; collecting = false; pool = null; return; }
        Sphere sphere = __instance.FrustumCullSphere;
        double x = sphere.radius / 1.7320508f, y = sphere.radiusY / 1.7320508f, z = sphere.radiusZ / 1.7320508f;
        bounds.Add(new TerrainDrawBounds(sphere.x - x, sphere.y - y, sphere.z - z,
            sphere.x + x, sphere.y + y, sphere.z + z, testFarPlane));
        ranges.Add((__instance.IndicesStart, __instance.IndicesEnd - __instance.IndicesStart));
    }

    private static void BeforeVisible() { if (Collecting) testFarPlane = false; }

    private static bool BypassPlanes(FrustumCulling culler) => Collecting && gpuFrustum && snapshotValid &&
        ReferenceEquals(culler, activeCuller);

    private static bool BoxWithoutPlanes(FrustumCulling __instance, ref bool __result)
    {
        if (!BypassPlanes(__instance)) return true;
        testFarPlane = true;
        __result = true;
        return false;
    }

    private static bool RangeWithoutPlanes(FrustumCulling __instance, Sphere sphere, int lodLevel, ref bool __result)
    {
        if (!BypassPlanes(__instance)) return true;
        // nowVisible is unused by the stock implementation. Keep all range comparisons
        // and their float/double promotion exactly as the stock CPU path.
        double squared = Player(__instance).HorDistanceSqTo(sphere.x, sphere.z);
        __result = lodLevel switch
        {
            0 => __instance.lod0BiasSq > 0f && squared < (double)(__instance.lod0BiasSq + 1024f),
            1 => squared < __instance.ViewDistanceSq,
            2 => squared <= __instance.lod2BiasSq,
            3 => squared > __instance.lod2BiasSq && squared < __instance.ViewDistanceSq,
            _ => false,
        };
        return false;
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
