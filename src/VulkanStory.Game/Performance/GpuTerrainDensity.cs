using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game.Performance;

internal static partial class GpuTerrainNoise
{
    private const int DensityColumns = 32;
    private static readonly SemaphoreSlim DensitySlots = new(2, 2);
    private static TerrainDensityBackend? densityBackend;
    private static readonly FieldInfo ColumnResults = AccessTools.Field(typeof(GenTerra), "columnResults")!;
    private static readonly FieldInfo FullySolid = AccessTools.Field(typeof(GenTerra), "layerFullySolid")!;
    private static readonly FieldInfo FullyEmpty = AccessTools.Field(typeof(GenTerra), "layerFullyEmpty")!;
    private static FieldInfo solidities = null!;
    private static FieldInfo[] densityFields = [];
    [ThreadStatic] private static DensityCapture? densityCapture;
    [ThreadStatic] private static bool densityCpuEvaluation;

    private sealed class DensityCapture
    {
        internal NewNormalizedSimplexFractalNoise.ColumnNoise Noise;
        internal readonly List<TerrainDensityRequest> Requests = new();
        internal required BitArray Solidities;
        internal HeadlessTerrainComparison.Probe? Probe;
    }

    private static void ConfigureDensity(VulkanDevice device, bool enabled) =>
        Volatile.Write(ref densityBackend, enabled && device.TerrainDensityAvailable ? device.SubmitTerrainDensity : null);

    private static void ValidateDensity()
    {
        Type entry = Entries.FieldType.GetElementType()!;
        string[] names = ["Seed", "X", "FrequencyY", "Z", "Amplitude", "Threshold", "SmoothingFactor", "StopBound"];
        densityFields = names.Select(n => AccessTools.Field(entry, n) ?? throw new InvalidOperationException("Stock density octave layout changed.")).ToArray();
        for (int i = 0; i < densityFields.Length; i++)
            if (densityFields[i].FieldType != (i == 0 ? typeof(long) : typeof(double)))
                throw new InvalidOperationException("Stock density octave field changed.");
        solidities = AccessTools.Field(ColumnResults.FieldType.GetElementType(), "ColumnBlockSolidities")!;
        if (solidities?.FieldType != typeof(BitArray) || FullySolid.FieldType != typeof(bool[]) || FullyEmpty.FieldType != typeof(bool[]) ||
            PatchProcessor.GetOriginalInstructions(columnBody!).Count(i => i.Calls(Sign)) != 1 ||
            Marshal.SizeOf<TerrainDensityOctave>() != 64 || Marshal.SizeOf<TerrainDensityColumn>() != 16 || Marshal.SizeOf<TerrainDensityRequest>() != 16)
            throw new InvalidOperationException("Stock density producer layout changed.");
    }

    private static double CaptureDensitySign(ref NewNormalizedSimplexFractalNoise.ColumnNoise noise, double y, double threshold)
    {
        DensityCapture? capture = densityCapture;
        if (capture == null) return noise.NoiseSign(y, threshold);
        capture.Requests.Add(new TerrainDensityRequest { Y = y, InverseThreshold = threshold });
        if (capture.Probe != null) Interlocked.Increment(ref capture.Probe.RequestedDensity);
        // Only the solidity branch consumes this result. Repair those bits and the layer
        // reductions before generate can enter its original block/fluid writing stage.
        return 0;
    }

    private sealed class DensityGroup : IDisposable
    {
        internal required DensityCapture[] Captures;
        internal ITerrainDensityBatch? Batch;
        internal required uint[] Results;
        internal string? Fallback;
        public void Dispose() => Batch?.Dispose();
    }

    private static ParallelLoopResult RunDensityColumns(Root root, ParallelOptions options, Action<int> action)
    {
        Array columns = (Array)ColumnResults.GetValue(root.Generator)!;
        var pending = new Queue<DensityGroup>(2);
        ParallelLoopResult loop = default;
        try
        {
            for (int start = 0; start < 1024; start += DensityColumns)
            {
                var captures = new DensityCapture[DensityColumns];
                int first = start;
                // GPU readback from the previous group overlaps the next group's stock
                // landform interpolation, distortion, threshold production and smoothing.
                loop = Parallel.For(first, first + DensityColumns, options, index =>
                {
                    var capture = new DensityCapture { Solidities = (BitArray)solidities.GetValue(columns.GetValue(index))!, Probe = root.Probe };
                    DensityCapture? previous = densityCapture;
                    densityCapture = capture;
                    try { action(index); captures[index - first] = capture; }
                    finally { densityCapture = previous; }
                });
                if (pending.Count == 2) FinishDensity(root, pending.Dequeue());
                pending.Enqueue(SubmitDensity(captures));
            }
            while (pending.Count != 0) FinishDensity(root, pending.Dequeue());
        }
        finally
        {
            while (pending.Count != 0) pending.Dequeue().Dispose();
        }

        // Capture's provisional non-solid decisions must not survive the batch.
        // Reduce the completed original column bitsets, keeping the stock top/bottom policy.
        var full = (bool[])FullySolid.GetValue(root.Generator)!;
        var empty = (bool[])FullyEmpty.GetValue(root.Generator)!;
        Array.Fill(full, true, 1, root.Height - 2);
        Array.Fill(empty, true, 1, root.Height - 2);
        foreach (object entry in columns)
        {
            var bits = (BitArray)solidities.GetValue(entry)!;
            for (int y = 1; y < root.Height - 1; y++)
                if (bits[y]) empty[y] = false; else full[y] = false;
        }
        return loop;
    }

