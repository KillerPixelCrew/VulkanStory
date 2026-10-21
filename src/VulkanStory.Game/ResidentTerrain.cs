using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game;

// Game identities and stock publication policy stay on this side of the renderer boundary.
internal static class ResidentTerrain
{
    internal sealed class Manager(ChunkRenderer renderer, int pass, int atlas)
    {
        internal readonly ChunkRenderer Renderer = renderer;
        internal readonly int Pass = pass, Atlas = atlas;
        internal readonly List<Pool> Pools = new();
        internal int ResidentMesh, CompatPools;
        internal ulong MainFrame = ulong.MaxValue;
        internal int MainEye;
    }
    internal sealed class Pool(Manager manager, VAO mesh, Vec3i origin, int dimension)
    {
        internal readonly Manager Manager = manager;
        internal readonly VAO Mesh = mesh;
        internal readonly Vec3i Origin = origin.Clone();
        internal readonly int Dimension = dimension;
        internal readonly HashSet<ModelDataPoolLocation> Parts = new();
        internal bool Refused = dimension != 0;
    }
    internal sealed class Part(Pool pool, Slot slot, TerrainPartDesc desc)
    {
        internal readonly Pool Pool = pool;
        internal readonly Slot Slot = slot;
        internal TerrainPartDesc Desc = desc;
        internal int Handle;
        internal ulong FrustumFrame = ulong.MaxValue;
        internal int FrustumEye;
    }
    internal sealed class Slot(int index, Bools visibility)
    {
        internal readonly int Index = index;
        internal readonly Bools Visibility = visibility;
        internal int Parts;
    }
    private readonly record struct Publication(Manager? Manager, Bools? Visibility, int Lod, bool Compat);
    [ThreadStatic] private static Publication publication;
    [ThreadStatic] internal static bool ParityActive;
    internal static readonly bool ParityRequested = Environment.GetEnvironmentVariable("VULKANSTORY_TERRAIN_PARITY") == "1" ||
        (HeadlessHarnessOptions.Enabled && HeadlessHarnessOptions.Scenario?.Actions.Any(action =>
            action.Kind == HeadlessScenarioActionKind.Assert && action.Field is "terrainParityChecks" or "terrainParityMismatches") == true);
    private static readonly ConditionalWeakTable<MeshDataPoolManager, Manager> Managers = new();
    private static readonly ConditionalWeakTable<MeshDataPool, Pool> Pools = new();
    private static readonly ConditionalWeakTable<VAO, Pool> MeshPools = new();
    private static readonly ConditionalWeakTable<ModelDataPoolLocation, Part> Parts = new();
    private static readonly ConditionalWeakTable<Bools, Slot> Slots = new();
    private static readonly Dictionary<int, Slot> ActiveSlots = new();
    private static int nextSlot;
    private static readonly AccessTools.FieldRef<MeshDataPool, MeshRef> PoolMesh = AccessTools.FieldRefAccess<MeshDataPool, MeshRef>("modelRef");
    private static readonly AccessTools.FieldRef<MeshDataPool, Vec3i> PoolOrigin = AccessTools.FieldRefAccess<MeshDataPool, Vec3i>("poolOrigin");
    private static readonly AccessTools.FieldRef<MeshDataPool, int> Dimension = AccessTools.FieldRefAccess<MeshDataPool, int>("dimensionId");
    private static readonly AccessTools.FieldRef<MeshDataPoolManager, FrustumCulling> Culler = AccessTools.FieldRefAccess<MeshDataPoolManager, FrustumCulling>("frustumCuller");
    private static readonly AccessTools.FieldRef<FrustumCulling, Vintagestory.API.Client.Plane[]> Planes = AccessTools.FieldRefAccess<FrustumCulling, Vintagestory.API.Client.Plane[]>("frustum");
    private static readonly AccessTools.FieldRef<FrustumCulling, BlockPos> Player = AccessTools.FieldRefAccess<FrustumCulling, BlockPos>("playerPos");
    private static readonly AccessTools.FieldRef<ClientChunk, ModelDataPoolLocation[]> Centers = AccessTools.FieldRefAccess<ClientChunk, ModelDataPoolLocation[]>("centerModelPoolLocations");
    private static readonly AccessTools.FieldRef<ClientChunk, ModelDataPoolLocation[]> Edges = AccessTools.FieldRefAccess<ClientChunk, ModelDataPoolLocation[]>("edgeModelPoolLocations");
    private static readonly (Type Type, string Name)[] Hooks =
    [
        (typeof(ChunkRenderer), "AddPoolsForAtlasAndPass"), (typeof(MeshDataPoolManager), "AddModel"),
        (typeof(TesselatedChunkPart), "AddModelAndStoreLocation"), (typeof(MeshDataPool), "InsertAt"),
        (typeof(MeshDataPool), "RemoveLocation"), (typeof(MeshDataPool), "FrustumCull"),
        (typeof(MeshDataPoolMasterManager), "RemoveDataPoolLocations"),
        (typeof(ClientChunk), "SetVisibility"), (typeof(ClientChunk), "SetPoolLocations"),
        (typeof(ClientChunk), "IsFrustumVisible")
    ];
    private static MethodInfo Target(Type type, string name) => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    internal static void Validate()
    {
        foreach (var hook in Hooks) _ = Target(hook.Type, hook.Name);
        _ = PoolMesh; _ = PoolOrigin; _ = Dimension; _ = Culler; _ = Planes; _ = Player; _ = Centers; _ = Edges;
    }
    internal static void Install(Harmony harmony)
    {
        void Patch(Type type, string name, string? prefix = null, string? postfix = null, string? finalizer = null) =>
            harmony.Patch(Target(type, name), prefix: prefix == null ? null : new HarmonyMethod(typeof(ResidentTerrain), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(ResidentTerrain), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ResidentTerrain), finalizer));
        Patch(typeof(ChunkRenderer), "AddPoolsForAtlasAndPass", postfix: nameof(RegisterManager));
        Patch(typeof(MeshDataPoolManager), "AddModel", prefix: nameof(BeforeAdd), finalizer: nameof(FinishPublication));
        Patch(typeof(TesselatedChunkPart), "AddModelAndStoreLocation", prefix: nameof(BeforePart), finalizer: nameof(FinishPublication));
        Patch(typeof(MeshDataPool), "InsertAt", postfix: nameof(AfterInsert));
        harmony.Patch(Target(typeof(MeshDataPool), "RemoveLocation"), prefix:
            new HarmonyMethod(typeof(Performance.TerrainOcclusionCull), nameof(Performance.TerrainOcclusionCull.InvalidateDepth)));
        Patch(typeof(MeshDataPool), "RemoveLocation", postfix: nameof(Removed));
        Patch(typeof(MeshDataPool), "FrustumCull", prefix: nameof(ReplayCompatPool));
        Patch(typeof(MeshDataPoolMasterManager), "RemoveDataPoolLocations", postfix: nameof(LocationsRemoved));
        Patch(typeof(ClientChunk), "SetVisibility", postfix: nameof(VisibilityChanged));
        Patch(typeof(ClientChunk), "SetPoolLocations", postfix: nameof(LocationsChanged));
        Patch(typeof(ClientChunk), "IsFrustumVisible", prefix: nameof(ReadChunkFrustum));
    }
    private static void RegisterManager(ChunkRenderer __instance, int atlas, EnumChunkRenderPass pass)
    {
        if (ChunkConsumerPatches.Adapter() == null) return;
        Managers.Add(__instance.poolsByRenderPass[(int)pass][atlas], new Manager(__instance, (int)pass, atlas));
    }
    private static void BeforeAdd(MeshDataPoolManager __instance, out Publication __state)
    {
        __state = publication;
        publication = publication with { Manager = Managers.TryGetValue(__instance, out var manager) ? manager : null };
    }
    private static void BeforePart(TesselatedChunkPart __instance, Bools cullVisible, int lodLevel, out Publication __state)
    {
        __state = publication;
        publication = publication with { Visibility = cullVisible, Lod = lodLevel, Compat = Performance.ChiselMeshLod.Owns(__instance) };
    }
    private static Exception? FinishPublication(Publication __state, Exception? __exception)
    { publication = __state; return __exception; }
    private static void AfterInsert(MeshDataPool __instance, Vec3i modelOrigin, ModelDataPoolLocation __result)
    { if (__result != null) Inserted(__instance, modelOrigin, __result); }

    // Publishes stock pool insertions into the resident part tables.
    internal static void Inserted(MeshDataPool pool, Vec3i modelOrigin, ModelDataPoolLocation location)
    {
        if (Parts.TryGetValue(location, out _) || publication.Manager is not { } manager ||
            PoolMesh(pool) is not VAO vao || ChunkConsumerPatches.Adapter() is not { } graphics) return;
        if (!Pools.TryGetValue(pool, out var retained))
        {
            retained = new Pool(manager, vao, PoolOrigin(pool) ?? modelOrigin, Dimension(pool));
            Pools.Add(pool, retained); MeshPools.Add(vao, retained); manager.Pools.Add(retained);
            if (retained.Refused) manager.CompatPools++;
            else if (manager.ResidentMesh == 0) manager.ResidentMesh = vao.VaoId;
        }
        // Stock reuses an empty pool at a new origin; its mesh identity stays the same.
        if (retained.Parts.Count == 0) retained.Origin.Set(PoolOrigin(pool) ?? modelOrigin);
        retained.Parts.Add(location);
        if (publication.Compat) Refuse(retained, graphics);
        if (publication.Visibility is not { } visibility) { Refuse(retained, graphics); return; }
        Slot slot = Slots.GetValue(visibility, value => new Slot(nextSlot++, value));
        if (slot.Parts++ == 0) ActiveSlots.Add(slot.Index, slot);
        Sphere sphere = location.FrustumCullSphere;
        var desc = new TerrainPartDesc(vao.VaoId, manager.Pass, manager.Atlas,
            (uint)location.IndicesStart, (uint)(location.IndicesEnd - location.IndicesStart),
            sphere.x, sphere.y, sphere.z, sphere.radius, sphere.radiusY, sphere.radiusZ, publication.Lod, slot.Index)
        {
            Hidden = location.Hide, PoolOriginX = retained.Origin.X,
            PoolOriginY = retained.Origin.Y, PoolOriginZ = retained.Origin.Z
        };
        var part = new Part(retained, slot, desc);
        Parts.Add(location, part);
        if (!retained.Refused)
        {
            part.Handle = graphics.AddResidentTerrainPart(desc);
            if (part.Handle == 0) Refuse(retained, graphics);
        }
    }
    private static void Refuse(Pool pool, GameGraphicsAdapter graphics)
    {
        if (!pool.Refused) pool.Manager.CompatPools++;
        pool.Refused = true;
        graphics.RemoveResidentTerrainPool(pool.Mesh.VaoId);
        foreach (var location in pool.Parts) if (Parts.TryGetValue(location, out var part)) part.Handle = 0;
        if (pool.Manager.ResidentMesh == pool.Mesh.VaoId)
            pool.Manager.ResidentMesh = pool.Manager.Pools.FirstOrDefault(candidate => !candidate.Refused)?.Mesh.VaoId ?? 0;
    }
    private static void Removed(MeshDataPool __instance, ModelDataPoolLocation location)
    {
        if (Pools.TryGetValue(__instance, out var pool)) pool.Parts.Remove(location);
        if (!Parts.TryGetValue(location, out var part)) return;
        if (part.Handle > 0) GameGraphicsAdapter.MeshAdapter(part.Pool.Mesh).RemoveResidentTerrainPart(part.Handle);
        if (--part.Slot.Parts == 0) ActiveSlots.Remove(part.Slot.Index);
        Parts.Remove(location);
    }
    internal static void Disposed(VAO mesh)
    {
        if (!MeshPools.TryGetValue(mesh, out var pool)) return;
        var graphics = GameGraphicsAdapter.MeshAdapter(mesh);
        graphics.RemoveResidentTerrainPool(mesh.VaoId);
        foreach (var location in pool.Parts)
            if (Parts.TryGetValue(location, out var part))
            {
                if (--part.Slot.Parts == 0) ActiveSlots.Remove(part.Slot.Index);
                Parts.Remove(location);
            }
        pool.Manager.Pools.Remove(pool); MeshPools.Remove(mesh); pool.Parts.Clear();
        if (pool.Refused) pool.Manager.CompatPools--;
        if (pool.Manager.ResidentMesh == mesh.VaoId)
            pool.Manager.ResidentMesh = pool.Manager.Pools.FirstOrDefault(candidate => !candidate.Refused)?.Mesh.VaoId ?? 0;
    }
    private static void VisibilityChanged(ClientChunk __instance)
    { Changed(Centers(__instance)); Changed(Edges(__instance)); }
    private static void LocationsChanged(ModelDataPoolLocation[] modelDataPoolLocations) => Changed(modelDataPoolLocations);
    // Delayed removal hides the old locations immediately, three frames before RemoveLocation.
    // Notify the same update path so a post-freeze change also refuses resident replay this frame.
    private static void LocationsRemoved(ModelDataPoolLocation[] locations) => Changed(locations);
    private static void ReadChunkFrustum(ClientChunk __instance)
    {
        // Match the original choice of first centre/edge part, including its null/empty cases.
        var centers = Centers(__instance); var edges = Edges(__instance);
        if (centers == null || edges == null || (centers.Length == 0 && edges.Length == 0)) return;
        var location = centers.Length == 0 ? edges[0] : centers[0];
        if (Parts.TryGetValue(location, out var part) && part.Handle > 0 && !part.Pool.Refused)
            GameGraphicsAdapter.MeshAdapter(part.Pool.Mesh).RefreshResidentTerrainFrustum(location, part);
    }
    private static void Changed(ModelDataPoolLocation[]? locations)
    {
        if (locations == null) return;
        foreach (var location in locations)
        {
            if (!Parts.TryGetValue(location, out var part) || part.Desc.Hidden == location.Hide) continue;
            part.Desc = part.Desc with { Hidden = location.Hide };
            if (part.Handle > 0) GameGraphicsAdapter.MeshAdapter(part.Pool.Mesh).UpdateResidentTerrainPart(part.Handle, part.Desc);
        }
    }
    private static bool ReplayCompatPool(MeshDataPool __instance)
    {
        if (!Pools.TryGetValue(__instance, out var pool) ||
            !GameGraphicsAdapter.MeshAdapter(pool.Mesh).SkipResidentTerrainPool(pool.Mesh.VaoId)) return true;
        __instance.indicesGroupsCount = __instance.RenderedTriangles = __instance.AllocatedTris = 0;
        return false;
    }
    internal static bool IsResident(ModelDataPoolLocation location) => Parts.TryGetValue(location, out var part) &&
        part.Handle > 0 && GameGraphicsAdapter.MeshAdapter(part.Pool.Mesh).SkipResidentTerrainPool(part.Pool.Mesh.VaoId);
    internal static bool TryManager(MeshDataPoolManager manager, out Manager retained) => Managers.TryGetValue(manager, out retained!);
    internal static FrustumCulling Frustum(MeshDataPoolManager manager) => Culler(manager);
    internal static bool ForeignPolicy() => new[] { typeof(ChunkRenderer), typeof(MeshDataPool), typeof(MeshDataPoolManager),
            typeof(ModelDataPoolLocation), typeof(FrustumCulling) }
        .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.NonPublic))
        .Any(method => Harmony.GetPatchInfo(method)?.Owners.Any(owner => !Performance.PerformancePatchSet.IsOwnOwner(owner)) == true);

    internal static void Republish(GameGraphicsAdapter graphics)
    {
        foreach (var entry in MeshPools)
        {
            Pool pool = entry.Value;
            if (!ReferenceEquals(GameGraphicsAdapter.MeshAdapter(pool.Mesh), graphics) || pool.Refused) continue;
            foreach (var location in pool.Parts)
                if (Parts.TryGetValue(location, out var part))
                {
                    part.Handle = graphics.AddResidentTerrainPart(part.Desc);
                    if (part.Handle == 0) { Refuse(pool, graphics); break; }
                }
        }
    }

    internal static void Visibility(Span<ulong> words)
    {
        words.Clear();
        int buffer = ModelDataPoolLocation.VisibleBufIndex;
        foreach (var slot in ActiveSlots.Values)
            if (slot.Visibility[buffer]) words[slot.Index >> 6] |= 1UL << (slot.Index & 63);
    }
    internal static int VisibilityWords => (nextSlot + 63) >> 6;

    // Evaluate the real stock method now, on private copies: neither later culler/visibility
    // updates nor IsVisible's FrustumVisible write may change the submitted frame or the game.
    internal static Dictionary<int, TerrainParityPart> CaptureParity(Manager manager, in TerrainView view,
        ReadOnlySpan<ulong> visibility)
    {
        var culler = new FrustumCulling
        {
            ViewDistanceSq = view.ViewDistanceSq, lod0BiasSq = (float)view.Lod0BiasSq, lod2BiasSq = view.Lod2BiasSq,
            shadowRangeX = view.ShadowRangeX, shadowRangeZ = view.ShadowRangeZ
        };
        Player(culler) = new BlockPos(view.PlayerBlockX, (int)view.PlayerY, view.PlayerBlockZ);
        static Vintagestory.API.Client.Plane Plane(TerrainPlane plane) => new()
        { normalX = plane.A, normalY = plane.B, normalZ = plane.C, D = plane.D };
        // The Plane constructor normalizes; these coefficients are already normalized.
        Planes(culler) = [Plane(view.Plane0), Plane(view.Plane1), Plane(view.Plane2),
            Plane(view.Plane3), Plane(view.Plane4), Plane(view.Plane5)];
        var expected = new Dictionary<int, TerrainParityPart>();
        var test = Performance.TerrainOcclusionCull.Source;
        var captured = test?.Source;
        var now = view.OcclusionCameraPosition ?? (X: view.CameraX, Y: view.CameraY, Z: view.CameraZ);
        bool depthApplies = view.CullMode == (int)EnumFrustumCullMode.CullNormal &&
            Performance.PerformancePatchSet.Current?.TerrainOcclusionCull == true;
        bool previous = ParityActive;
        ParityActive = true;
        try
        {
            foreach (var pool in manager.Pools)
            {
                if (pool.Refused) continue;
                foreach (var location in pool.Parts)
                {
                    if (!Parts.TryGetValue(location, out var part) || part.Handle == 0) continue;
                    int slot = part.Slot.Index;
                    bool visible = (uint)(slot >> 6) < (uint)visibility.Length &&
                        (visibility[slot >> 6] & (1UL << (slot & 63))) != 0;
                    Sphere sphere = location.FrustumCullSphere;
                    var inputs = part.Desc with { CenterX = sphere.x, CenterY = sphere.y, CenterZ = sphere.z,
                        Radius = sphere.radius, RadiusY = sphere.radiusY, RadiusZ = sphere.radiusZ,
                        Hidden = location.Hide, LodLevel = location.LodLevel };
                    var copy = new ModelDataPoolLocation
                    {
                        Hide = inputs.Hidden, LodLevel = inputs.LodLevel, FrustumVisible = location.FrustumVisible,
                        // Both entries agree, so a subsequent global VisibleBufIndex swap cannot affect this snapshot.
                        CullVisible = new Bools(visible, visible),
                        FrustumCullSphere = sphere // Sphere is a value type; preserve its exact float bits.
                    };
                    bool stockVisible = copy.IsVisible((EnumFrustumCullMode)view.CullMode, culler);
                    bool stockDepthVisible = stockVisible && !(depthApplies && test != null && captured != null &&
                        Performance.TerrainOcclusionCull.StockOccluded(sphere, captured, now, test));
                    expected.Add(part.Handle, new TerrainParityPart(stockVisible, stockDepthVisible, inputs, visible));
                }
            }
        }
        finally { ParityActive = previous; }
        return expected;
    }

    internal static TerrainView View(FrustumCulling culler, Vec3d camera, int eye, Matrix4x4 matrix, EnumFrustumCullMode mode, string uniform)
    {
        BlockPos player = Player(culler);
        var planes = Planes(culler);
        // The stock coefficients are world-space doubles. Keep their operation order and precision.
        TerrainPlane Plane(int i) => new(planes[i].normalX, planes[i].normalY, planes[i].normalZ, planes[i].D);
        return new TerrainView(eye, camera.X, camera.Y, camera.Z, player.X, player.Y, player.Z, matrix,
            ClientSettings.ViewDistance, culler.lod0BiasSq, culler.lod2BiasSq, (int)mode,
            mode is EnumFrustumCullMode.CullInstantShadowPassNear or EnumFrustumCullMode.CullInstantShadowPassFar)
        {
            OcclusionCameraPosition = eye is >= 0 and < 2 ? Performance.TerrainOcclusionCull.CameraNow() : null,
            ViewDistanceSq = culler.ViewDistanceSq, PlayerBlockX = player.X, PlayerBlockZ = player.Z,
            ShadowRangeX = culler.shadowRangeX, ShadowRangeZ = culler.shadowRangeZ, OriginUniform = uniform,
            Plane0 = Plane(0), Plane1 = Plane(1), Plane2 = Plane(2), Plane3 = Plane(3), Plane4 = Plane(4), Plane5 = Plane(5)
        };
    }
    internal static void Reset()
    {
        ChunkConsumerPatches.Adapter()?.ResetResidentTerrain();
        Managers.Clear(); Pools.Clear(); MeshPools.Clear(); Parts.Clear(); Slots.Clear(); ActiveSlots.Clear(); nextSlot = 0;
    }
}

internal sealed partial class GameGraphicsAdapter
{
    internal int AddResidentTerrainPart(in TerrainPartDesc part) => RequireDevice().AddTerrainPart(part);
    internal void UpdateResidentTerrainPart(int handle, in TerrainPartDesc part) => RequireDevice().UpdateTerrainPart(handle, part);
    internal void RemoveResidentTerrainPart(int handle) => RequireDevice().RemoveTerrainPart(handle);
    internal void RemoveResidentTerrainPool(int mesh) => RequireDevice().RemoveTerrainPool(mesh);
    internal void ResetResidentTerrain()
    {
        RequireLifecycleDevice().ResetTerrainScene();
        residentVisibilityFrame = ulong.MaxValue;
        residentReplay = false;
        Volatile.Write(ref residentMainSnapshot, null);
    }
}
