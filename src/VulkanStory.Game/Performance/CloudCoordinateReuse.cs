using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;

namespace VulkanStory.Game.Performance;

/// <summary>Reuses the stock FluffyClouds tile-position scratch vector. Weather calculations and worker cadence remain original.</summary>
internal static class CloudCoordinateReuse
{
    private sealed class State
    {
        internal readonly Vec3d Position = new();
        internal int Thread, Hits;
    }
    private readonly record struct Scope(State? Previous, State? Acquired);
    private static ConditionalWeakTable<object, State> states = new();
    [ThreadStatic] private static State? current;
    private static MethodInfo? target;
    private static MethodInfo[] consumers = [];
    private static volatile bool safe;
    private static readonly ConstructorInfo Constructor = typeof(Vec3d).GetConstructor([typeof(double), typeof(double), typeof(double)])!;

    internal static PerformanceFeature Create(Assembly essentials) => new("cloud-coordinate-reuse", s => s.CloudCoordinateReuse && safe, () =>
    {
        Type renderer = essentials.GetType("FluffyClouds.CloudRendererMap", true)!;
        target = AccessTools.Method(renderer, "UpdateCloudTilesOffThread", [typeof(int)]);
        if (target == null || PatchProcessor.GetOriginalInstructions(target).Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, Constructor)) != 1)
            throw new InvalidOperationException("Cloud tile coordinate construction anchor changed.");
        consumers = new[] { "Vintagestory.GameContent.WeatherDataReaderBase", "Vintagestory.GameContent.WeatherDataReaderPreLoad" }
            .SelectMany(name => AccessTools.GetDeclaredMethods(essentials.GetType(name, true)!))
            .Where(method => (method.Name is "LoadAdjacentSims" or "LoadLerp") && method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Vec3d))
            .ToArray();
        if (consumers.Length != 3) throw new InvalidOperationException("Cloud position reader signatures changed.");
        CheckReaders();
    }, harmony => harmony.Patch(target!, prefix: new HarmonyMethod(typeof(CloudCoordinateReuse), nameof(Begin)),
        transpiler: new HarmonyMethod(typeof(CloudCoordinateReuse), nameof(Transpiler)),
        finalizer: new HarmonyMethod(typeof(CloudCoordinateReuse), nameof(End))),
        WorldReady: api =>
        {
            // Mods finish loading after early renderer installation. Recheck reader hooks before using the optimization in a world.
            try { CheckReaders(); safe = true; }
            catch (Exception error) { safe = false; api.Logger.Warning("VulkanStory: cloud coordinate reuse unavailable: {0}", error.Message); }
        }, WorldLeft: () => { safe = false; states = new(); });
    private static void CheckReaders()
    {
        foreach (MethodInfo method in consumers.Append(target!))
            if (Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true)
                throw new InvalidOperationException("A mod patches " + method.DeclaringType!.FullName + "." + method.Name);
    }
    private static void Begin(object __instance, out Scope __state)
    {
        State? acquired = null;
        if (safe && PerformancePatchSet.Current?.CloudCoordinateReuse == true)
        {
            State state = states.GetOrCreateValue(__instance);
            if (Interlocked.CompareExchange(ref state.Thread, Environment.CurrentManagedThreadId, 0) == 0)
            { acquired = state; state.Hits = 0; }
        }
        __state = new(current, acquired);
        current = acquired; // Nested/concurrent invocations take fresh constructor results, never another update's scratch.
    }
    private static void End(Scope __state)
    {
        current = __state.Previous;
        if (__state.Acquired is not { } state) return;
        Interlocked.Add(ref PerformanceCounters.CloudCoordinatesReused, state.Hits);
        Volatile.Write(ref state.Thread, 0);
    }
    private static Vec3d Position(double x, double y, double z)
    {
        if (current is not { } state) return new Vec3d(x, y, z);
        state.Position.Set(x, y, z); state.Hits++;
        return state.Position;
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, Constructor))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(CloudCoordinateReuse), nameof(Position)); }
            yield return instruction;
        }
    }
}
