using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

// Stock TesselateChunk increments ClientChunk.quantityDrawn when it first
// reaches a chunk, and ClientWorldMap.IsChunkRendered (quantityDrawn > 0)
// admits that chunk's entities in SystemRenderEntities.OnBeforeRender, for both
// the main and shadow passes. First attempts keep stock CPU geometry, while
// later attempts can submit GPU work without blocking other queued chunks.
internal static partial class GpuChunkMeshing
{
    private static readonly AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> LoadedChunks =
        AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>("chunks");
    private static readonly AccessTools.FieldRef<ClientWorldMap, object> LoadedChunksLock =
        AccessTools.FieldRefAccess<ClientWorldMap, object>("chunksLock");
    private static readonly AccessTools.FieldRef<ClientChunk, int> QuantityDrawn =
        AccessTools.FieldRefAccess<ClientChunk, int>("quantityDrawn");
    /// <summary>Producer-thread GPU fence wait time; submit/poll generation never waits.</summary>
    internal static double GenerationWaitMilliseconds => 0;

    private static int UndrawnChunks(ClientMain game, bool stopAtFirst)
    {
        int count = 0;
        lock (LoadedChunksLock(game.WorldMap))
            foreach (ClientChunk chunk in LoadedChunks(game.WorldMap).Values)
            {
                if (chunk.Disposed || Volatile.Read(ref QuantityDrawn(chunk)) != 0) continue;
                count++;
                if (stopAtFirst) break;
            }
        return count;
    }

    /// <summary>
    /// Loaded chunks the stock tessellator has not reached yet, and loaded entities that the stock
    /// IsChunkRendered test consequently leaves unrendered. Main thread, at scenario samples only.
    /// </summary>
    internal static (int Chunks, int Entities)? UndrawnState(ClientMain? game)
    {
        if (game?.WorldMap is not { } map) return null;
        int entities = 0;
        foreach (Entity entity in game.LoadedEntities.Values)
            if (!ReferenceEquals(entity, game.EntityPlayer) && !entity.AllowOutsideLoadedRange && !map.IsChunkRendered(entity.Pos))
                entities++;
        return (UndrawnChunks(game, stopAtFirst: false), entities);
    }
}