    private static DensityGroup SubmitDensity(DensityCapture[] captures)
    {
        var descriptors = new TerrainDensityColumn[captures.Length];
        var octaves = new List<TerrainDensityOctave>();
        var requests = new List<TerrainDensityRequest>();
        for (int i = 0; i < captures.Length; i++)
        {
            DensityCapture capture = captures[i];
            Array entries = (Array)Entries.GetValue(capture.Noise)!;
            descriptors[i] = new TerrainDensityColumn { OctaveStart = (uint)octaves.Count, OctaveCount = (uint)entries.Length,
                RequestStart = (uint)requests.Count, RequestCount = (uint)capture.Requests.Count };
            foreach (object entry in entries)
                octaves.Add(new TerrainDensityOctave { Seed = (long)densityFields[0].GetValue(entry)!,
                    X = (double)densityFields[1].GetValue(entry)!, FrequencyY = (double)densityFields[2].GetValue(entry)!,
                    Z = (double)densityFields[3].GetValue(entry)!, Amplitude = (double)densityFields[4].GetValue(entry)!,
                    Threshold = (double)densityFields[5].GetValue(entry)!, SmoothingFactor = (double)densityFields[6].GetValue(entry)!,
                    StopBound = (double)densityFields[7].GetValue(entry)! });
            requests.AddRange(capture.Requests);
        }
        var group = new DensityGroup { Captures = captures, Results = new uint[requests.Count] };
        try
        {
            TerrainDensityBackend? submit = Volatile.Read(ref densityBackend);
            if (admitted && PerformancePatchSet.Current?.GpuTerrainNoise == true && submit != null && requests.Count != 0)
                group.Batch = submit(descriptors, CollectionsMarshal.AsSpan(octaves), CollectionsMarshal.AsSpan(requests));
            if (group.Batch == null && requests.Count != 0) group.Fallback = "density-submission-refused";
        }
        catch (Exception error) { group.Fallback = "density-submit: " + error.GetType().Name; }
        return group;
    }

    private static void FinishDensity(Root root, DensityGroup group)
    {
        using (group)
        {
            bool gpu = false;
            try { gpu = group.Batch?.Complete(group.Results) == true; }
            catch (Exception error) { group.Fallback = "density-readback: " + error.GetType().Name; }
            int offset = 0;
            foreach (DensityCapture capture in group.Captures)
            {
                bool useGpu = gpu && !group.Results.AsSpan(offset, capture.Requests.Count).Contains(uint.MaxValue);
                foreach (TerrainDensityRequest request in capture.Requests)
                {
                    uint result = group.Results[offset++];
                    bool solid = useGpu ? (result & 1) != 0 : CpuDensitySign(ref capture.Noise, request);
                    if (useGpu && root.Probe != null)
                    {
                        if (solid != CpuDensitySign(ref capture.Noise, request))
                            throw new InvalidOperationException($"Terrain GPU density sign mismatch at y={request.Y:R}, inverse threshold={request.InverseThreshold:R}.");
                        Interlocked.Increment(ref root.Probe.CheckedDensity);
                    }
                    capture.Solidities[(int)request.Y] = solid;
                    if (useGpu && root.Probe != null) Interlocked.Increment(ref root.Probe.AppliedDensity);
                }
                if (!useGpu && capture.Requests.Count != 0) root.Probe?.Reason(group.Fallback ?? "density-input-range");
            }
        }
    }

    private static bool CpuDensitySign(ref NewNormalizedSimplexFractalNoise.ColumnNoise noise, TerrainDensityRequest request)
    {
        bool previous = densityCpuEvaluation;
        densityCpuEvaluation = true;
        try { return noise.NoiseSign(request.Y, request.InverseThreshold) > 0; }
        finally { densityCpuEvaluation = previous; }
    }
}
