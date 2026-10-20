using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>Optimum's dedicated chisel LOD pools, rebuilt with side tables rather than injected game fields.
/// Both meshes are retained so toggling the option or distance changes rendering immediately without retessellating.</summary>
internal static class ChiselMeshLod
{
    private sealed class Marker { }
    private sealed class Proxy { internal MeshData? Source, Mesh; }
    private sealed class Pools
    {
        internal MeshData[][][]? CenterGeneration;
        internal readonly Dictionary<MeshData[][][], MeshData[][][]> Grids = new();
        internal void Clear()
        {
            foreach (var grid in Grids.Values) foreach (var level in grid) foreach (var pass in level)
                if (pass != null) foreach (var mesh in pass) mesh?.Clear();
        }
        internal void Discard()
        {
            foreach (var grid in Grids.Values) foreach (var level in grid) foreach (var pass in level)
                if (pass != null) foreach (var mesh in pass) mesh?.Dispose();
            Grids.Clear();
        }
    }
    private static ConditionalWeakTable<ChunkTesselator, Pools> pools = new();
    private static ConditionalWeakTable<BlockEntity, Proxy> proxies = new();
    private static ConditionalWeakTable<TesselatedChunkPart, Marker> parts = new();
    internal static bool Owns(TesselatedChunkPart part) => parts.TryGetValue(part, out _);
    private static ConditionalWeakTable<ModelDataPoolLocation, Marker> locations = new();
    [ThreadStatic] private static bool route, packing;
    private static readonly AccessTools.FieldRef<ChunkTesselator, MeshData[][][]> Current = AccessTools.FieldRefAccess<ChunkTesselator, MeshData[][][]>("currentModeldataByRenderPassByLodLevel");
    private static readonly AccessTools.FieldRef<ChunkTesselator, MeshData[][][]> Center = AccessTools.FieldRefAccess<ChunkTesselator, MeshData[][][]>("centerModeldataByRenderPassByLodLevel");
    private static readonly AccessTools.FieldRef<FrustumCulling, BlockPos> Player = AccessTools.FieldRefAccess<FrustumCulling, BlockPos>("playerPos");
    private static readonly MethodInfo Add = AccessTools.Method(typeof(ITerrainMeshPool), nameof(ITerrainMeshPool.AddMeshData), [typeof(MeshData), typeof(int)])!;
    private static readonly MethodInfo AddColor = AccessTools.Method(typeof(ITerrainMeshPool), nameof(ITerrainMeshPool.AddMeshData), [typeof(MeshData), typeof(ColorMapData), typeof(int)])!;
    private static MethodInfo? majority;
    private static MethodInfo Populate() => AccessTools.Method(typeof(ChunkTesselator), "populateTesselatedChunkPart")!;
    private static MethodInfo Store() => AccessTools.Method(typeof(TesselatedChunkPart), "AddModelAndStoreLocation")!;
    private static readonly MethodInfo InitializePools = AccessTools.Method(typeof(ChunkTesselator), "InitialiseRenderPassPools",
        [typeof(MeshData[]), typeof(EnumChunkRenderPass), typeof(int)])!;

