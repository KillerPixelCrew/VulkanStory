using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace VulkanStory.Game.Performance;

/// <summary>Ports Optimum's page cache and array renderer through the real ChunkMapLayer pixel producer and MapDB.
/// Palette/world identity, dirty neighbours, redraw/purge, live switches and map/world teardown retain their original behavior.</summary>
internal static class WorldMapPageCache
{
    private sealed class State(ChunkMapLayer layer)
    {
        internal readonly object Gate = new();
        internal readonly ChunkMapLayer Layer = layer;
        internal readonly HashSet<(int X, int Z)> Pending = new();
        internal readonly HashSet<(int X, int Z)> VisiblePages = new();
        internal MapPageStore? Store;
        internal MapPageRenderer? Renderer;
        internal volatile bool ArrayActive, Disposed;
        internal bool Requested, TransitionToOriginal;
        internal bool? Palette;
        internal string? Refusal;
        internal bool Warm { get { lock (Gate) return Pending.Count == 0; } }
        internal void Done(FastVec2i coord) { lock (Gate) Pending.Remove((coord.X, coord.Y)); }
        internal void QueueVisible()
        {
            lock (Gate)
            {
                Pending.Clear();
                foreach (FastVec2i chunk in Visible(Layer)) Pending.Add((chunk.X, chunk.Y));
            }
            lock (QueueGate(Layer))
                foreach (FastVec2i chunk in Visible(Layer)) Queue(Layer).Enqueue(chunk.Copy());
        }
        internal void EnsurePalette()
        {
            bool accurate = Accurate(Layer);
            if (Palette == accurate && Store != null) return;
            Store?.Dispose(); Palette = accurate;
            var api = Api(Layer);
            Store = new MapPageStore(api.World.SavegameIdentifier, Layer.getMapDbFilePath(), accurate);
            Renderer?.Dispose(); Renderer = null; ArrayActive = false;
        }
        internal void CloseRenderer() { ArrayActive = false; Renderer?.Dispose(); Renderer = null; }
        internal void Dispose()
        {
            if (Disposed) return;
            Disposed = true; CloseRenderer(); Store?.Dispose();
        }
        ~State() { Store?.Dispose(); } // Only cancels background I/O; GPU cleanup remains on the owner thread.
    }
    private static readonly object CreationGate = new();
    private static readonly ConditionalWeakTable<ChunkMapLayer, State> states = new();
    private static readonly ConditionalWeakTable<MapDB, State> databases = new();
    private static readonly System.Func<object, ICoreClientAPI> Api = ServerTypes.Getter<ICoreClientAPI>(typeof(ChunkMapLayer), "capi");
    private static readonly System.Func<object, bool> Accurate = ServerTypes.Getter<bool>(typeof(ChunkMapLayer), "colorAccurate");
    private static readonly System.Func<object, MapDB> Database = ServerTypes.Getter<MapDB>(typeof(ChunkMapLayer), "mapdb");
    private static readonly System.Func<object, HashSet<FastVec2i>> Visible = ServerTypes.Getter<HashSet<FastVec2i>>(typeof(ChunkMapLayer), "curVisibleChunks");
    private static readonly System.Func<object, object> QueueGate = ServerTypes.Getter<object>(typeof(ChunkMapLayer), "chunksToGenLock");
    private static readonly System.Func<object, UniqueQueue<FastVec2i>> Queue = ServerTypes.Getter<UniqueQueue<FastVec2i>>(typeof(ChunkMapLayer), "chunksToGen");
    private static readonly System.Func<object, ConcurrentDictionary<FastVec2i, MultiChunkMapComponent>> Legacy =
        ServerTypes.Getter<ConcurrentDictionary<FastVec2i, MultiChunkMapComponent>>(typeof(ChunkMapLayer), "loadedMapData");
    private static readonly System.Func<object, object> Ready = ServerTypes.Getter<object>(typeof(ChunkMapLayer), "readyMapPieces");
    private static readonly MethodInfo ClearReady = AccessTools.Field(typeof(ChunkMapLayer), "readyMapPieces")!.FieldType.GetMethod("Clear", Type.EmptyTypes)!;
    private static readonly MethodInfo CountReady = AccessTools.Field(typeof(ChunkMapLayer), "readyMapPieces")!.FieldType.GetProperty("Count")!.GetMethod!;

