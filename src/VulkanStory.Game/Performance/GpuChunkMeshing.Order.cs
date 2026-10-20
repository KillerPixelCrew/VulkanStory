using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

internal static partial class GpuChunkMeshing
{
    private sealed class OrderedFallback
    {
        internal long Revision;
        internal long Full = -1, Edge = -1;
    }

    private static void MarkedDirty(ClientWorldMap __instance, int __0, int __1, int __2) =>
        Dirtied(__instance.GetChunk(__0, __1, __2) as ClientChunk);

    private static void IndexedDirty(ClientWorldMap __instance, long __0) =>
        Dirtied(__instance.GetChunk(__0) as ClientChunk);

    private static void Dirtied(ClientChunk? chunk)
    {
        if (chunk == null) return;
        lock (Gate)
            if (OrderedCpu.TryGetValue(chunk, out var state)) state.Revision++;
    }

    // Called under Gate only after an actual mixed mesh. A dirty event during
    // tessellation advances the revision, so the next attempt re-evaluates it.
    // Each unchanged full/edge revision can request at most one mixed retry.
    private static void RememberOrdered(Work work)
    {
        if (work.EdgeOnly) work.Ordered.Edge = work.Revision;
        else work.Ordered.Full = work.Revision;
    }
}
