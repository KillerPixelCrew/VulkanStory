using System.Diagnostics;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

// Stock TesselateChunk increments ClientChunk.quantityDrawn when it first
// reaches a chunk, and ClientWorldMap.IsChunkRendered (quantityDrawn > 0)
// admits that chunk's entities in SystemRenderEntities.OnBeforeRender, for both
// the main and shadow passes. A GPU job blocks this single tessellation thread
// on two fence waits behind the frame's graphics work, so every chunk queued
// after it reaches its first tessellation later than with stock producers.
// Chunks the tessellator has not reached yet therefore keep stock producers.
internal static partial class GpuChunkMeshing
{
    private static readonly AccessTools.FieldRef<ClientMain, UniqueQueue<long>> DirtyNormal =
        AccessTools.FieldRefAccess<ClientMain, UniqueQueue<long>>("dirtyChunks");
    private static readonly AccessTools.FieldRef<ClientMain, UniqueQueue<long>> DirtyPriority =
        AccessTools.FieldRefAccess<ClientMain, UniqueQueue<long>>("dirtyChunksPriority");
    private static readonly AccessTools.FieldRef<ClientMain, UniqueQueue<long>> DirtyLast =
        AccessTools.FieldRefAccess<ClientMain, UniqueQueue<long>>("dirtyChunksLast");
    private static readonly AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> LoadedChunks =
        AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>("chunks");
    private static readonly AccessTools.FieldRef<ClientWorldMap, object> LoadedChunksLock =
        AccessTools.FieldRefAccess<ClientWorldMap, object>("chunksLock");
    private static readonly AccessTools.FieldRef<ClientChunk, int> QuantityDrawn =
        AccessTools.FieldRefAccess<ClientChunk, int>("quantityDrawn");
    [ThreadStatic] private static long backlogCheckedAt;
    [ThreadStatic] private static bool backlogFound;
    private static long generationWaitTicks;

    /// <summary>Producer-thread time spent inside synchronous GPU generation, including both fence waits.</summary>
    internal static double GenerationWaitMilliseconds => Interlocked.Read(ref generationWaitTicks) * 1000.0 / Stopwatch.Frequency;

    // Called before Gate: stock unload disposes chunks under chunksLock, and
    // that disposal takes Gate (Unloading).
    private static bool FirstTessellationsQueued(ClientMain game)
    {
        // The caller's chunk has already left its queue. Without other queued
        // work no chunk can wait behind this job. Counts are read without the
        // queue locks; a racing enqueue is equivalent to arriving after the check.
        if (DirtyPriority(game).Count + DirtyNormal(game).Count + DirtyLast(game).Count == 0) return false;
        long now = Stopwatch.GetTimestamp();
        // A found backlog keeps stock producers briefly instead of rescanning
        // the loaded-chunk map for every queued chunk.
        if (backlogFound && now - backlogCheckedAt < Stopwatch.Frequency / 10) return true;
        backlogCheckedAt = now;
        return backlogFound = UndrawnChunks(game, stopAtFirst: true) != 0;
    }

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
