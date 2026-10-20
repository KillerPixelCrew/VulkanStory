using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

internal static partial class GpuChunkMeshing
{
    // Counts refusal events: once per category per producer batch, rather than
    // once per cube. Counters are process cumulative like the backend totals.
    private enum Refusal { Control, World, Backend, ForeignPatch, Capacity, Allocation, Snapshot, UnsupportedProducer, OrderedFallback }
    private static readonly long[] AdmissionRefusals = new long[Enum.GetValues<Refusal>().Length];
    private static long admissionRefusals;
    internal static IEnumerable<KeyValuePair<string, long>> AdmissionCounters()
    {
        yield return new("chunkMeshingAdmissionRefusals", Interlocked.Read(ref admissionRefusals));
        foreach (Refusal category in Enum.GetValues<Refusal>())
            yield return new("chunkMeshingAdmissionRefusals" + category, Interlocked.Read(ref AdmissionRefusals[(int)category]));
    }
    private sealed class Admission
    {
        internal string? Reason;
        internal object? Detail;
        internal bool LeaseAttempted;
        internal readonly HashSet<Refusal> Counted = new();
        internal void Refuse(Refusal category, string reason, object? detail = null)
        {
            Reason = reason; Detail = detail;
            if (!Counted.Add(category)) return;
            Interlocked.Increment(ref admissionRefusals);
            Interlocked.Increment(ref AdmissionRefusals[(int)category]);
        }
        internal object Describe() => new { reason = Reason, leaseAttempted = LeaseAttempted, detail = Detail };
    }
    [ThreadStatic] private static Admission? admission;
    private sealed record ProducerState(Work? Work, Admission? Admission);

    private static bool RefuseCube(Work work, TCTCache vars, Refusal category, string reason, object? detail = null)
    {
        bool first = work.Admission.Counted.Add(category);
        if (first)
        {
            Interlocked.Increment(ref admissionRefusals);
            Interlocked.Increment(ref AdmissionRefusals[(int)category]);
        }
        if (work.ProbeBatch != 0)
            HeadlessChunkMeshingProbe.Refusal(work.ProbeBatch, vars.posX, vars.posY, vars.posZ,
                new { reason, leaseAttempted = true, leaseIdentity = RuntimeHelpers.GetHashCode(work.Batch), detail });
        return Cpu(vars);
    }

    private sealed class OrderedFallback
    {
        internal long Revision;
        internal long Full = -1, Edge = -1;
        internal int InFlight;
    }

    private static void MarkedDirty(ClientWorldMap __instance, int __0, int __1, int __2) =>
        Dirtied(__instance.GetChunk(__0, __1, __2) as ClientChunk);

    private static void IndexedDirty(ClientWorldMap __instance, long __0) =>
        Dirtied(__instance.GetChunk(__0) as ClientChunk);

    private static void Dirtied(ClientChunk? chunk)
    {
        if (chunk == null) return;
        lock (Gate)
            if (OrderedCpu.TryGetValue(chunk, out var state))
            {
                state.Revision++;
                // Stock has already cleared enquedForRedraw before NowProcessChunk.
                // This request enters its dirty queue even while geometry is in
                // flight or retained in a detached, not-yet-published payload.
                if (state.InFlight != 0 || Pending.Any(p => ReferenceEquals(p.Chunk, chunk)))
                    Interlocked.Increment(ref requeuedDuringFlight);
            }
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
