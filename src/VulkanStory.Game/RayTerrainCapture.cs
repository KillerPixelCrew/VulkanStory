using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>Retains accepted terrain independently of raster visibility, using VulkanStory-owned sidecars.</summary>
internal static class RayTerrainCapture
{
    private sealed record Allocation(GameGraphicsAdapter Owner, ulong Identity);
    private sealed record Capture(GameGraphicsAdapter Owner, int LocationCount, RayTracingGeometryUpload Upload);
    private static ConditionalWeakTable<ModelDataPoolLocation, Allocation> allocations = new();
    private static ProcessRuntime? runtime;
    private static long nextIdentity;
    private static readonly MethodInfo Add = AccessTools.Method(typeof(TesselatedChunkPart), "AddModelAndStoreLocation",
        [typeof(MeshDataPoolManager), typeof(List<ModelDataPoolLocation>), typeof(MeshData), typeof(Vec3i),
         typeof(int), typeof(Sphere), typeof(Bools), typeof(int)]) ?? throw new MissingMethodException("Terrain allocation seam is missing.");
    private static readonly MethodInfo RemoveLocation = AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation),
        [typeof(ModelDataPoolLocation)]) ?? throw new MissingMethodException("Terrain removal seam is missing.");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, int> Atlas =
        AccessTools.FieldRefAccess<TesselatedChunkPart, int>("atlasNumber");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, EnumChunkRenderPass> Pass =
        AccessTools.FieldRefAccess<TesselatedChunkPart, EnumChunkRenderPass>("pass");
    private static readonly AccessTools.FieldRef<MeshDataPool, List<ModelDataPoolLocation>> PoolLocations =
        AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, ICoreClientAPI> PoolApi =
        AccessTools.FieldRefAccess<MeshDataPoolManager, ICoreClientAPI>("capi");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, MeshData?> Lod0 =
        AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData?>("modelDataLod0");
    private static readonly AccessTools.FieldRef<TesselatedChunkPart, MeshData?> Lod1 =
        AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData?>("modelDataLod1");

    internal static void Validate()
    {
        if (Add.ReturnType != typeof(void) || Add.IsStatic || Add.GetMethodBody() == null ||
            RemoveLocation.ReturnType != typeof(void) || RemoveLocation.IsStatic || RemoveLocation.GetMethodBody() == null)
            throw new InvalidOperationException("Original terrain allocation/removal signatures changed.");
        if (AccessTools.Field(typeof(TesselatedChunkPart), "atlasNumber")?.FieldType != typeof(int) ||
            AccessTools.Field(typeof(TesselatedChunkPart), "pass")?.FieldType != typeof(EnumChunkRenderPass))
            throw new InvalidOperationException("Original terrain material metadata changed.");
        if (AccessTools.Field(typeof(MeshDataPool), "poolLocations")?.FieldType != typeof(List<ModelDataPoolLocation>))
            throw new InvalidOperationException("Original terrain location ownership changed.");
        if (AccessTools.Field(typeof(MeshDataPoolManager), "capi")?.FieldType != typeof(ICoreClientAPI))
            throw new InvalidOperationException("Original terrain world ownership changed.");
    }

    internal static void Install(Harmony harmony, ProcessRuntime owner)
    {
        if (runtime != null) throw new InvalidOperationException("Ray terrain capture already has an owner.");
        runtime = owner;
        harmony.Patch(Add,
            prefix: new HarmonyMethod(typeof(RayTerrainCapture), nameof(BeforeAdd)),
            postfix: new HarmonyMethod(typeof(RayTerrainCapture), nameof(AfterAdd)));
        harmony.Patch(RemoveLocation, postfix: new HarmonyMethod(typeof(RayTerrainCapture), nameof(AfterRemove)));
    }

    internal static void Detach(ProcessRuntime owner)
    {
        if (!ReferenceEquals(runtime, owner)) return;
        runtime = null;
        allocations = new ConditionalWeakTable<ModelDataPoolLocation, Allocation>();
    }

    private static GameGraphicsAdapter? Graphics()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        return ScreenManager.Platform is ClientPlatformWindows platform && runtime.TrySession(platform, out var session)
            ? session.Graphics : null;
    }

    private static void BeforeAdd(TesselatedChunkPart __instance, MeshDataPoolManager pools, List<ModelDataPoolLocation> locations,
        MeshData modeldata, Vec3i modelOrigin, int dimension, int lodLevel, out Capture? __state)
    {
        __state = null;
        var owner = Graphics();
        if (owner?.RaySceneCollectionEnabled != true || modeldata.VerticesCount == 0 || modeldata.IndicesCount == 0) return;
        int retainedLod = Lod0(__instance) is { VerticesCount: > 0 } ? 0 : Lod1(__instance) is { VerticesCount: > 0 } ? 1 : 3;
        // The game's level 2 stream contains details omitted from the far model;
        // retain it with the finest available main model, not every coincident LOD.
        if (lodLevel != 2 && lodLevel != retainedLod) return;
        int count = modeldata.VerticesCount;
        if (modeldata.xyz == null || modeldata.xyz.Length < checked(count * 3) ||
            modeldata.Indices == null || modeldata.Indices.Length < modeldata.IndicesCount)
            throw new InvalidOperationException("Accepted terrain mesh has an incomplete position/index upload.");
        var vertices = new RayTracingVertex[count];
        var indices = new uint[modeldata.IndicesCount];
        int intsStride = Math.Max(1, (modeldata.CustomInts?.InterleaveStride ?? 4) / sizeof(int));
        int floatsStride = Math.Max(2, (modeldata.CustomFloats?.InterleaveStride ?? 8) / sizeof(float));
        int shortsStride = Math.Max(2, (modeldata.CustomShorts?.InterleaveStride ?? 4) / sizeof(short));
        bool topsoil = Pass(__instance) == EnumChunkRenderPass.TopSoil;
        for (int i = 0; i < count; i++)
        {
            vertices[i] = new RayTracingVertex
            {
                Position = new Vector3(modeldata.xyz[i * 3], modeldata.xyz[i * 3 + 1], modeldata.xyz[i * 3 + 2]),
                PreviousPosition = new Vector3(modeldata.xyz[i * 3], modeldata.xyz[i * 3 + 1], modeldata.xyz[i * 3 + 2]),
                Flags = (uint)Value(modeldata.Flags, i),
                PackedNormal = (uint)Value(modeldata.Normals, i),
                Uv = new Vector2(Value(modeldata.Uv, i * 2), Value(modeldata.Uv, i * 2 + 1)),
                Color = Color(modeldata.Rgba, i * 4),
                MaterialData = (uint)Value(modeldata.CustomInts?.Values, i * intsStride),
                AuxiliaryData = topsoil
                    ? (uint)((PackedUvFlag(modeldata.CustomShorts?.Values, i * shortsStride)) |
                        (PackedUvFlag(modeldata.CustomShorts?.Values, i * shortsStride + 1) << 1))
                    : intsStride > 1 ? (uint)Value(modeldata.CustomInts?.Values, i * intsStride + 1) : 0,
                SecondaryUv = topsoil
                    ? new Vector2(PackedUv(modeldata.CustomShorts?.Values, i * shortsStride),
                        PackedUv(modeldata.CustomShorts?.Values, i * shortsStride + 1))
                    : new Vector2(Value(modeldata.CustomFloats?.Values, i * floatsStride),
                        Value(modeldata.CustomFloats?.Values, i * floatsStride + 1)),
            };
        }
        for (int i = 0; i < indices.Length; i++) indices[i] = checked((uint)modeldata.Indices[i]);
        RayTracingSurface surface = Pass(__instance) switch
        {
            EnumChunkRenderPass.OpaqueNoCull or EnumChunkRenderPass.OpaqueWaterPlant => RayTracingSurface.Cutout,
            EnumChunkRenderPass.BlendNoCull => RayTracingSurface.AlphaBlend,
            EnumChunkRenderPass.Transparent => RayTracingSurface.Transparent,
            EnumChunkRenderPass.Liquid => RayTracingSurface.Liquid,
            EnumChunkRenderPass.TopSoil => RayTracingSurface.Topsoil,
            _ => RayTracingSurface.Opaque,
        };
        __state = new Capture(owner, locations.Count, new RayTracingGeometryUpload(owner.RayWorldIdentity(PoolApi(pools).World), vertices, indices,
            modelOrigin.X, modelOrigin.Y, modelOrigin.Z, dimension, Atlas(__instance), surface, lodLevel));
    }

    private static void AfterAdd(List<ModelDataPoolLocation> locations, Capture? __state)
    {
        if (__state == null || locations.Count == __state.LocationCount) return;
        if (locations.Count != __state.LocationCount + 1)
            throw new InvalidOperationException("Terrain allocation produced an unexpected location count.");
        var location = locations[__state.LocationCount];
        ulong identity = checked((ulong)Interlocked.Increment(ref nextIdentity));
        __state.Owner.SetRayGeometry(identity, __state.Upload);
        allocations.Add(location, new Allocation(__state.Owner, identity));
    }

    private static void AfterRemove(MeshDataPool __instance, ModelDataPoolLocation location)
    {
        if (runtime?.Routing.RoutingEnabled != true || !allocations.TryGetValue(location, out Allocation? allocation)) return;
        if (PoolLocations(__instance).Contains(location)) return;
        allocation.Owner.RemoveRayGeometry(allocation.Identity);
        allocations.Remove(location);
    }

    private static int Value(int[]? values, int index) => values != null && index < values.Length ? values[index] : 0;
    private static float Value(float[]? values, int index) => values != null && index < values.Length ? values[index] : 0;
    private static float PackedUv(short[]? values, int index) => values != null && index < values.Length
        ? unchecked((ushort)values[index]) * (2f / ushort.MaxValue) : 0f;
    private static int PackedUvFlag(short[]? values, int index) => values != null && index < values.Length
        ? ((int)(unchecked((ushort)values[index]) * (65536f / ushort.MaxValue)) & 1) : 0;
    private static uint Color(byte[]? values, int index) => values != null && index + 3 < values.Length
        ? (uint)(values[index] | values[index + 1] << 8 | values[index + 2] << 16 | values[index + 3] << 24) : 0xffffffffu;
}
