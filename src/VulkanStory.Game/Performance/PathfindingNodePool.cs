using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;

namespace VulkanStory.Game.Performance;

/// <summary>Optimum's per-search node pool. Returned path nodes and their complete parent chains leave the pool permanently;
/// a subsequent search cannot mutate a path retained by the caller.</summary>
internal static class PathfindingNodePool
{
    private sealed class Pool
    {
        internal readonly PathNode?[] Nodes = new PathNode[4096];
        internal readonly HashSet<PathNode> Escaped = new(ReferenceEqualityComparer.Instance);
        internal int Used, ActiveThread, Depth;
    }
    private static ConditionalWeakTable<AStar, Pool> pools = new();
    private static readonly ConstructorInfo Start = typeof(PathNode).GetConstructor([typeof(BlockPos)])!;
    private static readonly ConstructorInfo Neighbour = typeof(PathNode).GetConstructor([typeof(PathNode), typeof(Cardinal)])!;
    private static MethodInfo Target() => AccessTools.Method(typeof(AStar), "FindPathOrEscapePath")!;
    internal static PerformanceFeature Create() => new("pathfinding-node-pool", s => s.PathfindingNodePool, () =>
    {
        var instructions = PatchProcessor.GetOriginalInstructions(Target());
        if (Target().ReturnType != typeof(List<PathNode>) || instructions.Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, Start)) != 2 ||
            instructions.Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, Neighbour)) != 1)
            throw new InvalidOperationException("Pathfinding node construction anchors changed.");
    }, harmony => harmony.Patch(Target(), prefix: new HarmonyMethod(typeof(PathfindingNodePool), nameof(Begin)),
        transpiler: new HarmonyMethod(typeof(PathfindingNodePool), nameof(Transpiler)),
        postfix: new HarmonyMethod(typeof(PathfindingNodePool), nameof(DetachResult)),
        finalizer: new HarmonyMethod(typeof(PathfindingNodePool), nameof(End))), WorldLeft: () => pools = new());

    private static void Begin(AStar __instance, out Pool? __state)
    {
        __state = null;
        if (PerformancePatchSet.Current?.PathfindingNodePool != true) return;
        Pool pool = pools.GetOrCreateValue(__instance);
        int thread = Environment.CurrentManagedThreadId;
        int active = Interlocked.CompareExchange(ref pool.ActiveThread, thread, 0);
        if (active != 0)
        {
            if (active == thread) { __state = pool; pool.Depth++; }
            return;
        }
        __state = pool; pool.Used = 0; pool.Depth = 1;
        // Clear hashed collections BEFORE pooled positions change, including the first two constructor calls.
        __instance.openSet.Clear(); __instance.closedSet.Clear();
    }
    private static void DetachResult(List<PathNode>? __result, Pool? __state)
    {
        if (__state == null || __state.Depth != 1 || __result == null) return;
        __state.Escaped.Clear();
        foreach (PathNode result in __result)
            for (PathNode? node = result; node is not null && __state.Escaped.Add(node); node = node.Parent) { }
        for (int i = 0; i < __state.Used; i++)
            if (__state.Nodes[i] is { } node && __state.Escaped.Contains(node)) __state.Nodes[i] = null;
        __state.Escaped.Clear();
    }
    private static void End(Pool? __state)
    {
        if (__state != null && --__state.Depth == 0) Volatile.Write(ref __state.ActiveThread, 0);
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Newobj && (Equals(instruction.operand, Start) || Equals(instruction.operand, Neighbour)))
            {
                bool start = Equals(instruction.operand, Start);
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PathfindingNodePool), start ? nameof(RentStart) : nameof(RentNeighbour));
            }
            yield return instruction;
        }
    }
    private static PathNode? Rent(AStar owner)
    {
        if (PerformancePatchSet.Current?.PathfindingNodePool != true || !pools.TryGetValue(owner, out var pool) ||
            pool.ActiveThread != Environment.CurrentManagedThreadId || pool.Depth != 1 || pool.Used >= pool.Nodes.Length) return null;
        int index = pool.Used++;
        PathNode node = pool.Nodes[index] ??= new PathNode(new BlockPos(0));
        node.gCost = 0; node.hCost = 0; node.Parent = null; node.pathLength = 0; node.HeapIndex = 0;
        node.Action = default;
        return node;
    }
    private static PathNode RentStart(BlockPos position, AStar owner)
    {
        PathNode? node = Rent(owner);
        if (node is null) return new PathNode(position);
        node.Set(position.X, position.Y, position.Z); node.dimension = position.dimension;
        return node;
    }
    private static PathNode RentNeighbour(PathNode parent, Cardinal direction, AStar owner)
    {
        PathNode? node = Rent(owner);
        if (node is null) return new PathNode(parent, direction);
        node.Set(parent.X + direction.Normali.X, parent.Y + direction.Normali.Y, parent.Z + direction.Normali.Z);
        node.dimension = parent.dimension;
        return node;
    }
}
