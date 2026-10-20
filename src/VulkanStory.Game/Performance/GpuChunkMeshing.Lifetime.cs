using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace VulkanStory.Game.Performance;

internal static partial class GpuChunkMeshing
{
    private static long payloadsSuperseded;
    internal static long PayloadsSuperseded => Interlocked.Read(ref payloadsSuperseded);
    private static readonly MethodInfo Merge = AccessTools.Method(typeof(TesselatedChunk), nameof(TesselatedChunk.MergeIfEqual), [typeof(TesselatedChunk)]);
    private static readonly MethodInfo Unload = AccessTools.Method(typeof(WorldChunk), nameof(WorldChunk.Dispose), Type.EmptyTypes);
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PositionX = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionX");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PositionY = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionYAndDimension");
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> PositionZ = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionZ");

    private static void ValidatePayloadLifecycle()
    {
        if (Merge?.ReturnType != typeof(bool) || Unload?.ReturnType != typeof(void))
            throw new InvalidOperationException("Stock chunk payload lifecycle changed.");
    }
    private static void InstallPayloadLifecycle(Harmony harmony)
    {
        harmony.Patch(Merge, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Superseding)));
        harmony.Patch(UnusedDispose, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Discarding)));
        harmony.Patch(Unload, prefix: new HarmonyMethod(typeof(GpuChunkMeshing), nameof(Unloading)));
    }
    // The stock merge replaces center and edge independently, and disposes only
    // the replaced arrays. Preserve that distinction (edge-only never drops center).
    private static void Superseding(TesselatedChunk __instance, TesselatedChunk __0)
    {
        if (PositionX(__instance) != PositionX(__0) || PositionY(__instance) != PositionY(__0) || PositionZ(__instance) != PositionZ(__0)) return;
        lock (Gate)
        {
            if (Centers(__0) != null) RetireParts(Centers(__instance), "superseded");
            if (Edges(__0) != null) RetireParts(Edges(__instance), "superseded");
        }
    }
    private static void Discarding(TesselatedChunk __instance)
    {
        lock (Gate)
        {
            string reason = SourceChunk(__instance)?.Disposed == true ? "chunk-unloaded" : "unused-tessellation";
            RetireParts(Centers(__instance), reason); RetireParts(Edges(__instance), reason);
        }
    }
    private static void Unloading(WorldChunk __instance)
    {
        if (__instance is not ClientChunk chunk) return;
        lock (Gate)
            foreach (var payload in Pending.Where(p => ReferenceEquals(p.Chunk, chunk)).ToArray()) payload.Dispose("chunk-unloaded");
    }
    private static Exception? PublicationEnded(TesselatedChunk tesschunk, Exception? __exception, bool __state)
    {
        try
        {
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
        foreach (var payload in Pending.ToArray()) payload.Dispose(reason);
    }
}
