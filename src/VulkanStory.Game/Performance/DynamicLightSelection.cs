using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>Optimum's dynamic-light scan cache and allocation-free nearest selection, retaining the original search radius.
/// Cached membership expires after 100 ms; position, brightness and loaded-entity identity are checked every frame.</summary>
internal static class DynamicLightSelection
{
    private sealed class State
    {
        internal Entity[]? Candidates;
        internal Entity[] Result = [];
        internal readonly List<Entity> Nearest = new();
        internal readonly List<double> Distances = new();
        internal double X, Y, Z;
        internal float Horizontal, Vertical;
        internal long Stamp;
        internal int LoadedCount;
        internal bool Cached;
    }
    private static ConditionalWeakTable<SystemRenderPlayerEffects, State> states = new();
    private static FieldInfo? limit;
    private static MethodInfo? target, lookup;
    private static Func<ClientMain, Vec3d, float, float, ActionConsumable<Entity>, Entity[]>? original;

    internal static PerformanceFeature Create() => new("dynamic-light-selection", s => s.DynamicLightScanCache || s.NearestLightSelection, () =>
    {
        target = AccessTools.Method(typeof(SystemRenderPlayerEffects), "onBeforeRender", [typeof(float)]);
        limit = AccessTools.Field(typeof(SystemRenderPlayerEffects), "maxDynLights");
        if (target == null || limit?.FieldType != typeof(int)) throw new InvalidOperationException("Dynamic light system members changed.");
        var calls = PatchProcessor.GetOriginalInstructions(target).Where(i => i.operand is MethodInfo { Name: "GetEntitiesAround" }).ToArray();
        if (calls.Length != 1) throw new InvalidOperationException("Dynamic light scan anchor changed.");
        lookup = (MethodInfo)calls[0].operand;
        original = lookup.CreateDelegate<Func<ClientMain, Vec3d, float, float, ActionConsumable<Entity>, Entity[]>>();
    }, harmony => harmony.Patch(target!, transpiler: new HarmonyMethod(typeof(DynamicLightSelection), nameof(Transpiler))),
        WorldLeft: () => states = new());

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (Equals(instruction.operand, lookup))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(DynamicLightSelection), nameof(Select));
            }
            yield return instruction;
        }
    }
    private static Entity[] Select(ClientMain game, Vec3d position, float horizontal, float vertical,
        ActionConsumable<Entity> matches, SystemRenderPlayerEffects owner)
    {
        var settings = PerformancePatchSet.Current;
        if (settings == null || (!settings.DynamicLightScanCache && !settings.NearestLightSelection))
            return original!(game, position, horizontal, vertical, matches);
        int maximum = (int)limit!.GetValue(owner)!;
        if (maximum <= 0) return [];
        State state = states.GetOrCreateValue(owner);
        long now = Stopwatch.GetTimestamp();
        double dx = position.X - state.X, dy = position.Y - state.Y, dz = position.Z - state.Z;
        if (!settings.DynamicLightScanCache || !state.Cached || state.Candidates == null || now - state.Stamp >= Stopwatch.Frequency / 10 ||
            dx * dx + dy * dy + dz * dz >= 4 || horizontal != state.Horizontal || vertical != state.Vertical || game.LoadedEntities.Count != state.LoadedCount)
        {
            state.Candidates = original!(game, position, horizontal, vertical, matches);
            state.X = position.X; state.Y = position.Y; state.Z = position.Z;
            state.Horizontal = horizontal; state.Vertical = vertical;
            state.Stamp = now; state.LoadedCount = game.LoadedEntities.Count;
        }
        state.Cached = settings.DynamicLightScanCache;
        state.Nearest.Clear(); state.Distances.Clear();
        foreach (Entity entity in state.Candidates)
        {
            if (!game.LoadedEntities.TryGetValue(entity.EntityId, out var live) || !ReferenceEquals(live, entity) || !matches(entity)) continue;
            double ex = entity.Pos.X - position.X, ey = entity.Pos.InternalY - position.Y, ez = entity.Pos.Z - position.Z;
            if (Math.Abs(ey) > vertical || ex * ex + ez * ez > horizontal * horizontal) continue;
            double squared = ex * ex + ey * ey + ez * ez;
            if (!settings.NearestLightSelection) { state.Nearest.Add(entity); continue; }
            int index = state.Distances.BinarySearch(squared);
            if (index < 0) index = ~index;
            if (index >= maximum) continue;
            state.Nearest.Insert(index, entity); state.Distances.Insert(index, squared);
            if (state.Nearest.Count > maximum) { state.Nearest.RemoveAt(maximum); state.Distances.RemoveAt(maximum); }
        }
        // Exact length prevents stale tail entries and preserves the original foreach without null sentinels.
        if (state.Result.Length != state.Nearest.Count) state.Result = new Entity[state.Nearest.Count];
        state.Nearest.CopyTo(state.Result);
        return state.Result;
    }
}
