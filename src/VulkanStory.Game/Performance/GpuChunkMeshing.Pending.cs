using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

internal static partial class GpuChunkMeshing
{
    // At most two entries: admission reserves one of the same two GPU tiles.
    // CPU parts and equality references are retained only until that job retires.
    private sealed class PendingGeneration(Work work, int cpuVertices)
    {
        internal readonly Work Work = work;
        internal readonly int CpuVertices = cpuVertices;
        internal bool Dequeued;
    }
    private static readonly Dictionary<TesselatedChunk, PendingGeneration> Generations = new();
    private static readonly AccessTools.FieldRef<TesselatedChunk, int> VertexCount =
        AccessTools.FieldRefAccess<TesselatedChunk, int>("VerticesCount");
    private static readonly AccessTools.FieldRef<ChunkTesselatorManager, object> ReadyQueueLock =
        AccessTools.FieldRefAccess<ChunkTesselatorManager, object>("tessChunksQueuePriorityLock");
    private static readonly AccessTools.FieldRef<ChunkTesselatorManager, Queue<TesselatedChunk>> ReadyQueue =
        AccessTools.FieldRefAccess<ChunkTesselatorManager, Queue<TesselatedChunk>>("tessChunksQueuePriority");
    private static readonly AccessTools.FieldRef<ChunkTesselatorManager, bool> ProcessReady =
        AccessTools.FieldRefAccess<ChunkTesselatorManager, bool>("processPrioQueue");
    private static readonly AccessTools.FieldRef<ClientChunk, bool> QueuedForUpload =
        AccessTools.FieldRefAccess<ClientChunk, bool>("queuedForUpload");
    internal static int PendingGenerations { get { lock (Gate) return Generations.Count; } }

    private static void PollGenerations(ChunkTesselatorManager manager)
    {
        var ready = new List<TesselatedChunk>(2);
        lock (Gate)
        {
            foreach (var (chunk, pending) in Generations.ToArray())
            {
                Work work = pending.Work;
                if (work.Generation != generation || SourceChunk(chunk).Disposed || !LightingReady(work.Producer))
                { CancelGeneration(chunk, "generation-changed"); continue; }
                try
                {
                    if (!work.Batch.TryComplete(out var ranges)) continue;
                    CompleteGeneration(work, chunk, ranges, pending.CpuVertices);
                    Generations.Remove(chunk);
                    work.Ordered.InFlight--;
                    work.Batch.Dispose();
                    // If stock hasn't selected it yet, it is already in its
                    // original queue. Otherwise give the completed selection
                    // back to the stock manager, including its real callback.
                    if (pending.Dequeued) ready.Add(chunk);
                }
                catch (Exception error)
                {
                    Game(work.Producer).Logger.Warning("VulkanStory: GPU chunk meshing completion failed: {0}", error.Message);
                    if (work.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(work.ProbeBatch, "generation-failed", new { error = error.ToString() });
                    enabled = false; faulted = true; generation++;
                    RetirePending("cpu-fallback");
                    world?.RedrawAllBlocks();
                    break;
                }
            }
        }
        // Stock merges take queue lock -> Gate; never invert that ordering.
        lock (ReadyQueueLock(manager))
            foreach (var chunk in ready)
            {
                if (ReadyQueue(manager) is not { } queue)
                { UnusedDispose.Invoke(chunk, null); continue; }
                QueuedForUpload(SourceChunk(chunk)) = true;
                queue.Enqueue(chunk);
                ProcessReady(manager) = true;
            }
    }
    // Called under Gate. Retiring a batch merely leaves its GPU tile leased
    // until a subsequent status poll; it never waits on a fence.
    private static void CancelGeneration(TesselatedChunk chunk, string reason)
    {
        if (!Generations.Remove(chunk, out var pending)) return;
        Work work = pending.Work;
        work.Ordered.InFlight--;
        RetireParts(Centers(chunk), reason); RetireParts(Edges(chunk), reason);
        UnusedDispose.Invoke(chunk, null);
        Centers(chunk) = null!; Edges(chunk) = null!;
        work.Batch.Dispose();
    }
}
