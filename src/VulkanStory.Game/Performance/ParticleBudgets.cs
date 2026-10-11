using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>Budgets the quantity consumed by the stock spawn loop without mutating shared particle providers.</summary>
/// <remarks>Inspired by telleznilsson/RAM-Garbage-Collector. The inherited loop covers quads, cubes, synchronous,
/// asynchronous and secondary particles; the original fractional-count sampling remains intact.</remarks>
internal static class ParticleBudgets
{
    private static readonly MethodInfo Quantity = AccessTools.PropertyGetter(typeof(IParticlePropertiesProvider), nameof(IParticlePropertiesProvider.Quantity));
    private static MethodInfo Target() => AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.SpawnParticles),
        [typeof(IParticlePropertiesProvider)]) ?? throw new MissingMethodException(nameof(ParticlePoolQuads), "SpawnParticles");

    private static int Anchor(IReadOnlyList<CodeInstruction> body)
    {
        int site = -1;
        for (int i = 0; i + 4 < body.Count; i++)
            if (body[i].Calls(Quantity) && body[i + 1].opcode == OpCodes.Ldarg_0 &&
                body[i + 2].operand is FieldInfo { Name: "currentGamespeed", FieldType: var type } && type == typeof(float) &&
                body[i + 2].opcode == OpCodes.Ldfld && body[i + 3].opcode == OpCodes.Mul && body[i + 4].IsStloc())
            {
                if (site >= 0) throw new InvalidOperationException("Particle quantity anchor is ambiguous.");
                site = i + 4;
            }
        return site >= 0 ? site : throw new InvalidOperationException("Particle quantity/speed anchor changed.");
    }

    internal static PerformanceFeature Create() => new("particle-budgets", s => s.ParticleBudgets,
        () => { _ = Anchor(PatchProcessor.GetOriginalInstructions(Target())); },
        harmony => harmony.Patch(Target(), transpiler: new HarmonyMethod(typeof(ParticleBudgets), nameof(Transpiler))));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        int site = Anchor(body);
        body.InsertRange(site,
        [
            new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(body[site]),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ParticleBudgets), nameof(Limit))),
        ]);
        return body;
    }

    private static float Limit(float quantity, ParticlePoolQuads pool, IParticlePropertiesProvider provider)
    {
        if (PerformancePatchSet.Current is not { ParticleBudgets: true } settings || provider.IgnoreUserConfig) return quantity;
        if (!float.IsFinite(quantity) || quantity <= 0) return 0;
        float result = quantity * (settings.ParticleQuantityPercent / 100f);
        if (settings.ParticleSpawnLimit > 0) result = Math.Min(result, settings.ParticleSpawnLimit);
        if (settings.ParticlePoolLimit > 0)
            result = Math.Min(result, Math.Max(0, settings.ParticlePoolLimit - pool.ParticlesPool.AliveCount));
        if (result < quantity) Interlocked.Increment(ref PerformanceCounters.ParticleSpawnsBudgeted);
        return result;
    }
}
