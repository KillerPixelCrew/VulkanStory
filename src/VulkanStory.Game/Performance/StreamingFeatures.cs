using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>S1: skips re-prioritising and re-sorting the tessellated upload queue when nothing that feeds the priorities changed.</summary>
/// <remarks>Priorities depend only on the player's block X/Z, camera yaw and the queued chunks. Enqueue always advances the queue tail and
/// dequeue never moves it, so an unchanged tail, backing array and player state means the remaining entries are still sorted.</remarks>
internal static class UploadQueueSortSkip
{
    private sealed class State
    {
        internal bool Valid, SkipSort;
        internal int Tail, X, Z;
        internal float Yaw;
        internal TesselatedChunk[]? Items;
    }
    private static readonly ConditionalWeakTable<SortableQueue<TesselatedChunk>, State> States = new();
    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> Game = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
    private static readonly AccessTools.FieldRef<SortableQueue<TesselatedChunk>, int> Tail =
        AccessTools.FieldRefAccess<SortableQueue<TesselatedChunk>, int>("tail");
    private static readonly AccessTools.FieldRef<SortableQueue<TesselatedChunk>, TesselatedChunk[]> Items =
        AccessTools.FieldRefAccess<SortableQueue<TesselatedChunk>, TesselatedChunk[]>("array");
    private static readonly MethodInfo RunEach = AccessTools.Method(typeof(SortableQueue<TesselatedChunk>), nameof(SortableQueue<TesselatedChunk>.RunForEach))!;
    private static readonly MethodInfo SortQueue = AccessTools.Method(typeof(SortableQueue<TesselatedChunk>), nameof(SortableQueue<TesselatedChunk>.Sort))!;
    private static MethodInfo Target() => AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame), [typeof(float)]) ??
        throw new MissingMethodException(typeof(ChunkTesselatorManager).FullName, "OnBeforeFrame");

    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        int each = body.Count(instruction => instruction.Calls(RunEach)), sort = body.Count(instruction => instruction.Calls(SortQueue));
        int first = body.ToList().FindIndex(instruction => instruction.Calls(RunEach));
        if (each != 1 || sort != 1 || body.ToList().FindIndex(instruction => instruction.Calls(SortQueue)) < first)
            throw new InvalidOperationException("Upload queue priority/sort anchors changed.");
    }

    internal static PerformanceFeature Create() => new("upload-queue-sort-skip", settings => settings.UploadQueueSortSkip,
        () => Check(PatchProcessor.GetOriginalInstructions(Target())),
        harmony => harmony.Patch(Target(), transpiler: new HarmonyMethod(typeof(UploadQueueSortSkip), nameof(Transpiler))));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body);
        foreach (var instruction in body)
        {
            if (instruction.Calls(RunEach))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(UploadQueueSortSkip), nameof(Prioritize));
            }
            else if (instruction.Calls(SortQueue))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(UploadQueueSortSkip), nameof(Sort)); }
            yield return instruction;
        }
    }

    // Both helpers run on the main thread inside the original tessChunksQueueLock.
    private static void Prioritize(SortableQueue<TesselatedChunk> queue, Action<TesselatedChunk> recalc, ChunkTesselatorManager manager)
    {
        State state = States.GetValue(queue, _ => new State());
        var entity = Game(manager).player?.Entity;
        float yaw = Game(manager).player?.CameraYaw ?? 0f;
        if (PerformancePatchSet.Current?.UploadQueueSortSkip == true && entity != null && state.Valid &&
            Tail(queue) == state.Tail && ReferenceEquals(Items(queue), state.Items) &&
            (int)entity.Pos.X == state.X && (int)entity.Pos.Z == state.Z && yaw == state.Yaw)
        {
            state.SkipSort = true;
            Interlocked.Increment(ref PerformanceCounters.QueueSortsSkipped);
            return;
        }
        state.SkipSort = false;
        queue.RunForEach(recalc);
        state.Valid = entity != null;
        if (entity != null) { state.X = (int)entity.Pos.X; state.Z = (int)entity.Pos.Z; state.Yaw = yaw; }
    }

    private static void Sort(SortableQueue<TesselatedChunk> queue)
    {
        State state = States.GetValue(queue, _ => new State());
        if (state.SkipSort) { state.SkipSort = false; return; }
        queue.Sort();
        Interlocked.Increment(ref PerformanceCounters.QueueSortsRun);
        // Sort may compact a wrapped queue; record the layout it leaves behind.
        state.Tail = Tail(queue); state.Items = Items(queue);
    }
}

/// <summary>S2: recycles the custom attribute parts (CustomInts/Shorts/Floats) of tessellated chunk meshes.</summary>
/// <remarks>The game's MeshDataRecycler already pools the large xyz/uv/rgba/flags/index arrays; these parts were the remaining
/// per-chunk allocations. Clones happen on the single tessellation thread; parts come back from the main thread right after
/// <c>AddModel</c> copied them (the Vulkan upload copies synchronously) and just before the game disposes the mesh.</remarks>
internal static class MeshPartPool
{
    private const int MaxFreePerType = 128;

    /// <summary>One part type's free list (tessellation thread) and return queue (main thread).</summary>
    private sealed class Pool<TPart, T>(Action<TPart> resetConversion) where TPart : CustomMeshDataPart<T>, new()
    {
        private readonly ConcurrentQueue<TPart> returned = new();
        private readonly List<TPart> free = new();
        private volatile bool clearRequested;

        internal void Return(TPart? part) { if (part != null) returned.Enqueue(part); }

        /// <summary>Drops pooled parts; the free list itself is cleared by its owning tessellation thread on its next clone.</summary>
        internal void Drain() { while (returned.TryDequeue(out _)) { } clearRequested = true; }

