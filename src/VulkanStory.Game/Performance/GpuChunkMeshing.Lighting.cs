using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

internal static partial class GpuChunkMeshing
{
    private static readonly ConditionalWeakTable<ChunkTesselator, Lighting> LightingOwners = new();
    private static readonly AccessTools.FieldRef<ChunkTesselator, bool> Started = AccessTools.FieldRefAccess<ChunkTesselator, bool>("started");
    private static readonly AccessTools.FieldRef<ChunkTesselator, bool> LightsGo = AccessTools.FieldRefAccess<ChunkTesselator, bool>("lightsGo");
    private static readonly AccessTools.FieldRef<ChunkTesselator, bool> TexturesGo = AccessTools.FieldRefAccess<ChunkTesselator, bool>("blockTexturesGo");
    private sealed record Lighting(ClientMain World, object Map, float[] Block, float[] Sun, byte[] Hue, byte[] Saturation);

    private static bool ValidLevels(float[]? levels) => levels is { Length: >= 32 } &&
        levels.All(v => float.IsFinite(v) && v >= 0 && v <= 1) && levels[^1] > 0;

    // Stock Start constructs the light converter only after BOTH notifications.
    // Certify its inputs after that construction, never just on world attachment.
    private static void LightingStarted(ChunkTesselator __instance)
    {
        lock (Gate)
        {
            var game = Game(__instance); var map = game.WorldMap;
            LightingOwners.Remove(__instance);
            if (!ReferenceEquals(world, game) || !Started(__instance) || !LightsGo(__instance) || !TexturesGo(__instance) ||
                !ValidLevels(map.BlockLightLevels) || !ValidLevels(map.SunLightLevels) ||
                map.hueLevels is not { Length: > 0 } || map.satLevels is not { Length: > 0 }) return;
            LightingOwners.Add(__instance, new(game, map, map.BlockLightLevels, map.SunLightLevels, map.hueLevels, map.satLevels));
        }
    }

    private static bool LightingReady(ChunkTesselator producer)
    {
        if (!LightingOwners.TryGetValue(producer, out var state) || !ReferenceEquals(state.World, world)) return false;
        var map = state.World.WorldMap;
        return ReferenceEquals(state.Map, map) && ReferenceEquals(state.Block, map.BlockLightLevels) &&
            ReferenceEquals(state.Sun, map.SunLightLevels) && ReferenceEquals(state.Hue, map.hueLevels) &&
            ReferenceEquals(state.Saturation, map.satLevels) && Started(producer) && LightsGo(producer) && TexturesGo(producer);
    }

    private static void LightingChanging(ChunkTesselator __instance)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(world, Game(__instance))) return;
            LightingOwners.Remove(__instance);
            generation++; RetirePending("lighting-changed"); OrderedCpu.Clear();
        }
    }
}