    internal static PerformanceFeature Create() => new("world-map-page-cache", s => s.WorldMapPageCache, () =>
    {
        foreach (string name in new[] { "Render", "OnMapOpenedClient", "OnMapClosedClient", "OnViewChangedClient", "loadFromChunkPixels", "Event_OnChunkDirty", "OnTick", "Dispose" })
            if (AccessTools.Method(typeof(ChunkMapLayer), name) == null) throw new MissingMethodException(typeof(ChunkMapLayer).FullName, name);
        if (ClearReady == null || CountReady == null) throw new InvalidOperationException("Map pixel queue members changed.");
    }, harmony =>
    {
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "Render"), prefix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Render)),
            postfix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(RenderTransition)));
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "OnMapOpenedClient"), postfix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Opened)));
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "OnMapClosedClient"), postfix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Closed)));
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "OnViewChangedClient"), postfix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(ViewChanged)));
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "loadFromChunkPixels"), prefix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Pixels)),
            postfix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(PixelsReady)));
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "Event_OnChunkDirty"), prefix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Dirty)));
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "OnTick"), prefix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Tick)));
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "OnMapCmdRedraw"), prefix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Redraw)));
        harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), "Dispose"), prefix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(DisposeLayer)));
        harmony.Patch(AccessTools.Method(typeof(MapDB), "GetMapPiece"), prefix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Read)),
            postfix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(ReadFinished)));
        harmony.Patch(AccessTools.Method(typeof(MapDB), "Purge"), postfix: new HarmonyMethod(typeof(WorldMapPageCache), nameof(Purge)));
    }, WorldLeft: () => { foreach (var entry in states) entry.Value.Dispose(); });

    private static State Get(ChunkMapLayer layer)
    {
        lock (CreationGate)
        {
            State state = states.GetValue(layer, _ => new State(layer));
            MapDB? database = Database(layer);
            if (database != null) databases.GetValue(database, _ => state);
            return state;
        }
    }
    private static void Opened(ChunkMapLayer __instance)
    {
        if (PerformancePatchSet.Current?.WorldMapPageCache != true) return;
        State state = Get(__instance); state.EnsurePalette();
    }
    private static void Closed(ChunkMapLayer __instance)
    { if (states.TryGetValue(__instance, out var state)) state.CloseRenderer(); }
    private static void ViewChanged(ChunkMapLayer __instance)
    { if (states.TryGetValue(__instance, out var state) && state.Requested && !state.Disposed) state.QueueVisible(); }
    private static bool Pixels(ChunkMapLayer __instance, FastVec2i cord, int[] pixels)
    {
        if (!states.TryGetValue(__instance, out var state) || state.Disposed) return true;
        if (PerformancePatchSet.Current?.WorldMapPageCache == true || state.TransitionToOriginal)
            state.Store?.Put(cord.X, cord.Y, pixels);
        return !state.ArrayActive || PerformancePatchSet.Current?.WorldMapPageCache != true;
    }
    private static void PixelsReady(ChunkMapLayer __instance, FastVec2i cord)
    { if (states.TryGetValue(__instance, out var state)) state.Done(cord); }
    private static bool Read(MapDB __instance, FastVec2i __0, ref MapPieceDB? __result)
    {
        if (PerformancePatchSet.Current?.WorldMapPageCache != true || !databases.TryGetValue(__instance, out var state) || state.Disposed || state.Store == null)
            return true;
        if (!state.Store.TryChunk(__0.X, __0.Y, out var pixels)) return true;
        __result = new MapPieceDB { Pixels = pixels };
        return false;
    }
    private static void ReadFinished(MapDB __instance, FastVec2i __0, MapPieceDB? __result)
    { if (__result?.Pixels == null && databases.TryGetValue(__instance, out var state)) state.Done(__0); }
    private static void Dirty(ChunkMapLayer __instance, Vec3i chunkCoord)
    {
        if (!states.TryGetValue(__instance, out var state) || state.Disposed) return;
        foreach (var (x, z) in new[] { (0, 0), (0, -1), (-1, 0), (0, 1), (1, 1) })
            state.Store?.Invalidate(chunkCoord.X + x, chunkCoord.Z + z);
    }
    private static bool Tick(ChunkMapLayer __instance)
    {
        if (states.TryGetValue(__instance, out var state) && state.ArrayActive)
        { ClearReady.Invoke(Ready(__instance), null); return false; }
        return true;
    }
    private static bool Render(ChunkMapLayer __instance, GuiElementMap mapElem, float dt)
    {
        State state = Get(__instance);
        if (state.Disposed || !__instance.Active) return true;
        bool requested = PerformancePatchSet.Current?.WorldMapPageCache == true;
        if (requested != state.Requested)
        {
            state.Requested = requested; state.Refusal = null;
            if (requested) state.EnsurePalette();
            else { state.TransitionToOriginal = state.Renderer != null; state.ArrayActive = false; }
            state.QueueVisible();
        }
        state.VisiblePages.Clear();
        foreach (var chunk in Visible(__instance)) state.VisiblePages.Add(MapPageStore.Key(chunk.X, chunk.Y));
        // A zoomed-out view can exceed the resident-page budget. Retain the complete original map path rather than evict
        // textures still referenced by this frame's draw. Changes of backend requeue the visible source pixels.
        bool canUseArray = requested && state.Refusal == null && state.VisiblePages.Count <= MapPageStore.Capacity;
        if (!canUseArray)
        {
            if (state.ArrayActive) { state.ArrayActive = false; state.TransitionToOriginal = true; state.QueueVisible(); }
            return true;
        }
        if (!state.Warm) return true;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !GameGraphicsAdapter.TryGet(platform, out var graphics)) return true;
        try
        {
            state.EnsurePalette();
            state.Renderer ??= new MapPageRenderer(Api(__instance), graphics!);
            if (!state.ArrayActive)
            {
                foreach (var entry in Legacy(__instance)) if (entry.Value.Texture != null) entry.Value.ActuallyDispose();
                Legacy(__instance).Clear(); ClearReady.Invoke(Ready(__instance), null);
                state.ArrayActive = true; state.TransitionToOriginal = false;
            }
            state.Renderer.Draw(state.Store!, state.VisiblePages, mapElem);
            return false;
        }
        catch (Exception error)
        {
            state.Refusal = error.Message; state.ArrayActive = false; state.TransitionToOriginal = false;
            state.CloseRenderer(); state.QueueVisible();
            Api(__instance).Logger.Warning("VulkanStory: map page rendering unavailable: {0}", error.Message);
            return true;
        }
    }
    private static void RenderTransition(ChunkMapLayer __instance, GuiElementMap mapElem)
    {
        if (!states.TryGetValue(__instance, out var state) || !state.TransitionToOriginal || state.Renderer == null) return;
        if (state.Warm && (int)CountReady.Invoke(Ready(__instance), null)! == 0)
        { state.TransitionToOriginal = false; state.CloseRenderer(); }
        else if (state.VisiblePages.Count <= MapPageStore.Capacity && state.Store != null)
            state.Renderer.Draw(state.Store, state.VisiblePages, mapElem);
    }
    private static void Redraw(ChunkMapLayer __instance)
    {
        if (!states.TryGetValue(__instance, out var state)) return;
        state.Store?.Purge(); state.CloseRenderer(); state.QueueVisible();
    }
    private static void Purge(MapDB __instance)
    { if (databases.TryGetValue(__instance, out var state)) Redraw(state.Layer); }
    private static void DisposeLayer(ChunkMapLayer __instance)
    { if (states.TryGetValue(__instance, out var state)) state.Dispose(); }
}
