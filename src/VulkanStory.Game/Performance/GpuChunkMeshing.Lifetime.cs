using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace VulkanStory.Game.Performance;

internal static partial class GpuChunkMeshing
{
    private static long payloadsSuperseded;
    internal static long PayloadsSuperseded => Interlocked.Read(ref payloadsSuperseded);
    private static long stalePublicationsDropped, requeuedDuringFlight;
    internal static long StalePublicationsDropped => Interlocked.Read(ref stalePublicationsDropped);
    internal static long RequeuedDuringFlight => Interlocked.Read(ref requeuedDuringFlight);
    // Normal and priority upload queues can publish in the opposite order to
    // production. Track CPU results too: a new CPU fallback must beat an older
    // detached GPU result, and an old CPU result must not replace a newer GPU one.
    private sealed class ChunkOrder
    {
        internal long Next, PublishedCenter, PublishedEdge;
    }
    private sealed class PublicationOrder(ChunkOrder owner, long sequence)
    {
        internal readonly ChunkOrder Owner = owner;
        internal long Center = sequence, Edge = sequence;
        internal bool PublishingCenter, PublishingEdge;
    }
    private static readonly ConditionalWeakTable<ClientChunk, ChunkOrder> ChunkOrders = new();
    private static readonly ConditionalWeakTable<TesselatedChunk, PublicationOrder> PublicationOrders = new();
    private static readonly MethodInfo Merge = AccessTools.Method(typeof(TesselatedChunk), nameof(TesselatedChunk.MergeIfEqual), [typeof(TesselatedChunk)]);
    private static readonly MethodInfo Unload = AccessTools.Method(typeof(WorldChunk), nameof(WorldChunk.Dispose), Type.EmptyTypes);
    private static readonly MethodInfo DisposePart = AccessTools.Method(typeof(TesselatedChunkPart), "Dispose", Type.EmptyTypes);
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PositionX = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionX");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PositionY = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionYAndDimension");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PositionZ = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionZ");

    private static void ValidatePayloadLifecycle()
    {
        if (Merge?.ReturnType != typeof(bool) || Unload?.ReturnType != typeof(void) || DisposePart?.ReturnType != typeof(void))
            throw new InvalidOperationException("Stock chunk payload lifecycle changed.");
    }
    private static void InstallPayloadLifecycle(Harmony harmony)
    {
        harmony.Patch(Merge, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Superseding)),
            finalizer: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(MergeEnded)));
        harmony.Patch(UnusedDispose, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Discarding)));
        harmony.Patch(Unload, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Unloading)));
    }
    // The stock merge replaces center and edge independently, and disposes only
    // the replaced arrays. Preserve that distinction (edge-only never drops center).
    private static bool Superseding(TesselatedChunk __instance, TesselatedChunk __0, ref bool __result, out bool __state)
    {
        __state = false;
        lock (Gate)
            if (Generations.ContainsKey(__instance) || Generations.ContainsKey(__0))
            { __result = false; return false; }
        if (PositionX(__instance) != PositionX(__0) || PositionY(__instance) != PositionY(__0) || PositionZ(__instance) != PositionZ(__0)) return true;
        // Stock compares coordinates alone. Do not merge a reloaded chunk's
        // arrays into a queue entry still owned by the unloaded chunk.
        if (!ReferenceEquals(SourceChunk(__instance), SourceChunk(__0))) { __result = false; return false; }
        Monitor.Enter(Gate); __state = true;
        if (PublicationOrders.TryGetValue(__instance, out var previous) && PublicationOrders.TryGetValue(__0, out var next))
        {
            if (Centers(__0) != null && Centers(__instance) != null && next.Center < previous.Center)
                DropStaleParts(ref Centers(__0));
            else if (Centers(__0) != null) previous.Center = next.Center;
            if (Edges(__0) != null && Edges(__instance) != null && next.Edge < previous.Edge)
                DropStaleParts(ref Edges(__0));
            else if (Edges(__0) != null) previous.Edge = next.Edge;
        }
        if (Centers(__0) != null) RetireParts(Centers(__instance), "superseded");
        if (Edges(__0) != null) RetireParts(Edges(__instance), "superseded");
        return true;
    }
    private static Exception? MergeEnded(Exception? __exception, bool __state)
    {
        if (__state) Monitor.Exit(Gate);
        return __exception;
    }
    private static void DropStaleParts(ref TesselatedChunkPart[] parts)
    {
        RetireParts(parts, "stale-publication");
        foreach (var part in parts) DisposePart.Invoke(part, null);
        parts = null!;
        Interlocked.Increment(ref stalePublicationsDropped);
    }
    private static void Discarding(TesselatedChunk __instance)
    {
        lock (Gate)
        {
            CancelGeneration(__instance, "unused-tessellation");
            string reason = SourceChunk(__instance)?.Disposed == true ? "chunk-unloaded" : "unused-tessellation";
            RetireParts(Centers(__instance), reason); RetireParts(Edges(__instance), reason);
        }
    }
    private static void Unloading(WorldChunk __instance)
    {
        if (__instance is not ClientChunk chunk) return;
        lock (Gate)
        {
            foreach (var pending in Generations.Keys.Where(c => ReferenceEquals(SourceChunk(c), chunk)).ToArray())
                CancelGeneration(pending, "chunk-unloaded");
            foreach (var payload in Pending.Where(p => ReferenceEquals(p.Chunk, chunk)).ToArray()) payload.Dispose("chunk-unloaded");
        }
    }
    private static Exception? PublicationEnded(TesselatedChunk tesschunk, Exception? __exception, bool __state)
    {
        try
        {
            if (__exception == null && __state && PublicationOrders.TryGetValue(tesschunk, out var order))
            {
                // Stock AddTesselatedChunk clears both arrays before returning.
                if (order.PublishingCenter) order.Owner.PublishedCenter = Math.Max(order.Owner.PublishedCenter, order.Center);
                if (order.PublishingEdge) order.Owner.PublishedEdge = Math.Max(order.Owner.PublishedEdge, order.Edge);
            }
            if (__exception != null)
                lock (Gate)
                {
                    RetireParts(Centers(tesschunk), "publication-failed");
                    RetireParts(Edges(tesschunk), "publication-failed");
                }
        }
        finally { if (__state) Monitor.Exit(Gate); }
        return __exception;
    }
    private static void RetireParts(TesselatedChunkPart[]? parts, string reason)
    {
        if (parts == null) return;
        foreach (var part in parts)
            if (PartLod.GetValue(part) is MeshData data && Payloads.TryGetValue(data, out var payload)) payload.Dispose(reason);
    }
    // Gate is held by every caller. Keep the CWT marker until stock disposal,
    // so invalidated count-only meshes cannot enter the CPU array recycler.
    private static void RetirePending(string reason)
    {
        foreach (var chunk in Generations.Keys.ToArray()) CancelGeneration(chunk, reason);
        foreach (var payload in Pending.ToArray()) payload.Dispose(reason);
    }
}
