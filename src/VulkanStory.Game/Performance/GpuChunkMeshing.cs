using System.Collections.Concurrent;
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
    private static ClientMain? world;
    [ThreadStatic] private static Work? current;
    [ThreadStatic] private static bool skippedPublication;
    private static readonly ConcurrentDictionary<Type, bool> AoTypes = new();
    private static readonly ConditionalWeakTable<MeshData, Payload> Payloads = new();
    private static readonly HashSet<Payload> Pending = new();
    private static readonly AccessTools.FieldRef<ChunkTesselator, Block[]> Solid = AccessTools.FieldRefAccess<ChunkTesselator, Block[]>("currentChunkBlocksExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, Block[]> Fluid = AccessTools.FieldRefAccess<ChunkTesselator, Block[]>("currentChunkFluidBlocksExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, int[]> Lights = AccessTools.FieldRefAccess<ChunkTesselator, int[]>("currentChunkRgbsExt");
    private static readonly AccessTools.FieldRef<ChunkTesselator, ClientMain> Game = AccessTools.FieldRefAccess<ChunkTesselator, ClientMain>("game");
    private static readonly AccessTools.FieldRef<TesselatedChunk, TesselatedChunkPart[]> Centers = AccessTools.FieldRefAccess<TesselatedChunk, TesselatedChunkPart[]>("centerParts");
    private static readonly AccessTools.FieldRef<TesselatedChunk, TesselatedChunkPart[]> Edges = AccessTools.FieldRefAccess<TesselatedChunk, TesselatedChunkPart[]>("edgeParts");
    private static readonly AccessTools.FieldRef<TesselatedChunk, ClientChunk> SourceChunk = AccessTools.FieldRefAccess<TesselatedChunk, ClientChunk>("chunk");
    private static readonly FieldInfo CubeHeight = AccessTools.Field(typeof(CubeTesselator), "blockHeight");
    private static readonly FieldInfo BleedingHeight = AccessTools.Field(typeof(BleedingCubeTesselator), "blockHeight");
    private static readonly MethodInfo Process = AccessTools.Method(typeof(ChunkTesselator), "NowProcessChunk",
        [typeof(int), typeof(int), typeof(int), typeof(TesselatedChunk), typeof(bool)]);
    private static readonly MethodInfo BeginProcess = AccessTools.Method(typeof(ChunkTesselator), "BeginProcessChunk");
    private static readonly MethodInfo Cube = AccessTools.Method(typeof(CubeTesselator), "Tesselate", [typeof(TCTCache)]);
    private static readonly MethodInfo Bleeding = AccessTools.Method(typeof(BleedingCubeTesselator), "Tesselate", [typeof(TCTCache)]);
    private static readonly MethodInfo UnusedDispose = AccessTools.Method(typeof(TesselatedChunk), "UnusedDispose");
    private static readonly MethodInfo Visible = AccessTools.Method(typeof(ChunkTesselator), "CalculateVisibleFaces",
        [typeof(bool), typeof(int), typeof(int), typeof(int)]);
    private static readonly FieldInfo CullField = AccessTools.Field(typeof(Block), nameof(Block.FaceCullMode));
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
     AccessTools.Method(typeof(BleedingTextureHelper), nameof(BleedingTextureHelper.ComputeBleedForFace))];

    private sealed class Work(ChunkTesselator producer, IChunkMeshBatch batch, int epoch)
    {
        internal readonly ChunkTesselator Producer = producer;
        internal readonly IChunkMeshBatch Batch = batch;
        internal readonly int Generation = epoch;
        internal readonly int AtlasGeneration = atlasGeneration;
        internal readonly List<ChunkMeshFace> Faces = new();
        internal readonly List<ChunkMeshBin> Bins = new();
        internal readonly HashSet<Block> CpuOnly = new(ReferenceEqualityComparer.Instance);
        internal ChunkMeshVoxel[]? Voxels;
        internal long ProbeBatch;
        internal bool Active;
    }
    internal sealed class Payload(IChunkMeshBatch batch, ChunkMeshRange range, int epoch, int atlasEpoch) : IDisposable
    {
        internal readonly IChunkMeshBatch Batch = batch;
        internal readonly ChunkMeshRange Range = range;
        internal readonly int Generation = epoch;
        internal readonly int AtlasGeneration = atlasEpoch;
        internal long ProbeBatch;
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(ProbeBatch, "payload-disposed", new { Range });
            lock (Gate) Pending.Remove(this);
            Batch.Dispose();
            GC.SuppressFinalize(this);
        }
        ~Payload() { Dispose(); }
    }

    internal static void Configure(VulkanDevice device, bool requested)
    {
        lock (Gate)
        {
            bool next = requested && !faulted && device.ChunkMeshingAvailable;
            if (ReferenceEquals(backend, device) && enabled == next) return;
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
            pending = Pending.ToArray();
        }
        foreach (var payload in pending) payload.Dispose();
        if (HeadlessChunkMeshingProbe.Active) HeadlessChunkMeshingProbe.WorldLeft();
    }
    internal static bool TryPayload(MeshData data, out Payload payload) => Payloads.TryGetValue(data, out payload!);

    internal static PerformanceFeature Create() => new("gpu-chunk-meshing", s => s.GpuChunkMeshing, Validate, harmony =>
    {
        // ClientMain.Start creates this manager before its terrain workers run.
        // Attach at that lifecycle boundary, never from a tessellation worker.
        harmony.Patch(ManagerStart, postfix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(LoadingWorld)));
        harmony.Patch(ManagerDispose, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(LeavingWorld)));
        harmony.Patch(Process, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Start)),
            postfix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Finish)),
            finalizer: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(End)));
        harmony.Patch(BeginProcess, postfix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Snapshot)));
        harmony.Patch(Visible, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(PrepareVisibility)),
            transpiler: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(VisibilityTranspiler)));
        harmony.Patch(Cube, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(CaptureCube)));
        harmony.Patch(Bleeding, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(CaptureCube)));
        harmony.Patch(Insert, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Reserve)));
        harmony.Patch(AccessTools.Method(typeof(MeshData), nameof(MeshData.Dispose)),
            prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Release)));
        harmony.Patch(AccessTools.Method(typeof(ChunkTesselator), nameof(ChunkTesselator.BlockTexturesLoaded)),
            prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(AtlasChanged)));
        harmony.Patch(AccessTools.Method(typeof(ChunkRenderer), nameof(ChunkRenderer.AddTesselatedChunk)),
            prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Publishable)));
        harmony.Patch(BeforeFrame, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(ResetPublication)),
            transpiler: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(CallbackTranspiler)));
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
            generation++; atlasGeneration++;
            // Only a missed loading attachment needs this fallback rebuild.
            // The normal WorldReady event preserves jobs for the same owner.
            if (remesh && enabled) next.RedrawAllBlocks();
        }
        foreach (var payload in retired) payload.Dispose();
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
            UnusedDispose == null || Visible?.ReturnType != typeof(bool) || Insert?.ReturnType != typeof(ModelDataPoolLocation) ||
            ManagerStart == null || ManagerDispose?.ReturnType != typeof(void))
            throw new InvalidOperationException("Official chunk meshing signatures changed.");
        foreach (var method in new[] { Process, BeginProcess, Cube, Bleeding, Insert, Visible })
            if (method.GetMethodBody() == null || Harmony.GetPatchInfo(method)?.Owners.Any(o => !PerformancePatchSet.IsOwnOwner(o)) == true)
                throw new InvalidOperationException("Chunk producer/pool has an external replacement.");
        // Validate the physical ABI used by the unchanged SSBO terrain shaders.
        if (Marshal.SizeOf<FaceData>() != 64 || Marshal.SizeOf<ChunkMeshFace>() != 64 || Marshal.SizeOf<ChunkMeshVoxel>() != 32)
            throw new InvalidOperationException("Chunk face ABI changed.");
        ValidatePool();
        if (PatchProcessor.GetOriginalInstructions(Visible).Count(i => i.LoadsField(CullField)) != 1)
            throw new InvalidOperationException("Stock solid visibility anchor changed.");
        if (BeforeFrame?.ReturnType != typeof(void) || ChunkCallback?.ReturnType != typeof(void) ||
            PatchProcessor.GetOriginalInstructions(BeforeFrame).Count(i => i.Calls(ChunkCallback)) != 2)
            throw new InvalidOperationException("Stock chunk publication callbacks changed.");
    }

    private static void Start(ChunkTesselator __instance, out Work? __state)
    {
        __state = current; current = null;
        lock (Gate)
        {
            ClientMain game = Game(__instance);
            if (!ReferenceEquals(world, game) || !enabled || backend == null || !game.Platform.UseSSBOs ||
                PerformancePatchSet.Current?.GpuChunkMeshing != true) return;
            // A late foreign producer patch keeps its original path.
            foreach (MethodInfo method in new[] { Cube, Bleeding, BeginProcess, Process, Insert, Visible })
                if (Harmony.GetPatchInfo(method)?.Owners.Any(o => !PerformancePatchSet.IsOwnOwner(o)) == true) return;
            foreach (MethodInfo method in StockPolicies)
                if (Harmony.GetPatchInfo(method)?.Owners.Any(o => !PerformancePatchSet.IsOwnOwner(o)) == true) return;
            IChunkMeshBatch? lease;
            try { lease = backend.LeaseChunkMeshing(); }
            catch (Exception error)
            {
                enabled = false; faulted = true;
                game.Logger.Warning("VulkanStory: GPU chunk meshing allocation refused: {0}", error.Message);
                return;
            }
            if (lease == null) return;
            current = new Work(__instance, lease, generation);
        }
    }
    private static void Snapshot(ChunkTesselator __instance, int __0, int __1, int __2, ClientChunk __3, bool __4, bool __result)
    {
        Work? work = current;
        if (HeadlessChunkMeshingProbe.Active)
        {
            long probe = HeadlessChunkMeshingProbe.Snapshot(Game(__instance), __0, __1, __2, __3,
                __result, work != null && ReferenceEquals(work.Producer, __instance) ? work.Batch : null,
                __4, Solid(__instance), Fluid(__instance));
            if (work != null && ReferenceEquals(work.Producer, __instance)) work.ProbeBatch = probe;
        }
        if (!__result || work == null || !ReferenceEquals(work.Producer, __instance)) return;
        Block[] solids = Solid(__instance), fluids = Fluid(__instance); int[] light = Lights(__instance);
        if (solids.Length != 39304 || fluids.Length != solids.Length || light.Length != solids.Length) return;
        var voxels = new ChunkMeshVoxel[solids.Length];
        for (int i = 0; i < voxels.Length; i++)
        {
            Block b = solids[i], f = fluids[i];
            if (b == null || f == null) return;
            voxels[i] = new ChunkMeshVoxel { Identity = b.Id, Material = (int)b.BlockMaterial,
                Opaque = Bits(b.SideOpaque), Solid = Bits(b.SideSolid), EmitAo = (uint)b.EmitSideAo,
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
    private static bool StockAo(Type type) => AoTypes.GetOrAdd(type, t =>
        AccessTools.Method(t, nameof(Block.DoEmitSideAo))?.DeclaringType == typeof(Block) &&
        AccessTools.Method(t, nameof(Block.DoEmitSideAoByFlag))?.DeclaringType == typeof(Block));

    private static bool SupportedProducer(ChunkTesselator producer, Block block)
    {
        int draw = (int)block.DrawType;
        // Atomic bin compaction is safe for depth-writing opaque passes. Blended
        // and OIT producers retain their original per-face emission order.
        if (draw is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 10 or 12) || (uint)block.FaceCullMode > 5 ||
            block.RenderPass is not (EnumChunkRenderPass.Opaque or EnumChunkRenderPass.OpaqueNoCull)) return false;
        Type type = Producers(producer)[draw].GetType();
        return type == typeof(CubeTesselator) || type == typeof(BleedingCubeTesselator);
    }

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
        current is { } work && ReferenceEquals(work.Producer, producer) && !work.CpuOnly.Contains(block) && SupportedProducer(producer, block)
            ? EnumFaceCullMode.NeverCull : block.FaceCullMode;

    private static void PrepareVisibility(ChunkTesselator __instance)
    {
        Work? work = current;
        if (work == null || !ReferenceEquals(work.Producer, __instance)) return;
        Block[] blocks = Solid(__instance);
        // Never duplicate a virtual AllowSnowCoverage callback. If any cell of
        // this block identity needs that policy, its original producer retains
        // CPU culling throughout this chunk; other stock identities can offload.
        for (int y = 1; y <= 32; y++)
            for (int z = 1; z <= 32; z++)
                for (int x = 1; x <= 32; x++)
                {
                    int i = (y * 34 + z) * 34 + x;
                    if (blocks[i + 1156]?.DrawType == EnumDrawType.JSONAndSnowLayer && blocks[i] is { } block)
                        work.CpuOnly.Add(block);
                }
    }

    // Used only when an admitted producer cannot fit or its particular cell has
    // custom AO/fluid/shape policy. GPU-admitted cells never run these CPU tests.
    private static int CpuFaces(TCTCache vars)
    {
        int mask = 0; Block a = vars.block; Block[] blocks = Solid(vars.tct);
        for (int side = 0; side < 6; side++)
        {
            Block b = blocks[vars.extIndex3d + TileSideEnum.MoveIndex[side]];
            bool opaque = a.SideOpaque[side], other = b.SideOpaque[TileSideEnum.GetOpposite(side)];
            if (side == 4 && b.DrawType == EnumDrawType.JSONAndSnowLayer && other &&
                !a.AllowSnowCoverage(Game(vars.tct), new BlockPos(vars.posX, vars.posY, vars.posZ))) other = false;
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
        if (current is { } work && ReferenceEquals(work.Producer, vars.tct) && !vars.isDecorTesselate &&
            !work.CpuOnly.Contains(vars.block) && ReferenceEquals(vars.block, Solid(vars.tct)[vars.extIndex3d]) &&
            SupportedProducer(vars.tct, vars.block)) vars.drawFaceFlags &= CpuFaces(vars);
        return true;
    }

    private static bool CaptureCube(object __instance, TCTCache __0)
    {
        Work? work = current; TCTCache vars = __0;
        if (work?.Active != true || !ReferenceEquals(vars.tct, work.Producer) || vars.isDecorTesselate ||
            vars.occ != 0.67f || vars.halfoccInverted != 0.0196875f ||
            vars.IsDecorOnJson || !ReferenceEquals(vars.blockFaceVertices, CubeFaceVertices.blockFaceVertices) ||
            work.CpuOnly.Contains(vars.block) || !ReferenceEquals(vars.block, Solid(vars.tct)[vars.extIndex3d]) || !SupportedProducer(vars.tct, vars.block) ||
            __instance.GetType() != typeof(CubeTesselator) && __instance.GetType() != typeof(BleedingCubeTesselator) ||
            vars.RenderPass == EnumChunkRenderPass.Liquid || vars.RenderPass == EnumChunkRenderPass.TopSoil ||
            (uint)vars.block.FaceCullMode > 5 || work.Faces.Count + 6 > work.Batch.FaceCapacity ||
            (int)vars.block.DrawType is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 10 or 12)) return Cpu(vars);
        Block[] blocks = Solid(vars.tct), fluids = Fluid(vars.tct);
        // Position-dependent/custom AO remains CPU. The full 3x3x3 producer
        // neighborhood must use stock AO; absorbing liquids keep the stock path.
        for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
                for (int x = -1; x <= 1; x++)
                {
                    int i = vars.extIndex3d + y * 1156 + z * 34 + x;
                    if (!StockAo(blocks[i].GetType()) || fluids[i].LightAbsorption > 0) return Cpu(vars);
                }
        // The source game invokes a virtual snow-coverage policy for this pair.
        // Keep it entirely on the CPU, including its already-applied decor mask.
        if (blocks[vars.extIndex3d + 1156].DrawType == EnumDrawType.JSONAndSnowLayer) return Cpu(vars);
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
                    if (work.Bins.Count >= 128) { work.Faces.RemoveRange(rollback, work.Faces.Count - rollback); return Cpu(vars); }
                    bin = work.Bins.Count; work.Bins.Add(key);
                }
                work.Faces.Add(new ChunkMeshFace { X = vars.finalX, Y = vars.finalY, Z = vars.finalZ, Height = height,
                    U1 = texture.x1, V1 = texture.y1, U2 = texture.x2, V2 = texture.y2,
                    Index = (uint)vars.extIndex3d, Side = (uint)side, Bin = (uint)bin,
                    Flags = (uint)(vars.VertexFlags | BlockFacing.ALLFACES[side].NormalPackedFlags),
                    ColorMap = (uint)vars.ColorMapData.Value, CullMode = (uint)vars.block.FaceCullMode,
                    Smooth = vars.aoAndSmoothShadows && vars.block.SideAo[side] ? 1u : 0u,
                    Padding = (uint)EnumBlockMaterial.Leaves });
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
        if (work?.Active != true || !ReferenceEquals(work.Producer, __instance) || work.Faces.Count == 0) return;
        lock (Gate)
        {
            if (work.Generation != generation)
            {
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "generation-rejected",
                    new { captured = work.Generation, current = generation });
                UnusedDispose.Invoke(tessChunk, null); throw new RetryTesselationException("GPU chunk generation changed.");
            }
            ChunkMeshRange[] ranges;
            try { ranges = work.Batch.Generate(work.Voxels!, CollectionsMarshal.AsSpan(work.Faces), Neighbours(), work.Bins.Count); }
            catch (Exception error)
            {
                if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "generation-failed", new { error = error.Message });
                // No partial chunk can reach callbacks/publication. Requeue once
                // with the original CPU producers if backend generation failed.
                enabled = false; faulted = true; generation++; UnusedDispose.Invoke(tessChunk, null);
                Game(__instance).Logger.Warning("VulkanStory: GPU chunk meshing generation failed: {0}", error.Message);
                throw new RetryTesselationException("GPU chunk generation failed; retry with stock producers.");
            }
            if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Generated(work.ProbeBatch, ranges, work.Bins.ToArray());
            var center = new List<TesselatedChunkPart>(); var edge = new List<TesselatedChunkPart>();
            foreach (var range in ranges)
            {
                var bin = work.Bins[range.Bin];
                var data = new MeshData(false) { VerticesCount = range.FaceCount * 4, IndicesCount = range.FaceCount * 6,
                    CustomInts = new CustomMeshDataPartInt() };
                work.Batch.Retain();
                var payload = new Payload(work.Batch, range, generation, work.AtlasGeneration) { ProbeBatch = work.ProbeBatch };
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
    private static Exception? End(Exception? __exception, Work? __state)
    {
        Work? work = current; current = __state;
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
            atlasGeneration++; generation++; world?.RedrawAllBlocks();
        }
    }
    private static bool Publishable(TesselatedChunk tesschunk, ClientChunk hostChunk)
    {
        lock (Gate)
        {
            skippedPublication = false;
            foreach (var part in (Centers(tesschunk) ?? []).Concat(Edges(tesschunk) ?? []))
                if (PartLod.GetValue(part) is MeshData data && Payloads.TryGetValue(data, out var payload) &&
                    (payload.AtlasGeneration != atlasGeneration || !ReferenceEquals(SourceChunk(tesschunk), hostChunk)))
                {
                    if (payload.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(payload.ProbeBatch, "publication-rejected",
                        new { payload.Range, staleAtlas = payload.AtlasGeneration != atlasGeneration,
                            differentChunkOwner = !ReferenceEquals(SourceChunk(tesschunk), hostChunk) });
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
    }
    private static void ResetPublication() => skippedPublication = false;
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
