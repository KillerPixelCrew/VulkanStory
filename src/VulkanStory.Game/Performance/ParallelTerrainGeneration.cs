using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VulkanStory.Game.Performance;

/// <summary>Gives each worldgen worker its own initialized GenTerra workspace. Noise/configuration remain shared read-only;
/// the landform map cache retains its original lifetime and is protected by one lock across original and cloned generators.</summary>
internal static class ParallelTerrainGeneration
{
    private sealed class Workers { internal readonly Dictionary<int, ChunkColumnGenerationDelegate> Generators = new(); }
    private static ConditionalWeakTable<GenTerra, Workers> workers = new();
    private static readonly MethodInfo Clone = AccessTools.Method(typeof(object), "MemberwiseClone")!;
    private static readonly FieldInfo[] Scratch = new[] { "taperMap", "columnResults", "layerFullySolid", "layerFullyEmpty", "borderIndicesByCardinal" }
        .Select(name => AccessTools.Field(typeof(GenTerra), name)!).ToArray();
    private static readonly FieldInfo Cache = AccessTools.Field(typeof(GenTerra), "LandformMapByRegion")!;
    private static readonly FieldInfo MaxThreads = AccessTools.Field(typeof(GenTerra), "maxThreads")!;

    internal static PerformanceFeature Create() => new("parallel-terrain-generation", s => s.ParallelTerrainGeneration, () =>
    {
        if (Scratch.Any(f => f == null || !f.FieldType.IsArray) || Cache == null || MaxThreads?.FieldType != typeof(int))
            throw new InvalidOperationException("Terrain generator workspace fields changed.");
        if (AccessTools.Field(Scratch[1].FieldType.GetElementType(), "ColumnBlockSolidities")?.FieldType != typeof(BitArray))
            throw new InvalidOperationException("Terrain column solidity workspace changed.");
        _ = ServerTypes.Method(ServerTypes.Supply, "GenerateChunkColumns_OnSeparateThread", typeof(int), typeof(int));
    }, harmony =>
    {
        harmony.Patch(AccessTools.Method(typeof(GenTerra), "GetOrLoadLerpedLandformMap"),
            prefix: new HarmonyMethod(typeof(ParallelTerrainGeneration), nameof(LockCache)),
            finalizer: new HarmonyMethod(typeof(ParallelTerrainGeneration), nameof(UnlockCache)));
        harmony.Patch(AccessTools.Method(typeof(GenTerra), "initWorldGen"),
            postfix: new HarmonyMethod(typeof(ParallelTerrainGeneration), nameof(Reset)));
        harmony.Patch(ServerTypes.Method(ServerTypes.Supply, "GenerateChunkColumns_OnSeparateThread", typeof(int), typeof(int)),
            postfix: new HarmonyMethod(typeof(ParallelTerrainGeneration), nameof(UseIdleWorker)));
    }, WorldLeft: () => workers = new());

    internal static bool TryInvoke(ChunkColumnGenerationDelegate original, IChunkColumnGenerateRequest request, object gate)
    {
        if (PerformancePatchSet.Current?.ParallelTerrainGeneration != true || !WorldgenPipeline.ParallelActive || original.Target is not GenTerra generator ||
            original.Method.DeclaringType != typeof(GenTerra)) return false;
        Workers state = workers.GetOrCreateValue(generator);
        ChunkColumnGenerationDelegate invoke;
        lock (gate)
        {
            int thread = Environment.CurrentManagedThreadId;
            if (!state.Generators.TryGetValue(thread, out invoke!))
            {
                var clone = (GenTerra)Clone.Invoke(generator, null)!;
                foreach (FieldInfo field in Scratch)
                {
                    Array source = (Array)field.GetValue(generator)!;
                    Array copy = (Array)source.Clone();
                    if (field.Name == "columnResults")
                    {
                        FieldInfo bits = AccessTools.Field(copy.GetType().GetElementType(), "ColumnBlockSolidities")!;
                        for (int index = 0; index < copy.Length; index++)
                        {
                            object column = copy.GetValue(index)!;
                            bits.SetValue(column, ((BitArray)bits.GetValue(column)!).Clone());
                            copy.SetValue(column, index);
                        }
                    }
                    field.SetValue(clone, copy);
                }
                // Avoid multiplying the existing inner noise loop's task count by the column worker count.
                int inner = (int)MaxThreads.GetValue(generator)!;
                MaxThreads.SetValue(clone, Math.Max(1, inner / Math.Max(1, WorldgenPipeline.WorkerCount)));
                state.Generators.Add(thread, invoke = original.Method.CreateDelegate<ChunkColumnGenerationDelegate>(clone));
            }
        }
        invoke(request);
        return true;
    }
    private static void LockCache(GenTerra __instance, out object __state)
    {
        __state = Cache.GetValue(__instance)!;
        Monitor.Enter(__state);
    }
    private static void UnlockCache(object __state) => Monitor.Exit(__state);
    private static void Reset(GenTerra __instance) => workers.Remove(__instance);
    private static void UseIdleWorker(object __instance, int stageStart, int stageEnd, ref int __result)
    {
        if (__result != 0 || stageStart <= 1 || PerformancePatchSet.Current?.ParallelTerrainGeneration != true || !WorldgenPipeline.ParallelActive) return;
        // The existing worker pool normally assigns one pass to each worker. Idle later-pass workers may also
        // process Terrain. The pipeline's footprint lease and expected-pass check prevent duplicate population.
        __result = (int)ServerTypes.Method(ServerTypes.Supply, "GenerateChunkColumns_OnSeparateThread", typeof(int), typeof(int))
            .Invoke(__instance, [1, 2])!;
    }
}
