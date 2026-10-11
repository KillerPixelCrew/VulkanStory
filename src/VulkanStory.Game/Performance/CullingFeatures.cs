using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>C1: far shadow cascade skips the BlendNoCull (foliage) terrain pool; the draw route lives in <see cref="ChunkConsumerPatches" />.</summary>
internal static class ShadowFarFoliage
{
    internal static PerformanceFeature Create() => new("shadow-far-foliage", settings => settings.ShadowFarFoliage, () =>
    {
        MethodInfo method = AccessTools.Method(typeof(ChunkRenderer), "RenderShadow", [typeof(float)]) ??
            throw new MissingMethodException(typeof(ChunkRenderer).FullName, "RenderShadow");
        if (ChunkConsumerPatches.FoliageShadowSite(PatchProcessor.GetOriginalInstructions(method)) < 0)
            throw new InvalidOperationException("RenderShadow foliage pool anchor changed.");
    }, _ => ChunkConsumerPatches.FoliageSkipArmed = true, () => ChunkConsumerPatches.FoliageSkipArmed = false);
}

/// <summary>C2: entities beyond the shadow distance skip the shadow pass, with a 10% margin so the cut-off does not flicker.</summary>
/// <remarks>The skipped branch is the original else-branch, which clears <c>IsShadowRendered</c>; far entities then also skip shadow-only animation.</remarks>
internal static class EntityShadowCull
{
    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> Game = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
    private static readonly FieldInfo RendererEntity = AccessTools.Field(typeof(EntityRenderer), nameof(EntityRenderer.entity))!;
    private static readonly MethodInfo InFrustum = AccessTools.Method(typeof(FrustumCulling), nameof(FrustumCulling.SphereInFrustum),
        [typeof(double), typeof(double), typeof(double), typeof(double)])!;
    private static MethodInfo Target() => AccessTools.Method(typeof(SystemRenderEntities), "OnRenderFrameShadows", [typeof(float)]) ??
        throw new MissingMethodException(typeof(SystemRenderEntities).FullName, "OnRenderFrameShadows");

    /// <summary>Finds the entity local store and the frustum test's false branch in the first shadow loop.</summary>
    private static (int Store, Label Skip) Anchor(IReadOnlyList<CodeInstruction> body)
    {
        int load = -1;
        for (int index = 0; index + 1 < body.Count; index++)
            if (body[index].LoadsField(RendererEntity) && body[index + 1].IsStloc()) { load = index; break; }
        if (load < 0) throw new InvalidOperationException("Shadow entity local anchor changed.");
        int test = -1;
        for (int index = load; index < body.Count; index++) if (body[index].Calls(InFrustum)) { test = index; break; }
        if (test < 0 || test + 1 >= body.Count || (body[test + 1].opcode != OpCodes.Brfalse && body[test + 1].opcode != OpCodes.Brfalse_S) ||
            body[test + 1].operand is not Label skip)
            throw new InvalidOperationException("Shadow frustum branch anchor changed.");
        return (load + 1, skip);
    }

    internal static PerformanceFeature Create() => new("entity-shadow-cull", settings => settings.EntityShadowCull,
        () => Anchor(PatchProcessor.GetOriginalInstructions(Target())),
        harmony => harmony.Patch(Target(), transpiler: new HarmonyMethod(typeof(EntityShadowCull), nameof(Transpiler))));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        var (store, skip) = Anchor(body);
        body.InsertRange(store + 1,
        [
            new CodeInstruction(OpCodes.Ldarg_0),
            IlLocals.LoadOf(body[store]),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EntityShadowCull), nameof(Skip))),
            new CodeInstruction(OpCodes.Brtrue, skip),
        ]);
        return body;
    }

    private static bool Skip(SystemRenderEntities system, Entity entity)
    {
        if (PerformancePatchSet.Current is not { EntityShadowCull: true } settings) return false;
        var player = Game(system).EntityPlayer;
        if (player == null || ReferenceEquals(entity, player)) return false;
        double dx = entity.Pos.X - player.Pos.X, dz = entity.Pos.Z - player.Pos.Z;
        // Entities already outside re-enter 10% closer than the cut-off.
        double limit = entity.IsShadowRendered ? settings.EntityShadowDistance : settings.EntityShadowDistance * .9;
        if (dx * dx + dz * dz <= limit * limit) return false;
        Interlocked.Increment(ref PerformanceCounters.EntityShadowsSkipped);
        return true;
    }
}

/// <summary>Local-variable helpers shared by the performance transpilers.</summary>
internal static class IlLocals
{
    /// <summary>Returns the load matching a local store, preserving its operand.</summary>
    internal static CodeInstruction LoadOf(CodeInstruction store)
    {
        if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
        if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
        if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
        if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
        if (store.opcode == OpCodes.Stloc_S) return new CodeInstruction(OpCodes.Ldloc_S, store.operand);
        if (store.opcode == OpCodes.Stloc) return new CodeInstruction(OpCodes.Ldloc, store.operand);
        throw new InvalidOperationException("Expected a local store, found " + store.opcode + ".");
    }
}

