using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game.Performance;

/// <summary>Stock cube producers share the game's existing custom producer, pool and publication owners.</summary>
internal static partial class GpuChunkMeshing
{
    private static readonly object Gate = new();
    private static VulkanDevice? backend;
    private static bool enabled;
    private static bool faulted;
    private static int generation;
    private static int atlasGeneration;
    private static int dimension;
    private static ClientMain? world;
    [ThreadStatic] private static Work? current;
    [ThreadStatic] private static bool skippedPublication;
    private static readonly ConcurrentDictionary<Type, bool> AoTypes = new();
    private static readonly ConditionalWeakTable<MeshData, Payload> Payloads = new();
    // Only the failed dirty revision stays on the CPU, with one retry per
    // full/edge revision. Stock retry requeues do not mark a chunk dirty again.
    private static readonly ConditionalWeakTable<ClientChunk, OrderedFallback> OrderedCpu = new();
    private static readonly HashSet<Payload> Pending = new();
    private static readonly AccessTools.FieldRef<ChunkTesselator, Block[]> Solid = AccessTools.FieldRefAccess<ChunkTesselator, Block[]>("currentChunkBlocksExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, Block[]> Fluid = AccessTools.FieldRefAccess<ChunkTesselator, Block[]>("currentChunkFluidBlocksExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, int[]> Lights = AccessTools.FieldRefAccess<ChunkTesselator, int[]>("currentChunkRgbsExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, byte[]> Draw = AccessTools.FieldRefAccess<ChunkTesselator, byte[]>("currentChunkDraw32");
    private static readonly AccessTools.FieldRef<ChunkTesselator, MeshData[][][]> CenterPools =
        AccessTools.FieldRefAccess<ChunkTesselator, MeshData[][][]>("centerModeldataByRenderPassByLodLevel");
    private static readonly AccessTools.FieldRef<ChunkTesselator, MeshData[][][]> EdgePools =
        AccessTools.FieldRefAccess<ChunkTesselator, MeshData[][][]>("edgeModeldataByRenderPassByLodLevel");
    private static readonly AccessTools.FieldRef<ChunkTesselator, ClientMain> Game = AccessTools.FieldRefAccess<ChunkTesselator, ClientMain>("game");
    private static readonly AccessTools.FieldRef<TesselatedChunk, TesselatedChunkPart[]> Centers = AccessTools.FieldRefAccess<TesselatedChunk, TesselatedChunkPart[]>("centerParts");
    private static readonly AccessTools.FieldRef<TesselatedChunk, TesselatedChunkPart[]> Edges = AccessTools.FieldRefAccess<TesselatedChunk, TesselatedChunkPart[]>("edgeParts");
    private static readonly AccessTools.FieldRef<TesselatedChunk, ClientChunk> SourceChunk = AccessTools.FieldRefAccess<TesselatedChunk, ClientChunk>("chunk");
    private static readonly FieldInfo CubeHeight = AccessTools.Field(typeof(CubeTesselator), "blockHeight");
    private static readonly FieldInfo BleedingHeight = AccessTools.Field(typeof(BleedingCubeTesselator), "blockHeight");
    private static readonly MethodInfo Process = AccessTools.Method(typeof(ChunkTesselator), "NowProcessChunk",
        [typeof(int), typeof(int), typeof(int), typeof(TesselatedChunk), typeof(bool)]);
    private static readonly MethodInfo BeginProcess = AccessTools.Method(typeof(ChunkTesselator), "BeginProcessChunk");
    private static readonly MethodInfo ProducerStart = AccessTools.Method(typeof(ChunkTesselator), "Start", Type.EmptyTypes);
    private static readonly MethodInfo LightLevels = AccessTools.Method(typeof(ChunkTesselator), "LightlevelsReceived", Type.EmptyTypes);
    private static readonly MethodInfo Cube = AccessTools.Method(typeof(CubeTesselator), "Tesselate", [typeof(TCTCache)]);
    private static readonly MethodInfo Bleeding = AccessTools.Method(typeof(BleedingCubeTesselator), "Tesselate", [typeof(TCTCache)]);
    private static readonly MethodInfo UnusedDispose = AccessTools.Method(typeof(TesselatedChunk), "UnusedDispose");
    private static readonly MethodInfo Visible = AccessTools.Method(typeof(ChunkTesselator), "CalculateVisibleFaces",
        [typeof(bool), typeof(int), typeof(int), typeof(int)]);
    private static readonly FieldInfo CullField = AccessTools.Field(typeof(Block), nameof(Block.FaceCullMode));
    private static readonly MethodInfo DecorCull = AccessTools.Method(typeof(ChunkTesselator), "CullVisibleFacesWithDecor",
        [typeof(Dictionary<int, Block>), typeof(Dictionary<int, Block>)]);
    private static readonly MethodInfo DecorBuild = AccessTools.Method(typeof(ChunkTesselator), "BuildDecorPolygons");
    private static readonly MethodInfo MarkDirty = AccessTools.Method(typeof(ClientWorldMap), nameof(ClientWorldMap.MarkChunkDirty),
        [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(Action), typeof(bool), typeof(bool)]);
    private static readonly MethodInfo SetDirty = AccessTools.Method(typeof(ClientWorldMap), nameof(ClientWorldMap.SetChunkDirty),
        [typeof(long), typeof(bool), typeof(bool), typeof(bool)]);
    private static readonly MethodInfo BeforeFrame = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame), [typeof(float)]);
    private static readonly ConstructorInfo ManagerStart = AccessTools.Constructor(typeof(ChunkTesselatorManager), [typeof(ClientMain)]);
    private static readonly MethodInfo ManagerDispose = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.Dispose), [typeof(ClientMain)]);
    private static readonly MethodInfo ChunkCallback = AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerChunkRetesselated),
        [typeof(Vec3i), typeof(ClientChunk)]);
    private static readonly AccessTools.FieldRef<ChunkTesselator, IBlockTesselator[]> Producers =
        AccessTools.FieldRefAccess<ChunkTesselator, IBlockTesselator[]>("blockTesselators");
    private static readonly MethodInfo[] StockPolicies =
    [AccessTools.Method(typeof(Block), nameof(Block.DoEmitSideAo)), AccessTools.Method(typeof(Block), nameof(Block.DoEmitSideAoByFlag)),
     AccessTools.Method(typeof(CubeTesselator), nameof(CubeTesselator.DrawBlockFace)),
     AccessTools.Method(typeof(BleedingTextureHelper), nameof(BleedingTextureHelper.GetFaceTextureSubId)),
     AccessTools.Method(typeof(BleedingTextureHelper), nameof(BleedingTextureHelper.ComputeBleedForFace)),
     AccessTools.PropertyGetter(typeof(Block), nameof(Block.ForFluidsLayer)),
     AccessTools.PropertyGetter(typeof(BlockForFluidsLayer), nameof(Block.ForFluidsLayer))];

    private sealed class Work(ChunkTesselator producer, IChunkMeshBatch batch, int epoch,
        int x, int y, int z, bool edgeOnly, OrderedFallback ordered)
    {
        internal readonly ChunkTesselator Producer = producer;
        internal readonly IChunkMeshBatch Batch = batch;
        internal readonly int Generation = epoch;
        internal readonly int AtlasGeneration = atlasGeneration;
        internal readonly int X = x, Y = y, Z = z;
        internal readonly bool EdgeOnly = edgeOnly;
        internal readonly OrderedFallback Ordered = ordered;
        internal readonly long Revision = ordered.Revision;
        internal readonly bool OrderedCpu = (edgeOnly ? ordered.Edge : ordered.Full) == ordered.Revision;
        internal readonly Admission Admission = admission!;
        internal bool Decor;
        internal readonly List<ChunkMeshFace> Faces = new();
        internal readonly List<ChunkMeshBin> Bins = new();
        // Identities whose original CPU face culling is retained. Their faces
        // can still be lit and packed in compute with that stock mask.
        internal readonly HashSet<Block> CpuCulled = new(ReferenceEqualityComparer.Instance);
        internal ChunkMeshVoxel[]? Voxels;
        internal long ProbeBatch;
        internal bool Active;
        internal Dictionary<ChunkMeshBin, MeshData>? ReferenceMeshes;
        internal bool ReferenceFailed;
        internal readonly List<Payload> Payloads = new();
    }
    internal sealed class Payload(IChunkMeshBatch batch, ChunkMeshRange range, int epoch, int atlasEpoch, ClientChunk chunk) : IDisposable
    {
        internal readonly IChunkMeshBatch Batch = batch;
        internal readonly ChunkMeshRange Range = range;
        internal readonly int Generation = epoch;
        internal readonly int AtlasGeneration = atlasEpoch;
        internal readonly ClientChunk Chunk = chunk;
        private readonly long created = Stopwatch.GetTimestamp();
        internal long ProbeBatch;
        private int disposed;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Dispose() => Dispose("mesh-disposed");
        internal void Dispose(string reason)
        {
            lock (Gate)
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                if (reason == "superseded") Interlocked.Increment(ref payloadsSuperseded);
                if (ProbeBatch != 0) HeadlessChunkMeshingProbe.PayloadDisposed(ProbeBatch, Range,
                    Stopwatch.GetElapsedTime(created).TotalMilliseconds, reason);
                Pending.Remove(this);
                Batch.Dispose();
                GC.SuppressFinalize(this);
            }
        }
        ~Payload() { Dispose(); }
    }

    internal static void Configure(VulkanDevice device, bool requested)
    {
        lock (Gate)
        {
            bool next = requested && !faulted && device.ChunkMeshingAvailable;
            int nextDimension = world?.EntityPlayer?.Pos.Dimension ?? 0;
            if (ReferenceEquals(backend, device) && enabled == next && dimension == nextDimension) return;
            RetirePending(dimension != nextDimension ? "dimension-left" : "live-switch");
            dimension = nextDimension; OrderedCpu.Clear();
            backend = device; enabled = next; generation++;
            // Retain the visible pools until the original dirty-chunk queue
            // supplies replacements, including both center and border parts.
            world?.RedrawAllBlocks();
        }
    }
    internal static void WorldLeft()
    {
        Payload[] pending;
        lock (Gate)
        {
            generation++; atlasGeneration++; world = null; enabled = false;
            OrderedCpu.Clear(); LightingOwners.Clear(); AoTypes.Clear();
            pending = Pending.ToArray();
        }
        foreach (var payload in pending) payload.Dispose("world-left");
        if (HeadlessChunkMeshingProbe.Active) HeadlessChunkMeshingProbe.WorldLeft();
    }
    internal static bool TryPayload(MeshData data, out Payload payload) => Payloads.TryGetValue(data, out payload!);

    internal static PerformanceFeature Create() => new("gpu-chunk-meshing", s => s.GpuChunkMeshing, Validate, harmony =>
    {
        // ClientMain.Start creates this manager before its terrain workers run.
        // Attach at that lifecycle boundary, never from a tessellation worker.
        harmony.Patch(ManagerStart, postfix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(LoadingWorld)));
        harmony.Patch(ManagerDispose, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(LeavingWorld)));
        harmony.Patch(ProducerStart, postfix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(LightingStarted)));
        harmony.Patch(LightLevels, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(LightingChanging)));
        harmony.Patch(Process, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Start)),
            postfix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Finish)),
            finalizer: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(End)));
        harmony.Patch(BeginProcess, postfix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Snapshot)));
        harmony.Patch(Visible, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(PrepareVisibility)),
            transpiler: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(VisibilityTranspiler)));
        harmony.Patch(DecorCull, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(DecorVisibility)));
        harmony.Patch(DecorBuild, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(BeginDecor)),
            postfix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(EndDecor)));
        harmony.Patch(MarkDirty, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(MarkedDirty)));
        harmony.Patch(SetDirty, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(IndexedDirty)));
        harmony.Patch(Cube, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(CaptureCube)));
        harmony.Patch(Bleeding, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(CaptureCube)));
        harmony.Patch(Insert, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Reserve)));
        harmony.Patch(AccessTools.Method(typeof(MeshData), nameof(MeshData.Dispose)),
            prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Release)));
        harmony.Patch(AccessTools.Method(typeof(ChunkTesselator), nameof(ChunkTesselator.BlockTexturesLoaded)),
            prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(AtlasChanged)));
        harmony.Patch(AccessTools.Method(typeof(ChunkRenderer), nameof(ChunkRenderer.AddTesselatedChunk)),
            prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Publishable)),
            finalizer: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(PublicationEnded)));
        harmony.Patch(BeforeFrame, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(ResetPublication)),
            transpiler: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(CallbackTranspiler)));
        InstallPayloadLifecycle(harmony);
    }, Uninstall: WorldLeft, WorldReady: api =>
    {
        if (api.World is ClientMain next) AttachWorld(next, remesh: true);
    }, WorldLeft: WorldLeft);

    private static void LoadingWorld(ClientMain __0) => AttachWorld(__0, remesh: false);
    private static void AttachWorld(ClientMain next, bool remesh)
    {
        Payload[] retired;
        lock (Gate)
        {
            if (ReferenceEquals(world, next)) return;
            retired = Pending.ToArray();
            world = next;
            dimension = next.EntityPlayer?.Pos.Dimension ?? 0; OrderedCpu.Clear();
            LightingOwners.Clear(); AoTypes.Clear();
            generation++; atlasGeneration++;
            if (remesh) LightingStarted(next.TerrainChunkTesselator);
            // Only a missed loading attachment needs this fallback rebuild.
            // The normal WorldReady event preserves jobs for the same owner.
            if (remesh && enabled) next.RedrawAllBlocks();
        }
        foreach (var payload in retired) payload.Dispose("world-replaced");
    }
    private static void LeavingWorld(ClientMain __0)
    {
        lock (Gate)
            if (ReferenceEquals(world, __0)) WorldLeft();
    }

    private static void Validate()
    {
        if (Process?.ReturnType != typeof(int) || BeginProcess?.ReturnType != typeof(bool) || Cube?.ReturnType != typeof(void) ||
            Bleeding?.ReturnType != typeof(void) || CubeHeight?.FieldType != typeof(float) || BleedingHeight?.FieldType != typeof(float) ||
            UnusedDispose == null || ProducerStart?.ReturnType != typeof(void) || LightLevels?.ReturnType != typeof(void) ||
            Visible?.ReturnType != typeof(bool) || Insert?.ReturnType != typeof(ModelDataPoolLocation) ||
            ManagerStart == null || ManagerDispose?.ReturnType != typeof(void) || DecorCull?.ReturnType != typeof(void) ||
            DecorBuild?.ReturnType != typeof(void) ||
            MarkDirty?.ReturnType != typeof(void) || SetDirty?.ReturnType != typeof(void) ||
            StockPolicies.Any(m => m == null))
            throw new InvalidOperationException("Official chunk meshing signatures changed.");
        foreach (var method in new[] { Process, BeginProcess, ProducerStart, LightLevels, Cube, Bleeding, Insert, Visible, DecorCull, DecorBuild })
            if (method.GetMethodBody() == null || Harmony.GetPatchInfo(method)?.Owners.Any(o => !PerformancePatchSet.IsOwnOwner(o)) == true)
                throw new InvalidOperationException("Chunk producer/pool has an external replacement.");
        // Validate the physical ABI used by the unchanged SSBO terrain shaders.
        if (Marshal.SizeOf<FaceData>() != 64 || Marshal.SizeOf<ChunkMeshFace>() != 64 || Marshal.SizeOf<ChunkMeshVoxel>() != 32)
            throw new InvalidOperationException("Chunk face ABI changed.");
        ValidatePool();
        ValidatePayloadLifecycle();
        if (PatchProcessor.GetOriginalInstructions(Visible).Count(i => i.LoadsField(CullField)) != 1)
            throw new InvalidOperationException("Stock solid visibility anchor changed.");
        if (BeforeFrame?.ReturnType != typeof(void) || ChunkCallback?.ReturnType != typeof(void) ||
            PatchProcessor.GetOriginalInstructions(BeforeFrame).Count(i => i.Calls(ChunkCallback)) != 2)
            throw new InvalidOperationException("Stock chunk publication callbacks changed.");
    }

    private static void Start(ChunkTesselator __instance, int __0, int __1, int __2, TesselatedChunk __3, bool __4, out ProducerState __state)
    {
        __state = new(current, admission); current = null; admission = new Admission();
        lock (Gate)
        {
            ClientMain game = Game(__instance);
            if (!ReferenceEquals(world, game))
            { admission.Refuse(Refusal.World, "world-owner-mismatch", new { attached = world != null, dimension }); return; }
            if (PerformancePatchSet.Current?.GpuChunkMeshing != true)
            { admission.Refuse(Refusal.Control, "control-disabled"); return; }
            if (faulted) { admission.Refuse(Refusal.Backend, "backend-faulted"); return; }
            if (backend == null || !backend.ChunkMeshingAvailable)
            { admission.Refuse(Refusal.Backend, "backend-unavailable"); return; }
            if (!enabled) { admission.Refuse(Refusal.Control, "admission-disabled"); return; }
            if (!LightingReady(__instance))
            { admission.Refuse(Refusal.Snapshot, "current-world-lighting-not-ready"); return; }
            if (!game.Platform.UseSSBOs) { admission.Refuse(Refusal.Backend, "ssbo-disabled"); return; }
            // A late foreign producer patch keeps its original path.
            foreach (MethodInfo method in new[] { Cube, Bleeding, ProducerStart, LightLevels, BeginProcess, Process, Insert, Visible, DecorCull, DecorBuild })
                if (Harmony.GetPatchInfo(method)?.Owners.Any(o => !PerformancePatchSet.IsOwnOwner(o)) == true)
                { admission.Refuse(Refusal.ForeignPatch, "foreign-producer-patch", new { method = method.ToString(),
                    owners = Harmony.GetPatchInfo(method)?.Owners.ToArray() }); return; }
            foreach (MethodInfo method in StockPolicies)
                if (Harmony.GetPatchInfo(method)?.Owners.Any(o => !PerformancePatchSet.IsOwnOwner(o)) == true)
                { admission.Refuse(Refusal.ForeignPatch, "foreign-policy-patch", new { method = method.ToString(),
                    owners = Harmony.GetPatchInfo(method)?.Owners.ToArray() }); return; }
            ClientChunk chunk = SourceChunk(__3);
            OrderedFallback ordered = OrderedCpu.GetValue(chunk, _ => new OrderedFallback());
            IChunkMeshBatch? lease;
            try
            {
                admission.LeaseAttempted = true;
                lease = backend.LeaseChunkMeshing(__0, __1, __2, RuntimeHelpers.GetHashCode(chunk), ordered.Revision,
                    __4, generation, out string? refusal, out object? owners);
                if (lease == null)
                {
                    admission.Refuse(refusal == "lease-capacity-saturated" ? Refusal.Capacity : Refusal.Backend,
                        refusal ?? "backend-unavailable", new { owners, pendingPayloads = Pending.GroupBy(p => p.Batch)
                            .Select(g => new { leaseIdentity = RuntimeHelpers.GetHashCode(g.Key), count = g.Count() }).ToArray() });
                    return;
                }
            }
            catch (Exception error)
            {
                admission.Refuse(Refusal.Allocation, "lease-allocation-failed", new { error = error.ToString() });
                enabled = false; faulted = true;
                game.Logger.Warning("VulkanStory: GPU chunk meshing allocation refused: {0}", error.Message);
                return;
            }
            current = new Work(__instance, lease, generation, __0 * 32, __1 * 32 % 32768, __2 * 32, __4,
                ordered);
        }
    }
    private static void Snapshot(ChunkTesselator __instance, int __0, int __1, int __2, ClientChunk __3, bool __4, bool __result)
    {
        Work? work = current;
        try
        {
            if (work != null && ReferenceEquals(work.Producer, __instance))
            {
                if (!__result) work.Admission.Refuse(Refusal.Snapshot, "stock-snapshot-not-visible");
                else CaptureSnapshot(work, __instance);
            }
        }
        catch (Exception error)
        {
            admission?.Refuse(error is OutOfMemoryException ? Refusal.Allocation : Refusal.Snapshot,
                error is OutOfMemoryException ? "snapshot-allocation-failed" : "snapshot-capture-failed", new { error = error.ToString() });
            throw;
        }
        finally
        {
            if (HeadlessChunkMeshingProbe.Active)
            {
                long probe = HeadlessChunkMeshingProbe.Snapshot(Game(__instance), __0, __1, __2, __3,
                    __result, work?.Active == true && ReferenceEquals(work.Producer, __instance) ? work.Batch : null,
                    __4, Solid(__instance), Fluid(__instance), admission?.Describe() ?? new { reason = "start-hook-not-observed" });
                if (work != null && ReferenceEquals(work.Producer, __instance)) work.ProbeBatch = probe;
                if (probe != 0 && work?.Active == true) backend?.EnableChunkMeshingEquality(work.Batch);
            }
        }
    }
    private static void CaptureSnapshot(Work work, ChunkTesselator __instance)
    {
        lock (Gate)
            if (work.Generation != generation || !LightingReady(__instance))
            { work.Admission.Refuse(Refusal.Snapshot, "lighting-changed-before-snapshot"); return; }
        Block[] solids = Solid(__instance), fluids = Fluid(__instance); int[] light = Lights(__instance);
        if (solids.Length != 39304 || fluids.Length != solids.Length || light.Length != solids.Length)
        { work.Admission.Refuse(Refusal.Snapshot, "snapshot-layout-unsupported", new { solids = solids.Length,
            fluids = fluids.Length, lights = light.Length }); return; }
        var voxels = new ChunkMeshVoxel[solids.Length];
        for (int i = 0; i < voxels.Length; i++)
        {
            Block b = solids[i], f = fluids[i];
            if (b == null || f == null)
            { work.Admission.Refuse(Refusal.Snapshot, "snapshot-null-block", new { index = i, solid = b == null, fluid = f == null }); return; }
            // The stock face-light pass treats an absorbing fluid as the front
            // block, otherwise the solid. Pack that front block's policies too.
            Block front = f.LightAbsorption > 0 ? f : b;
            uint emit = b.EmitSideAo | (uint)front.EmitSideAo << 8;
            if (front.LightAbsorption > 0 && StockAo(front.GetType()) && front.ForFluidsLayer) emit |= 1u << 16;
            voxels[i] = new ChunkMeshVoxel { Identity = b.Id, Material = (int)b.BlockMaterial,
                Opaque = Bits(b.SideOpaque), Solid = Bits(b.SideSolid), EmitAo = emit,
                Absorption = b.LightAbsorption, FluidAbsorption = f.LightAbsorption, Light = (uint)light[i] };
        }
        work.Voxels = voxels; work.Active = true;
    }
    private static uint Bits(Vintagestory.API.Datastructures.SmallBoolArray bits)
    {
        uint value = 0;
        for (int side = 0; side < 6; side++) if (bits[side]) value |= 1u << side;
        return value;
    }
    // Face lighting calls these policies on neighbouring and front blocks.
    // Only the stock declarations are known to be side-effect-free; the stock
    // ForFluidsLayer getters are constants. Other overrides stay on the CPU.
    private static bool StockAo(Type type) => AoTypes.GetOrAdd(type, t =>
        AccessTools.Method(t, nameof(Block.DoEmitSideAo))?.DeclaringType == typeof(Block) &&
        AccessTools.Method(t, nameof(Block.DoEmitSideAoByFlag))?.DeclaringType == typeof(Block) &&
        AccessTools.PropertyGetter(t, nameof(Block.ForFluidsLayer))?.DeclaringType is { } owner &&
        (owner == typeof(Block) || owner == typeof(BlockForFluidsLayer)));

    private static bool SupportedProducer(Work work, Block block)
    {
        int draw = (int)block.DrawType;
        if (draw is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 10 or 12) || !SupportedPass(work, block.RenderPass)) return false;
        Type type = Producers(work.Producer)[draw].GetType();
        return type == typeof(CubeTesselator) || type == typeof(BleedingCubeTesselator);
    }
    // Depth-writing opaque passes can publish GPU faces as their own part.
    // Blended/OIT passes depend on emission order: compute keeps the stock
    // order, and a GPU bin must own the entire stock mesh of its pass/atlas.
    private static bool Ordered(EnumChunkRenderPass pass) =>
        pass is EnumChunkRenderPass.BlendNoCull or EnumChunkRenderPass.Transparent or EnumChunkRenderPass.Meta;
    private static bool SupportedPass(Work work, EnumChunkRenderPass pass) =>
        pass is EnumChunkRenderPass.Opaque or EnumChunkRenderPass.OpaqueNoCull || Ordered(pass) && !work.OrderedCpu;
    // Compute owns the pair test only for solid-layer stock cull policies.
    private static bool GpuCulled(Work work, Block block) =>
        (uint)block.FaceCullMode <= 5 && !work.CpuCulled.Contains(block) && SupportedProducer(work, block);

    private static IEnumerable<CodeInstruction> VisibilityTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (!instruction.LoadsField(CullField)) { yield return instruction; continue; }
            // Existing stack contains the source block. Preserve its branch
            // labels on ldarg.0, then pass the producer to the policy helper.
            var load = new CodeInstruction(OpCodes.Ldarg_0);
            load.labels.AddRange(instruction.labels); load.blocks.AddRange(instruction.blocks);
            yield return load;
            yield return CodeInstruction.Call(typeof(GpuChunkMeshing), nameof(VisibilityPolicy));
        }
    }
    private static EnumFaceCullMode VisibilityPolicy(Block block, ChunkTesselator producer) =>
        current is { } work && ReferenceEquals(work.Producer, producer) && GpuCulled(work, block)
            ? EnumFaceCullMode.NeverCull : block.FaceCullMode;

    private static void PrepareVisibility(ChunkTesselator __instance)
    {
        Work? work = current;
        if (work == null || !ReferenceEquals(work.Producer, __instance)) return;
        Block[] blocks = Solid(__instance), fluids = Fluid(__instance);
        // Never duplicate a virtual AllowSnowCoverage callback. If any cell of
        // this block identity needs that policy, the original pass culls it
        // throughout this chunk, invoking it once. The same applies when one
        // identity occupies both layers of a cell, which would make its CPU
        // fallback ambiguous. Compute still lights and packs those faces.
        for (int y = 1; y <= 32; y++)
            for (int z = 1; z <= 32; z++)
                for (int x = 1; x <= 32; x++)
                {
                    int i = (y * 34 + z) * 34 + x;
                    if (blocks[i] is not { } block) continue;
                    if (blocks[i + 1156]?.DrawType == EnumDrawType.JSONAndSnowLayer ||
                        ReferenceEquals(fluids[i], block) && block.DrawType != EnumDrawType.Empty) work.CpuCulled.Add(block);
                }
    }

    // Stock decor admission reads the culled face bits of the attached cell.
    // Restore the original solid mask there; compute repeats the same pair
    // test on that subset, so only faces visible in the stock mask remain.
    private static void DecorVisibility(ChunkTesselator __instance, Dictionary<int, Block> __0)
    {
        if (current is not { } work || !ReferenceEquals(work.Producer, __instance)) return;
        byte[] draw = Draw(__instance); Block[] blocks = Solid(__instance);
        foreach (var (key, decor) in __0)
        {
            if (decor == null || ((decor.IsMissing ? 1 : decor.decorBehaviorFlags) & 1) == 0) continue;
            int index = DecorBits.Index3dFromIndex(key), x = index % 32, z = index / 32 % 32, y = index / 1024;
            // Edge-only passes leave the interior's previous bits unused.
            if (draw[index] == 0 || work.EdgeOnly && x % 31 != 0 && y % 31 != 0 && z % 31 != 0) continue;
            int ext = ((y + 1) * 34 + z + 1) * 34 + x + 1;
            if (blocks[ext] is { } block && GpuCulled(work, block))
                draw[index] = (byte)(draw[index] & ~63 | CpuFaces(__instance, block, ext, work.X + x, work.Y + y, work.Z + z));
        }
    }

    // Stock's alternating-offset decor branch does not set isDecorTesselate.
    // Guard the entire pass, including decorations matching the attached block.
    private static void BeginDecor(ChunkTesselator __instance)
    { if (current is { } work && ReferenceEquals(work.Producer, __instance)) work.Decor = true; }
    private static void EndDecor(ChunkTesselator __instance)
    { if (current is { } work && ReferenceEquals(work.Producer, __instance)) work.Decor = false; }

    // Used only when an admitted producer cannot fit or its particular cell has
    // custom AO/fluid/shape policy, and for decor-bearing cells. Other
    // GPU-culled cells never run these CPU tests.
    private static int CpuFaces(ChunkTesselator producer, Block a, int index, int x, int y, int z)
    {
        int mask = 0; Block[] blocks = Solid(producer);
        for (int side = 0; side < 6; side++)
        {
            Block b = blocks[index + TileSideEnum.MoveIndex[side]];
            bool opaque = a.SideOpaque[side], other = b.SideOpaque[TileSideEnum.GetOpposite(side)];
            if (side == 4 && b.DrawType == EnumDrawType.JSONAndSnowLayer && other &&
                !a.AllowSnowCoverage(Game(producer), new BlockPos(x, y, z))) other = false;
            bool visible = a.FaceCullMode switch
            {
                EnumFaceCullMode.Default => !other || !opaque,
                EnumFaceCullMode.NeverCull => true,
                EnumFaceCullMode.Merge => b != a && (!opaque || !other),
                EnumFaceCullMode.Collapse => b == a && side is 4 or 0 or 3 || b != a && (!opaque || !other),
                EnumFaceCullMode.MergeMaterial => !a.SideSolid[side] || b.BlockMaterial != a.BlockMaterial && (!opaque || !other) || !b.SideSolid[TileSideEnum.GetOpposite(side)],
                EnumFaceCullMode.CollapseMaterial => b.BlockMaterial == a.BlockMaterial ? side is 0 or 3 : !other || side < 4 && !opaque,
                _ => throw new InvalidOperationException("Unsupported cube cull policy."),
            };
            if (visible) mask |= 1 << side;
        }
        return mask;
    }
    private static bool Cpu(TCTCache vars)
    {
        if (current is { } work && ReferenceEquals(work.Producer, vars.tct) && !work.Decor && !vars.isDecorTesselate &&
            ReferenceEquals(vars.block, Solid(vars.tct)[vars.extIndex3d]) && GpuCulled(work, vars.block))
            vars.drawFaceFlags &= CpuFaces(vars.tct, vars.block, vars.extIndex3d, vars.posX, vars.posY, vars.posZ);
        return true;
    }

    private static bool CaptureCube(object __instance, TCTCache __0)
    {
        if (referenceProducer) return true;
        Work? work = current; TCTCache vars = __0;
        if (work?.Active != true || !ReferenceEquals(vars.tct, work.Producer)) return Cpu(vars);
        if (work.Decor || vars.isDecorTesselate || vars.IsDecorOnJson)
            return RefuseCube(work, vars, Refusal.UnsupportedProducer, "decor-producer");
        if (vars.occ != 0.67f || vars.halfoccInverted != 0.0196875f ||
            !ReferenceEquals(vars.blockFaceVertices, CubeFaceVertices.blockFaceVertices))
            return RefuseCube(work, vars, Refusal.UnsupportedProducer, "custom-cube-layout-or-ao");
        if (work.OrderedCpu && (Ordered(vars.RenderPass) || Ordered(vars.block.RenderPass)))
            return RefuseCube(work, vars, Refusal.OrderedFallback, "ordered-cpu-dirty-revision-marker",
                work.ProbeBatch == 0 ? null : new { work.Revision, currentRevision = work.Ordered.Revision, work.Ordered.Full, work.Ordered.Edge,
                    work.EdgeOnly, pass = vars.RenderPass.ToString() });
        if (!SupportedPass(work, vars.RenderPass))
            return RefuseCube(work, vars, Refusal.UnsupportedProducer, "unsupported-render-pass",
                work.ProbeBatch == 0 ? null : new { pass = vars.RenderPass.ToString() });
        if (!SupportedProducer(work, vars.block) ||
            __instance.GetType() != typeof(CubeTesselator) && __instance.GetType() != typeof(BleedingCubeTesselator))
            return RefuseCube(work, vars, Refusal.UnsupportedProducer, "unsupported-cube-producer",
                work.ProbeBatch == 0 ? null : new { producer = __instance.GetType().FullName,
                    block = vars.block.Code?.ToString(), draw = vars.block.DrawType.ToString() });
        if (work.Faces.Count + 6 > work.Batch.FaceCapacity)
            return RefuseCube(work, vars, Refusal.Capacity, "face-capacity-saturated",
                work.ProbeBatch == 0 ? null : new { count = work.Faces.Count, work.Batch.FaceCapacity });
        Block[] blocks = Solid(vars.tct), fluids = Fluid(vars.tct);
        // Fluid-layer cubes (lake ice) keep their stock fluid-layer culling.
        bool solid = ReferenceEquals(vars.block, blocks[vars.extIndex3d]);
        if (!solid && !ReferenceEquals(vars.block, fluids[vars.extIndex3d]))
            return RefuseCube(work, vars, Refusal.UnsupportedProducer, "snapshot-producer-identity-mismatch");
        // Position-dependent/custom AO remains CPU. The full 3x3x3 producer
        // neighborhood, and each absorbing fluid that stock lighting can treat
        // as the front block, must use the stock AO and fluid-layer policies.
        for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
                for (int x = -1; x <= 1; x++)
                {
                    int i = vars.extIndex3d + y * 1156 + z * 34 + x;
                    if (!StockAo(blocks[i].GetType()) || fluids[i].LightAbsorption > 0 && !StockAo(fluids[i].GetType()))
                        return RefuseCube(work, vars, Refusal.UnsupportedProducer, "custom-neighbour-ao-or-fluid-policy",
                            work.ProbeBatch == 0 ? null : new { index = i,
                                solid = blocks[i].GetType().FullName, fluid = fluids[i].GetType().FullName });
                }
        // Retained CPU culling already produced the stock face mask; compute
        // keeps it with NeverCull and only performs lighting and packing.
        uint cull = (uint)(solid && GpuCulled(work, vars.block) ? vars.block.FaceCullMode : EnumFaceCullMode.NeverCull);
        Block self = vars.block;
        uint policy = (uint)EnumBlockMaterial.Leaves | (self.BlockMaterial == EnumBlockMaterial.Leaves ? 256u : 0u) |
            (uint)GameMath.Clamp(self.LightAbsorption, 0, 32) << 16;
        float height = (float)(__instance is BleedingCubeTesselator ? BleedingHeight : CubeHeight).GetValue(__instance)!;
        bool edge = vars.lx is 0 or 31 || vars.ly is 0 or 31 || vars.lz is 0 or 31;
        int rollback = work.Faces.Count;
        try
        {
            for (int side = 0; side < 6; side++)
            {
                // This original mask includes bedrock suppression, decors, snow
                // callbacks and edge-only policy. Compute owns stock pair culling.
                if ((vars.drawFaceFlags & (1 << side)) == 0) continue;
                TextureAtlasPosition texture = Texture(__instance, vars, side);
                var key = new ChunkMeshBin(edge, (int)vars.RenderPass, texture.atlasNumber);
                int bin = work.Bins.IndexOf(key);
                if (bin < 0)
                {
                    if (work.Bins.Count >= 128)
                    { work.Faces.RemoveRange(rollback, work.Faces.Count - rollback);
                        return RefuseCube(work, vars, Refusal.Capacity, "bin-capacity-saturated",
                            work.ProbeBatch == 0 ? null : new { count = work.Bins.Count }); }
                    bin = work.Bins.Count; work.Bins.Add(key);
                }
                work.Faces.Add(new ChunkMeshFace { X = vars.finalX, Y = vars.finalY, Z = vars.finalZ, Height = height,
                    U1 = texture.x1, V1 = texture.y1, U2 = texture.x2, V2 = texture.y2,
                    Index = (uint)vars.extIndex3d, Side = (uint)side, Bin = (uint)bin,
                    Flags = (uint)(vars.VertexFlags | BlockFacing.ALLFACES[side].NormalPackedFlags),
                    ColorMap = (uint)vars.ColorMapData.Value, CullMode = cull,
                    Smooth = vars.aoAndSmoothShadows && vars.block.SideAo[side] ? 1u : 0u,
                    Padding = policy });
            }
        }
        catch
        {
            work.Faces.RemoveRange(rollback, work.Faces.Count - rollback);
            throw;
        }
        vars.UpdateChunkMinMax(vars.finalX, vars.finalY, vars.finalZ);
        vars.UpdateChunkMinMax(vars.finalX + 1, vars.finalY + height, vars.finalZ + 1);
        if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Candidates(work.ProbeBatch, vars.posX, vars.posY, vars.posZ,
            CollectionsMarshal.AsSpan(work.Faces)[rollback..]);
        if (work.ProbeBatch != 0)
        {
            try { CaptureReference(work, __instance, vars, solid); }
            catch (Exception error)
            {
                work.ReferenceFailed = true;
                HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "equality-reference-failed", new { error = error.ToString() });
            }
        }
        return false;
    }

    private static TextureAtlasPosition Texture(object producer, TCTCache vars, int side)
    {
        int sub;
        if (producer is BleedingCubeTesselator)
        {
            sub = BleedingTextureHelper.GetFaceTextureSubId(vars, side);
            if (sub <= 0) sub = vars.fastBlockTextureSubidsByFace[side];
            var bleed = BleedingTextureHelper.ComputeBleedForFace(vars, sub, side, -1);
            int blended = bleed.HasBleed ? bleed.BlendedTextureSubId : sub;
            var positions = Game(vars.tct).BlockAtlasManager.TextureAtlasPositionsByTextureSubId;
            if (BleedingTextureHelper.IsValidTextureSubId(positions, blended)) sub = blended;
            return positions[sub];
        }
        sub = vars.fastBlockTextureSubidsByFace[side];
        var block = vars.block;
        if ((block.HasTiles || block.HasAlternates) && block.FastTextureVariants[side] is { } variants)
        {
            int selector = block.HasTiles ? BakedCompositeTexture.GetTiledTexturesSelector(variants, side, vars.posX, vars.posY, vars.posZ)
                : GameMath.MurmurHash3(vars.posX, vars.posY, vars.posZ);
            sub = variants[GameMath.Mod(selector, variants.Length)].TextureSubId;
        }
        return vars.textureAtlasPositionsByTextureSubId[sub];
    }
    private static ChunkMeshAoNeighbour[] Neighbours()
    {
        var result = new ChunkMeshAoNeighbour[54];
        for (int side = 0; side < 6; side++)
            for (int i = 0; i < 9; i++)
            {
                var n = CubeFaceVertices.blockFaceVerticesCentered[side][i];
                result[side * 9 + i] = new ChunkMeshAoNeighbour { Offset = n.extIndexOffset, Facing = n.FacingFlags,
                    Opposite = n.OppositeFlags, UpperOrLeft = n.OppositeFlagsUpperOrLeft, LowerOrRight = n.OppositeFlagsLowerOrRight };
            }
        return result;
    }
    private static void Finish(ChunkTesselator __instance, TesselatedChunk tessChunk, ref int __result)
    {
        Work? work = current;
        if (work?.Active != true || !ReferenceEquals(work.Producer, __instance)) return;
        if (work.Faces.Count == 0)
        {
            // A lease is only resource admission. No supported producer may
            // have supplied faces after the stock snapshot was captured.
            work.Admission.Refuse(Refusal.UnsupportedProducer, "no-supported-face-candidates");
            if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "cpu-no-gpu-admission", work.Admission.Describe());
            return;
        }
        lock (Gate)
        {
            bool lightingReady = LightingReady(__instance);
            if (work.Generation != generation || SourceChunk(tessChunk).Disposed || !lightingReady)
            {
                work.Admission.Refuse(lightingReady ? Refusal.World : Refusal.Snapshot,
                    lightingReady ? "generation-changed" : "current-world-lighting-changed",
                    new { captured = work.Generation, current = generation, lightingReady });
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "cpu-no-gpu-admission", work.Admission.Describe());
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "generation-rejected",
                    new { captured = work.Generation, current = generation });
                UnusedDispose.Invoke(tessChunk, null); throw new RetryTesselationException("GPU chunk generation changed.");
            }
            if (MixedOrderedBin(work) is { } mixed)
            {
                work.Admission.Refuse(Refusal.OrderedFallback, "mixed-blended-bin-retry", new { mixed, work.Revision, work.EdgeOnly });
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "cpu-no-gpu-admission", work.Admission.Describe());
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "ordered-bin-mixed", new { mixed });
                // A blended mesh shared with CPU producers cannot keep the stock
                // face order across two parts. Retry with original producers.
                RememberOrdered(work);
                UnusedDispose.Invoke(tessChunk, null); throw new RetryTesselationException("Blended GPU chunk faces share a CPU mesh.");
            }
            ChunkMeshRange[] ranges;
            try { ranges = work.Batch.Generate(work.Voxels!, CollectionsMarshal.AsSpan(work.Faces), Neighbours(), work.Bins.Count); }
            catch (Exception error)
            {
                work.Admission.Refuse(Refusal.Backend, "generation-failed", new { error = error.ToString() });
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "cpu-no-gpu-admission", work.Admission.Describe());
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "generation-failed", new { error = error.Message });
                // No partial chunk can reach callbacks/publication. Requeue once
                // with the original CPU producers if backend generation failed.
                enabled = false; faulted = true; generation++; RetirePending("cpu-fallback"); UnusedDispose.Invoke(tessChunk, null);
                Game(__instance).Logger.Warning("VulkanStory: GPU chunk meshing generation failed: {0}", error.Message);
                throw new RetryTesselationException("GPU chunk generation failed; retry with stock producers.");
            }
            if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Generated(work.ProbeBatch, ranges, work.Bins.ToArray());
            if (work.ProbeBatch != 0) CompareReference(work, ranges);
            var center = new List<TesselatedChunkPart>(); var edge = new List<TesselatedChunkPart>();
            foreach (var range in ranges)
            {
                var bin = work.Bins[range.Bin];
                var data = new MeshData(false) { VerticesCount = range.FaceCount * 4, IndicesCount = range.FaceCount * 6,
                    CustomInts = new CustomMeshDataPartInt() };
                work.Batch.Retain();
                var payload = new Payload(work.Batch, range, generation, work.AtlasGeneration, SourceChunk(tessChunk)) { ProbeBatch = work.ProbeBatch };
                work.Payloads.Add(payload);
                Payloads.Add(data, payload); Pending.Add(payload);
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "payload-created",
                    new { range, generation, atlas = work.AtlasGeneration });
                var part = new TesselatedChunkPart();
                PartAtlas.SetValue(part, bin.Atlas); PartPass.SetValue(part, (EnumChunkRenderPass)bin.Pass); PartLod.SetValue(part, data);
                (bin.Edge ? edge : center).Add(part);
                __result += data.VerticesCount;
            }
            if (center.Count != 0) Centers(tessChunk) = (Centers(tessChunk) ?? []).Concat(center).ToArray();
            if (edge.Count != 0) Edges(tessChunk) = (Edges(tessChunk) ?? []).Concat(edge).ToArray();
        }
    }
    private static ChunkMeshBin? MixedOrderedBin(Work work)
    {
        if (work.OrderedCpu) return null;
        foreach (var bin in work.Bins)
            if (Ordered((EnumChunkRenderPass)bin.Pass) &&
                (bin.Edge ? EdgePools(work.Producer) : CenterPools(work.Producer))[1][bin.Pass][bin.Atlas].VerticesCount != 0) return bin;
        return null;
    }
    private static Exception? End(Exception? __exception, ProducerState? __state)
    {
        Work? work = current; current = __state?.Work; admission = __state?.Admission;
        if (__exception != null && work != null)
            foreach (var payload in work.Payloads) payload.Dispose(__exception is RetryTesselationException ? "retry-tessellation" : "producer-failed");
        if (work?.ProbeBatch is > 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "producer-ended",
            new { candidates = work.Faces.Count, error = __exception?.Message });
        work?.Batch.Dispose();
        return __exception;
    }
    private static bool Release(MeshData __instance)
    {
        if (!Payloads.TryGetValue(__instance, out var payload)) return true;
        Payloads.Remove(__instance); payload.Dispose();
        // Count-only snapshots never enter the game's array recycler.
        return false;
    }
    private static readonly FieldInfo PartAtlas = AccessTools.Field(typeof(TesselatedChunkPart), "atlasNumber");
    private static readonly FieldInfo PartPass = AccessTools.Field(typeof(TesselatedChunkPart), "pass");
    private static readonly FieldInfo PartLod = AccessTools.Field(typeof(TesselatedChunkPart), "modelDataLod1");

    private static void AtlasChanged(ChunkTesselator __instance)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(world, Game(__instance))) return;
            LightingOwners.Remove(__instance);
            atlasGeneration++; generation++; RetirePending("atlas-changed"); OrderedCpu.Clear(); world?.RedrawAllBlocks();
        }
    }
    private static bool Publishable(TesselatedChunk tesschunk, ClientChunk hostChunk, out bool __state)
    {
        // Keep validation and every part's publication atomic with unload/live
        // invalidation. The finalizer releases this even when stock pooling fails.
        __state = false;
        skippedPublication = false;
        var parts = (Centers(tesschunk) ?? []).Concat(Edges(tesschunk) ?? []);
        if (!parts.Any(part => PartLod.GetValue(part) is MeshData data && Payloads.TryGetValue(data, out _))) return true;
        Monitor.Enter(Gate); __state = true;
        foreach (var part in parts)
            if (PartLod.GetValue(part) is MeshData data && Payloads.TryGetValue(data, out var payload) &&
                (payload.IsDisposed || payload.Generation != generation || payload.AtlasGeneration != atlasGeneration ||
                 !ReferenceEquals(SourceChunk(tesschunk), hostChunk)))
            {
                if (payload.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(payload.ProbeBatch, "publication-rejected",
                    new { payload.Range, staleAtlas = payload.AtlasGeneration != atlasGeneration,
                        differentChunkOwner = !ReferenceEquals(SourceChunk(tesschunk), hostChunk) });
                RetireParts(Centers(tesschunk), "publication-rejected"); RetireParts(Edges(tesschunk), "publication-rejected");
                UnusedDispose.Invoke(tesschunk, null);
                Centers(tesschunk) = null!; Edges(tesschunk) = null!;
                // A reloaded chunk at these coordinates is a different owner.
                // Keep its visible pools, just as for an obsolete atlas, and
                // ask the normal scheduler for a current replacement.
                world?.RedrawAllBlocks();
                skippedPublication = true;
                return false;
            }
        return true;
    }
    private static void ResetPublication()
    {
        skippedPublication = false;
        lock (Gate)
            foreach (var payload in Pending.Where(p => p.Chunk.Disposed).ToArray()) payload.Dispose("chunk-unloaded");
    }
    private static IEnumerable<CodeInstruction> CallbackTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(ChunkCallback))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(GpuChunkMeshing), nameof(PublicationCallback));
            }
            yield return instruction;
        }
    }
    private static void PublicationCallback(ClientEventManager events, Vec3i position, ClientChunk chunk)
    {
        bool skip = skippedPublication; skippedPublication = false;
        if (!skip) events.TriggerChunkRetesselated(position, chunk);
    }
}
