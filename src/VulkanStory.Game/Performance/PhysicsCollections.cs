using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.Server;

namespace VulkanStory.Game.Performance;

/// <summary>Reuses physics client lists only after all queued readers finish. Reserves activation-list space in one batch
/// without delaying physics activation or changing simulation cadence.</summary>
internal static class PhysicsCollections
{
    private sealed class State { internal int Readers; }
    private sealed class Lease(State state, Action action)
    {
        private int released;
        internal void Invoke() { try { action(); } finally { Release(); } }
        internal void Release() { if (Interlocked.Exchange(ref released, 1) == 0) Interlocked.Decrement(ref state.Readers); }
    }
    private static ConditionalWeakTable<PhysicsManager, State> states = new();
    private static readonly AccessTools.FieldRef<PhysicsManager, List<ConnectedClient>> Clients = AccessTools.FieldRefAccess<PhysicsManager, List<ConnectedClient>>("ClientList");
    private static readonly AccessTools.FieldRef<PhysicsManager, List<IPhysicsTickable>> Tickables = AccessTools.FieldRefAccess<PhysicsManager, List<IPhysicsTickable>>("tickables");
    private static System.Func<object, PhysicsManager>? owner;
    private static MethodInfo? queue;
    private static MethodInfo Build() => AccessTools.Method(typeof(PhysicsManager), "BuildClientList")!;
    private static readonly ConstructorInfo ListConstructor = typeof(List<ConnectedClient>).GetConstructor(Type.EmptyTypes)!;

    internal static PerformanceFeature Create() => new("physics-collections", s => s.PhysicsCollectionReuse || s.PhysicsActivationBatch, () =>
    {
        Type helper = AccessTools.Inner(typeof(PhysicsManager), "PhysicsOffthreadTasks") ?? throw new TypeLoadException("PhysicsOffthreadTasks");
        queue = AccessTools.Method(helper, "QueueAsyncTask", [typeof(Action)]) ?? throw new MissingMethodException(helper.FullName, "QueueAsyncTask");
        owner = ServerTypes.Getter<PhysicsManager>(helper, "physicsManager");
        if (PatchProcessor.GetOriginalInstructions(Build()).Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, ListConstructor)) != 1)
            throw new InvalidOperationException("Physics client-list construction anchor changed.");
    }, harmony =>
    {
        harmony.Patch(Build(), transpiler: new HarmonyMethod(typeof(PhysicsCollections), nameof(Transpiler)));
        harmony.Patch(queue!, prefix: new HarmonyMethod(typeof(PhysicsCollections), nameof(QueueReader)),
            finalizer: new HarmonyMethod(typeof(PhysicsCollections), nameof(QueueFailure)));
        harmony.Patch(AccessTools.Method(typeof(PhysicsManager), "ServerTick", [typeof(float)]),
            prefix: new HarmonyMethod(typeof(PhysicsCollections), nameof(ReserveActivations)));
    }, WorldLeft: () => states = new());

    private static void QueueReader(object __instance, ref Action a, out Lease __state)
    {
        // Track readers even while reuse is off: turning the switch on cannot invalidate a previously queued read.
        State state = states.GetOrCreateValue(owner!(__instance));
        Interlocked.Increment(ref state.Readers);
        __state = new Lease(state, a);
        a = __state.Invoke;
    }
    private static void QueueFailure(Exception? __exception, Lease __state)
    { if (__exception != null) __state.Release(); }
    private static void ReserveActivations(PhysicsManager __instance)
    {
        if (PerformancePatchSet.Current?.PhysicsActivationBatch != true || __instance.toAdd.IsEmpty) return;
        List<IPhysicsTickable> list = Tickables(__instance);
        int queued = __instance.toAdd.Count;
        list.EnsureCapacity(list.Count + Math.Min(queued, int.MaxValue - list.Count));
        // The original drains EVERY activation this tick. The donor's 50-entity cap is intentionally corrected here.
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, ListConstructor))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PhysicsCollections), nameof(ClientList));
            }
            yield return instruction;
        }
    }
    private static List<ConnectedClient> ClientList(PhysicsManager manager)
    {
        List<ConnectedClient>? list = Clients(manager);
        State state = states.GetOrCreateValue(manager);
        if (PerformancePatchSet.Current?.PhysicsCollectionReuse == true && list != null && Volatile.Read(ref state.Readers) == 0)
        { list.Clear(); return list; }
        return new List<ConnectedClient>();
    }
}