/// <summary>C4a: particle spawns whose emitter lies beyond the view distance (plus its spawn area and a margin) are skipped.</summary>
/// <remarks>Only the two stock property types are gated, using their stored emitter position; reading <c>Pos</c> would advance their random state.</remarks>
internal static class ParticleDistanceGate
{
    private const double Margin = 16;
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, ClientMain> Game = AccessTools.FieldRefAccess<ParticlePoolQuads, ClientMain>("game");
    private static MethodInfo Target() => AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.SpawnParticles),
        [typeof(IParticlePropertiesProvider)]) ?? throw new MissingMethodException(typeof(ParticlePoolQuads).FullName, "SpawnParticles");

    internal static PerformanceFeature Create() => new("particle-distance-gate", settings => settings.ParticleDistanceGate, () =>
    {
        if (Target().ReturnType != typeof(int)) throw new InvalidOperationException("Particle spawn signature changed.");
        _ = AccessTools.Field(typeof(SimpleParticleProperties), nameof(SimpleParticleProperties.MinPos)) ?? throw new MissingFieldException("MinPos");
        _ = AccessTools.Field(typeof(AdvancedParticleProperties), nameof(AdvancedParticleProperties.basePos)) ?? throw new MissingFieldException("basePos");
    }, harmony => harmony.Patch(Target(), prefix: new HarmonyMethod(typeof(ParticleDistanceGate), nameof(Prefix))));

    // Runs on the main and the async particle threads; reads only immutable-enough positions.
    private static bool Prefix(ParticlePoolQuads __instance, IParticlePropertiesProvider particleProperties, ref int __result)
    {
        if (PerformancePatchSet.Current?.ParticleDistanceGate != true || particleProperties == null) return true;
        double x, z, spread;
        Type kind = particleProperties.GetType();
        if (kind == typeof(SimpleParticleProperties))
        {
            var simple = (SimpleParticleProperties)particleProperties;
            if (simple.MinPos == null) return true;
            x = simple.MinPos.X + (simple.AddPos?.X ?? 0) / 2; z = simple.MinPos.Z + (simple.AddPos?.Z ?? 0) / 2;
            spread = simple.AddPos == null ? 0 : Math.Abs(simple.AddPos.X) + Math.Abs(simple.AddPos.Z);
        }
        else if (kind == typeof(AdvancedParticleProperties))
        {
            var advanced = (AdvancedParticleProperties)particleProperties;
            if (advanced.basePos == null) return true;
            x = advanced.basePos.X; z = advanced.basePos.Z; spread = 0;
        }
        else return true;
        var player = Game(__instance)?.EntityPlayer;
        if (player == null) return true;
        double dx = x - player.Pos.X, dz = z - player.Pos.Z, range = ClientSettings.ViewDistance + spread + Margin;
        if (dx * dx + dz * dz <= range * range) return true;
        Interlocked.Increment(ref PerformanceCounters.ParticleSpawnsGated);
        __result = 0;
        return false;
    }
}

/// <summary>C4b: dropped items beyond the configured distance are not drawn (opaque and shadow passes).</summary>
internal static class DroppedItemCull
{
    internal static PerformanceFeature Create(Assembly essentials)
    {
        MethodInfo? target = null;
        return new("dropped-item-cull", settings => settings.DroppedItemCull, () =>
        {
            Type renderer = essentials.GetType("Vintagestory.GameContent.EntityItemRenderer", true)!;
            target = AccessTools.Method(renderer, "DoRender3DOpaque", [typeof(float), typeof(bool)]) ??
                throw new MissingMethodException(renderer.FullName, "DoRender3DOpaque");
            if (!typeof(EntityRenderer).IsAssignableFrom(renderer)) throw new InvalidOperationException("Dropped item renderer base changed.");
        }, harmony => harmony.Patch(target!, prefix: new HarmonyMethod(typeof(DroppedItemCull), nameof(Prefix))));
    }

    private static bool Prefix(EntityRenderer __instance)
    {
        if (PerformancePatchSet.Current is not { DroppedItemCull: true } settings) return true;
        var player = __instance.capi?.World?.Player?.Entity;
        if (player == null || __instance.entity == null) return true;
        double dx = __instance.entity.Pos.X - player.Pos.X, dz = __instance.entity.Pos.Z - player.Pos.Z;
        if (dx * dx + dz * dz <= (double)settings.DroppedItemDistance * settings.DroppedItemDistance) return true;
        Interlocked.Increment(ref PerformanceCounters.DroppedItemsCulled);
        return false;
    }
}
