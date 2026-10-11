using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Plane = Vintagestory.API.Client.Plane;

namespace VulkanStory.Game.Performance;

/// <summary>Optimum's parallel plane tests with the game's double precision and comparison boundaries.
/// Plane vectors refresh after every frustum calculation, including rotations and FOV changes at a stationary camera.</summary>
internal static class SimdFrustumTests
{
    private readonly record struct Group(Vector<double> X, Vector<double> Y, Vector<double> Z, Vector<double> D,
        Vector<double> SignX, Vector<double> SignY, Vector<double> SignZ, Vector<long> Mask5, Vector<long> Mask6);
    private sealed class State { internal Group[]? Groups; }
    private static readonly ConditionalWeakTable<FrustumCulling, State> states = new();
    private static readonly AccessTools.FieldRef<FrustumCulling, Plane[]> Planes = AccessTools.FieldRefAccess<FrustumCulling, Plane[]>("frustum");
    private static readonly AccessTools.FieldRef<FrustumCulling, BlockPos> Player = AccessTools.FieldRefAccess<FrustumCulling, BlockPos>("playerPos");
    internal static PerformanceFeature Create() => new("simd-frustum-tests", s => s.SimdFrustumTests, () =>
    {
        foreach (string name in new[] { "SphereInFrustum", "InFrustum", "InFrustumAndRange", "InFrustumShadowPass" })
            if (AccessTools.Method(typeof(FrustumCulling), name) == null) throw new MissingMethodException(typeof(FrustumCulling).FullName, name);
    }, harmony =>
    {
        harmony.Patch(AccessTools.Method(typeof(FrustumCulling), "CalcFrustumEquations", [typeof(double[])]),
            postfix: new HarmonyMethod(typeof(SimdFrustumTests), nameof(Refresh)));
        harmony.Patch(AccessTools.Method(typeof(FrustumCulling), "SphereInFrustum"), prefix: new HarmonyMethod(typeof(SimdFrustumTests), nameof(Sphere)));
        harmony.Patch(AccessTools.Method(typeof(FrustumCulling), "InFrustum"), prefix: new HarmonyMethod(typeof(SimdFrustumTests), nameof(Box)));
        harmony.Patch(AccessTools.Method(typeof(FrustumCulling), "InFrustumAndRange"), prefix: new HarmonyMethod(typeof(SimdFrustumTests), nameof(Range)));
        harmony.Patch(AccessTools.Method(typeof(FrustumCulling), "InFrustumShadowPass"), prefix: new HarmonyMethod(typeof(SimdFrustumTests), nameof(Shadow)));
    });

    private static void Refresh(FrustumCulling __instance)
    {
        if (!Vector.IsHardwareAccelerated || PerformancePatchSet.Current?.SimdFrustumTests != true) return;
        Plane[] planes = Planes(__instance);
        int width = Vector<double>.Count;
        var groups = new Group[(6 + width - 1) / width];
        Span<double> x = stackalloc double[width], y = stackalloc double[width], z = stackalloc double[width], d = stackalloc double[width];
        Span<double> sx = stackalloc double[width], sy = stackalloc double[width], sz = stackalloc double[width];
        Span<long> mask5 = stackalloc long[width], mask6 = stackalloc long[width];
        for (int group = 0; group < groups.Length; group++)
        {
            for (int lane = 0; lane < width; lane++)
            {
                int index = group * width + lane;
                Plane plane = index < 6 ? planes[index] : default;
                x[lane] = plane.normalX; y[lane] = plane.normalY; z[lane] = plane.normalZ; d[lane] = plane.D;
                sx[lane] = plane.normalX > 0 ? 1 : -1;
                sy[lane] = plane.normalY > 0 ? 1 : -1;
                sz[lane] = plane.normalZ > 0 ? 1 : -1;
                mask5[lane] = index < 5 ? -1 : 0; mask6[lane] = index < 6 ? -1 : 0;
            }
            groups[group] = new(new(x), new(y), new(z), new(d), new(sx), new(sy), new(sz), new(mask5), new(mask6));
        }
        Volatile.Write(ref states.GetOrCreateValue(__instance).Groups, groups);
    }
    private static Group[]? Snapshot(FrustumCulling owner)
    {
        if (PerformancePatchSet.Current?.SimdFrustumTests != true || !Vector.IsHardwareAccelerated) return null;
        return states.TryGetValue(owner, out var state) ? Volatile.Read(ref state.Groups) : null;
    }
    private static bool Sphere(FrustumCulling __instance, double x, double y, double z, double radius, ref bool __result)
    {
        if (Snapshot(__instance) is not { } groups) return true;
        __result = true;
        foreach (Group group in groups)
        {
            // Match scalar operation order; do not fuse multiplies or truncate coefficients to floats.
            var distance = (group.X * new Vector<double>(x) + group.Y * new Vector<double>(y)) + group.Z * new Vector<double>(z) + group.D;
            var rejected = Vector.LessThanOrEqual(distance, new Vector<double>(-radius)) & group.Mask6;
            if (!Vector.EqualsAll(rejected, Vector<long>.Zero)) { __result = false; break; }
        }
        return false;
    }
    private static bool Outside(Group[] groups, Vintagestory.API.MathTools.Sphere sphere, bool five)
    {
        // The original divides the extents as floats, then adds them to double-precision positions.
        var rx = new Vector<double>((double)(sphere.radius / 1.7320508f));
        var ry = new Vector<double>((double)(sphere.radiusY / 1.7320508f));
        var rz = new Vector<double>((double)(sphere.radiusZ / 1.7320508f));
        foreach (Group group in groups)
        {
            var px = new Vector<double>(sphere.x) + group.SignX * rx;
            var py = new Vector<double>(sphere.y) + group.SignY * ry;
            var pz = new Vector<double>(sphere.z) + group.SignZ * rz;
            var distance = (px * group.X + py * group.Y) + pz * group.Z + group.D;
            var rejected = Vector.LessThan(distance, Vector<double>.Zero) & (five ? group.Mask5 : group.Mask6);
            if (!Vector.EqualsAll(rejected, Vector<long>.Zero)) return true;
        }
        return false;
    }
    private static bool Box(FrustumCulling __instance, Vintagestory.API.MathTools.Sphere sphere, ref bool __result)
    {
        if (Snapshot(__instance) is not { } groups) return true;
        __result = !Outside(groups, sphere, false); return false;
    }
    private static bool Shadow(FrustumCulling __instance, Vintagestory.API.MathTools.Sphere sphere, ref bool __result)
    {
        if (Snapshot(__instance) is not { } groups) return true;
        BlockPos player = Player(__instance);
        __result = !(Math.Abs((float)player.X - sphere.x) >= __instance.shadowRangeX) &&
            !(Math.Abs((float)player.Z - sphere.z) >= __instance.shadowRangeZ) && !Outside(groups, sphere, false);
        return false;
    }
    private static bool Range(FrustumCulling __instance, Vintagestory.API.MathTools.Sphere sphere, int lodLevel, ref bool __result)
    {
        if (Snapshot(__instance) is not { } groups) return true;
        if (Outside(groups, sphere, true)) { __result = false; return false; }
        double squared = Player(__instance).HorDistanceSqTo(sphere.x, sphere.z);
        __result = lodLevel switch
        {
            0 => __instance.lod0BiasSq > 0 && squared < __instance.lod0BiasSq + 1024f,
            1 => squared < __instance.ViewDistanceSq,
            2 => squared <= __instance.lod2BiasSq,
            3 => squared > __instance.lod2BiasSq && squared < __instance.ViewDistanceSq,
            _ => false,
        };
        return false;
    }
}