        /// <summary>Copies <paramref name="source" /> like the original part clone, reusing a returned part when one fits.</summary>
        /// <returns>Null when the source does not qualify; the caller then uses the original clone.</returns>
        internal TPart? Clone(TPart source)
        {
            // Only automatic allocation sizes are reproduced exactly by a reused part.
            if (source.Values == null || source.AllocationSize != source.Count) return null;
            if (clearRequested) { clearRequested = false; free.Clear(); }
            while (returned.TryDequeue(out var back)) if (free.Count < MaxFreePerType) free.Add(back);
            int best = -1;
            for (int index = 0; index < free.Count; index++)
            {
                int length = free[index].Values?.Length ?? 0;
                if (length >= source.Count && (best < 0 || length < free[best].Values!.Length)) best = index;
            }
            TPart part;
            if (best >= 0)
            {
                part = free[best]; free[best] = free[^1]; free.RemoveAt(free.Count - 1);
                Interlocked.Increment(ref PerformanceCounters.MeshPartsReused);
            }
            else { part = new TPart(); Interlocked.Increment(ref PerformanceCounters.MeshPartsAllocated); }
            part.AutoAllocationSize();
            part.Count = source.Count;
            if (part.Values == null || part.Values.Length < source.Count) part.Values = new T[source.Values.Length];
            Array.Copy(source.Values, part.Values, source.Count);
            part.InterleaveSizes = CopyInto(source.InterleaveSizes, part.InterleaveSizes);
            part.InterleaveOffsets = CopyInto(source.InterleaveOffsets, part.InterleaveOffsets);
            part.InterleaveStride = source.InterleaveStride;
            part.Instanced = source.Instanced;
            part.StaticDraw = source.StaticDraw;
            part.BaseOffset = source.BaseOffset;
            // The original clone does not copy Conversion; a fresh part keeps its type default.
            resetConversion(part);
            return part;
        }

        private static int[]? CopyInto(int[]? source, int[]? existing)
        {
            if (source == null) return null;
            if (existing == null || existing.Length != source.Length) return (int[])source.Clone();
            Array.Copy(source, existing, source.Length);
            return existing;
        }
    }

    private static readonly DataConversion IntDefault = new CustomMeshDataPartInt().Conversion;
    private static readonly DataConversion ShortDefault = new CustomMeshDataPartShort().Conversion;
    private static readonly Pool<CustomMeshDataPartInt, int> Ints = new(part => part.Conversion = IntDefault);
    private static readonly Pool<CustomMeshDataPartShort, short> Shorts = new(part => part.Conversion = ShortDefault);
    private static readonly Pool<CustomMeshDataPartFloat, float> Floats = new(_ => { });
    private static readonly MethodInfo CloneRecycled = AccessTools.Method(typeof(MeshData), nameof(MeshData.CloneUsingRecycler), Type.EmptyTypes)!;
    private static MethodInfo Populate() => AccessTools.Method(typeof(ChunkTesselator), "populateTesselatedChunkPart") ??
        throw new MissingMethodException(typeof(ChunkTesselator).FullName, "populateTesselatedChunkPart");
    private static MethodInfo Store() => AccessTools.Method(typeof(TesselatedChunkPart), "AddModelAndStoreLocation") ??
        throw new MissingMethodException(typeof(TesselatedChunkPart).FullName, "AddModelAndStoreLocation");

    internal static PerformanceFeature Create() => new("mesh-part-pool", settings => settings.MeshPartPool, () =>
    {
        if (PatchProcessor.GetOriginalInstructions(Populate()).Count(instruction => instruction.Calls(CloneRecycled)) != 4)
            throw new InvalidOperationException("Chunk part clone anchors changed.");
        if (!Store().GetParameters().Any(parameter => parameter.Name == "modeldata" && parameter.ParameterType == typeof(MeshData)))
            throw new InvalidOperationException("Chunk part upload signature changed.");
    }, harmony =>
    {
        harmony.Patch(Populate(), transpiler: new HarmonyMethod(typeof(MeshPartPool), nameof(Transpiler)));
        harmony.Patch(Store(), postfix: new HarmonyMethod(typeof(MeshPartPool), nameof(Reclaim)));
    }, WorldLeft: () => { Ints.Drain(); Shorts.Drain(); Floats.Drain(); });

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(CloneRecycled))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(MeshPartPool), nameof(Clone)); }
            yield return instruction;
        }
    }

    // Tessellation thread. The source is the tessellator's own working mesh, so its parts can be detached for the clone.
    private static MeshData Clone(MeshData source)
    {
        if (PerformancePatchSet.Current?.MeshPartPool != true) return source.CloneUsingRecycler();
        var ints = source.CustomInts; var shorts = source.CustomShorts; var floats = source.CustomFloats;
        source.CustomInts = null; source.CustomShorts = null; source.CustomFloats = null;
        MeshData copy;
        try { copy = source.CloneUsingRecycler(); }
        finally { source.CustomInts = ints; source.CustomShorts = shorts; source.CustomFloats = floats; }
        if (ints != null) copy.CustomInts = Ints.Clone(ints) ?? ints.Clone();
        if (shorts != null) copy.CustomShorts = Shorts.Clone(shorts) ?? shorts.Clone();
        if (floats != null) copy.CustomFloats = Floats.Clone(floats) ?? floats.Clone();
        return copy;
    }

    // Main thread, after AddModel copied the mesh and before TesselatedChunkPart.Dispose drops the parts.
    private static void Reclaim(MeshData modeldata)
    {
        if (PerformancePatchSet.Current?.MeshPartPool != true || modeldata == null) return;
        Ints.Return(modeldata.CustomInts); Shorts.Return(modeldata.CustomShorts); Floats.Return(modeldata.CustomFloats);
    }
}