    internal static PerformanceFeature Create(Assembly survival)
    {
        MethodInfo? target = null;
        return new("chisel-mesh-lod", s => s.ChiselMeshLod, () =>
        {
            Type micro = survival.GetType("Vintagestory.GameContent.BlockEntityMicroBlock", true)!;
            target = AccessTools.Method(micro, "OnTesselation", [typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);
            majority = AccessTools.Method(micro, "GetMajorityMaterialId");
            if (target == null || majority == null || PatchProcessor.GetOriginalInstructions(target).Count(i => i.Calls(Add) || i.Calls(AddColor)) != 2)
                throw new InvalidOperationException("Chisel tessellation anchors changed.");
            _ = Populate(); _ = Store();
            if (InitializePools == null) throw new MissingMethodException("ChunkTesselator.InitialiseRenderPassPools");
        }, harmony =>
        {
            harmony.Patch(target!, transpiler: new HarmonyMethod(typeof(ChiselMeshLod), nameof(MicroTranspiler)));
            foreach (string name in new[] { "GetPoolForPass", "GetMeshPoolForPass" })
                harmony.Patch(AccessTools.Method(typeof(ChunkTesselator), name), prefix: new HarmonyMethod(typeof(ChiselMeshLod), nameof(SwapPool)),
                    finalizer: new HarmonyMethod(typeof(ChiselMeshLod), nameof(RestorePool)));
            harmony.Patch(AccessTools.Method(typeof(ChunkTesselator), "NowProcessChunk"), prefix: new HarmonyMethod(typeof(ChiselMeshLod), nameof(BeginChunk)));
            harmony.Patch(Populate(), postfix: new HarmonyMethod(typeof(ChiselMeshLod), nameof(PackChisel)));
            harmony.Patch(Store(), prefix: new HarmonyMethod(typeof(ChiselMeshLod), nameof(BeforeStore)), postfix: new HarmonyMethod(typeof(ChiselMeshLod), nameof(AfterStore)));
            harmony.Patch(AccessTools.Method(typeof(ModelDataPoolLocation), nameof(ModelDataPoolLocation.IsVisible)),
                prefix: new HarmonyMethod(typeof(ChiselMeshLod), nameof(Visible)));
        }, WorldLeft: () => { pools = new(); proxies = new(); parts = new(); locations = new(); });
    }
    private static IEnumerable<CodeInstruction> MicroTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Add) || instruction.Calls(AddColor))
            {
                bool color = instruction.Calls(AddColor);
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ChiselMeshLod), color ? nameof(AddWithColor) : nameof(AddPair));
            }
            yield return instruction;
        }
    }
    private static MeshData? ProxyMesh(BlockEntity owner)
    {
        if (owner.Api is not ICoreClientAPI api || owner.Block?.Attributes?["canChisel"].AsBool(true) == false) return null;
        int material = (int)majority!.Invoke(owner, [null])!;
        if (material <= 0) return null;
        MeshData? source = api.TesselatorManager.GetDefaultBlockMesh(api.World.GetBlock(material));
        if (source == null || source.VerticesCount == 0) return null;
        Proxy proxy = proxies.GetOrCreateValue(owner);
        if (!ReferenceEquals(proxy.Source, source))
        {
            proxy.Mesh?.Dispose(); proxy.Source = source; proxy.Mesh = source.Clone();
        }
        return proxy.Mesh;
    }
    private static void AddPair(ITerrainMeshPool mesher, MeshData full, int lod, BlockEntity owner)
    {
        MeshData? far = ProxyMesh(owner);
        if (far == null) { mesher.AddMeshData(full, lod); return; }
        bool previous = route; route = true;
        try { mesher.AddMeshData(full, 2); mesher.AddMeshData(far, 3); }
        finally { route = previous; }
    }
    private static void AddWithColor(ITerrainMeshPool mesher, MeshData full, ColorMapData color, int lod, BlockEntity owner)
    {
        MeshData? far = ProxyMesh(owner);
        if (far == null) { mesher.AddMeshData(full, color, lod); return; }
        bool previous = route; route = true;
        try { mesher.AddMeshData(full, color, 2); mesher.AddMeshData(far, color, 3); }
        finally { route = previous; }
    }
    private static void BeginChunk(ChunkTesselator __instance)
    { if (pools.TryGetValue(__instance, out var state)) state.Clear(); }
    private static void SwapPool(ChunkTesselator __instance, out MeshData[][][]? __state)
    {
        __state = null;
        if (!route) return;
        Pools state = pools.GetOrCreateValue(__instance);
        if (!ReferenceEquals(state.CenterGeneration, Center(__instance)))
        {
            state.Discard(); state.CenterGeneration = Center(__instance);
        }
        MeshData[][][] source = Current(__instance);
        if (!state.Grids.TryGetValue(source, out var extra)) state.Grids.Add(source, extra = EmptyGrid(__instance, source));
        __state = source; Current(__instance) = extra;
    }
    private static void RestorePool(ChunkTesselator __instance, MeshData[][][]? __state)
    { if (__state != null) Current(__instance) = __state; }
    private static MeshData[][][] EmptyGrid(ChunkTesselator tesselator, MeshData[][][] source)
    {
        var grid = new MeshData[source.Length][][];
        for (int level = 0; level < source.Length; level++)
        {
            grid[level] = new MeshData[source[level].Length][];
            for (int pass = 0; pass < source[level].Length; pass++)
            {
                if (source[level][pass] == null) continue;
                grid[level][pass] = new MeshData[source[level][pass].Length];
                // EmptyClone sizes vertex buffers from the used count, which is zero in a fresh pool.
                // Stock tessellation doubles capacity on growth; zero capacity can never grow.
                // Use the stock initializer to retain positive capacity and each render pass's custom data.
                InitializePools.Invoke(tesselator, [grid[level][pass], (EnumChunkRenderPass)pass, 1024]);
            }
        }
        return grid;
    }
    private static void PackChisel(ChunkTesselator __instance, MeshData[][][] modeldataByRenderPassByLodLevel,
        ref TesselatedChunkPart[] tessChunkParts, ref int __result)
    {
        if (packing || !pools.TryGetValue(__instance, out var state) || !state.Grids.TryGetValue(modeldataByRenderPassByLodLevel, out var grid)) return;
        int vertices = 0;
        foreach (var pass in grid[2]) if (pass != null) foreach (var mesh in pass) vertices += mesh.VerticesCount;
        if (vertices == 0) return;
        object?[] args = [grid, null];
        packing = true;
        try { Populate().Invoke(__instance, args); }
        finally { packing = false; }
        var extra = (TesselatedChunkPart[])args[1]!;
        foreach (var part in extra) parts.Add(part, new Marker());
        var merged = new TesselatedChunkPart[tessChunkParts.Length + extra.Length];
        Array.Copy(tessChunkParts, merged, tessChunkParts.Length); Array.Copy(extra, 0, merged, tessChunkParts.Length, extra.Length);
        tessChunkParts = merged; __result += vertices;
    }
    private static void BeforeStore(List<ModelDataPoolLocation> locations, out int __state) => __state = locations.Count;
    private static void AfterStore(TesselatedChunkPart __instance, List<ModelDataPoolLocation> locations, int __state)
    {
        if (locations.Count > __state && parts.TryGetValue(__instance, out var marker))
            ChiselMeshLod.locations.Add(locations[^1], marker);
    }
    private static bool Visible(ModelDataPoolLocation __instance, EnumFrustumCullMode mode, FrustumCulling culler, ref bool __result)
    {
        if (!locations.TryGetValue(__instance, out _)) return true;
        RendererSettings? settings = PerformancePatchSet.Current;
        var player = Player(culler);
        double squared = player.HorDistanceSqTo(__instance.FrustumCullSphere.x, __instance.FrustumCullSphere.z);
        bool far = settings?.ChiselMeshLod == true && squared > (double)settings.ChiselLodDistance * settings.ChiselLodDistance;
        bool selected = __instance.LodLevel == (far ? 3 : 2);
        __result = selected && !__instance.Hide && __instance.CullVisible[ModelDataPoolLocation.VisibleBufIndex];
        if (__result)
            __result = mode is EnumFrustumCullMode.CullInstantShadowPassNear or EnumFrustumCullMode.CullInstantShadowPassFar
                ? culler.InFrustumShadowPass(__instance.FrustumCullSphere)
                : culler.InFrustum(__instance.FrustumCullSphere) && (mode != EnumFrustumCullMode.CullNormal || squared < culler.ViewDistanceSq);
        if (mode == EnumFrustumCullMode.CullNormal) __instance.FrustumVisible = __result;
        return false;
    }
}
