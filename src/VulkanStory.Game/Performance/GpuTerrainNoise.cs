using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game.Performance;

/// <summary>Groups stock column sign requests for compute, retaining the original threshold producer,
/// smoothing and block consumers. Raw-octave assistance remains available when density is unsupported.</summary>
internal static partial class GpuTerrainNoise
{
    private const int Capacity = 32768;
    private const int MaximumSnapshots = 8;
    private readonly record struct Key(long Seed, long X, long Y, long Z);
    private sealed record Root(NewNormalizedSimplexFractalNoise Noise, int Height, GenTerra Generator,
        HeadlessTerrainComparison.Probe? Probe = null, bool Allowed = true, string? Denied = null);
    private sealed class Workspace
    {
        internal readonly Dictionary<Key, int> Indices = new();
        internal readonly float[] Results = new float[Capacity];
    }
    private static readonly Stack<Workspace> Workspaces = new();
    // Keep CPU snapshots bounded independently of the two GPU/input leases:
    // completed readers must not reserve a GPU tile or allocate without a limit.
    private static readonly Stack<TerrainNoiseInput[]> Inputs = new();
    private sealed class Column(Root root) : IDisposable
    {
        internal readonly Root Root = root;
        internal Workspace? Work;
        internal TerrainNoiseInput[]? Input;
        internal ITerrainNoiseBatch? Batch;
        internal long Applied;
        internal string? Fallback;
        internal bool Leased, SnapshotLeased, Completed;
        internal void RetireBatch()
        {
            ITerrainNoiseBatch? batch = Batch;
            Batch = null;
            try { batch?.Dispose(); }
            finally
            {
                if (Input != null)
                {
                    lock (Inputs) Inputs.Push(Input);
                    Input = null;
                }
                if (Leased) { Leased = false; Slots.Release(); }
            }
        }
        public void Dispose()
        {
            try { RetireBatch(); }
            finally
            {
                Completed = false;
                if (Applied != 0) { Interlocked.Add(ref appliedSamples, Applied); Applied = 0; }
                if (Work != null)
                {
                    Work.Indices.Clear();
                    lock (Workspaces) Workspaces.Push(Work);
                    Work = null;
                }
                if (SnapshotLeased) { SnapshotLeased = false; SnapshotSlots.Release(); }
            }
        }
    }
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static readonly SemaphoreSlim SnapshotSlots = new(MaximumSnapshots, MaximumSnapshots);
    private static readonly MethodInfo Generate = AccessTools.Method(typeof(GenTerra), "generate",
        [typeof(IServerChunk[]), typeof(int), typeof(int), typeof(bool)])!;
    private static readonly MethodInfo ForColumn = AccessTools.Method(typeof(NewNormalizedSimplexFractalNoise), "ForColumn")!;
    private static readonly MethodInfo Evaluate = AccessTools.Method(typeof(NewSimplexNoiseLayer), "Evaluate_ImprovedXZ")!;
    private static readonly MethodInfo Sign = AccessTools.Method(typeof(NewNormalizedSimplexFractalNoise.ColumnNoise), "NoiseSign")!;
    private static readonly MethodInfo ParallelFor = AccessTools.Method(typeof(Parallel), "For",
        [typeof(int), typeof(int), typeof(ParallelOptions), typeof(Action<int>)])!;
    private static readonly FieldInfo Terrain = AccessTools.Field(typeof(GenTerra), "terrainNoise")!;
    private static readonly FieldInfo Api = AccessTools.Field(typeof(GenTerra), "api")!;
    private static readonly FieldInfo Entries = AccessTools.Field(typeof(NewNormalizedSimplexFractalNoise.ColumnNoise), "orderedOctaveEntries")!;
    private static MethodInfo? columnBody;
    private static FieldInfo? seed, x, z, frequencyY;
    private static Func<IWorldGenHandler, string?>? unsafeHandlers;
    private static volatile bool admitted;
    private static TerrainNoiseBackend? backend;
    private static string backendAvailability = "not-configured";
    private static long appliedSamples;
    internal static long AppliedSamples => Interlocked.Read(ref appliedSamples);
    [ThreadStatic] private static Root? root;
    [ThreadStatic] private static Column? column;

    // Resolve/read only during feature validation; retain the stock array without copying it.
    private static readonly Lazy<float[]?> StockGradients = new(() =>
    {
        FieldInfo? field = AccessTools.Field(typeof(NewSimplexNoiseLayer), "Gradients3D");
        return field?.IsStatic == true && field.FieldType == typeof(float[])
            ? (float[]?)field.GetValue(null) : null;
    });
    internal static float[] Gradients => StockGradients.Value!;
    internal static void Configure(VulkanDevice device, bool enabled)
    {
        ConfigureDensity(device, enabled);
        Volatile.Write(ref backendAvailability, enabled ? device.TerrainNoiseAvailabilityReason : "setting-disabled");
        Volatile.Write(ref backend, enabled && device.TerrainNoiseAvailable ? device.SubmitTerrainNoise : null);
    }

    internal static PerformanceFeature Create() => new("gpu-terrain-noise", s => s.GpuTerrainNoise, () =>
    {
        if (Generate == null || ForColumn == null || Evaluate?.ReturnType != typeof(float) || Sign == null ||
            Terrain?.FieldType != typeof(NewNormalizedSimplexFractalNoise) || Api?.FieldType != typeof(ICoreServerAPI) ||
            Entries?.FieldType.IsArray != true || StockGradients.Value?.Length != 1024)
            throw new InvalidOperationException("Stock terrain noise bindings changed.");
        Type entry = Entries.FieldType.GetElementType()!;
        seed = AccessTools.Field(entry, "Seed"); x = AccessTools.Field(entry, "X");
        z = AccessTools.Field(entry, "Z"); frequencyY = AccessTools.Field(entry, "FrequencyY");
        if (seed?.FieldType != typeof(long) || x?.FieldType != typeof(double) || z?.FieldType != typeof(double) || frequencyY?.FieldType != typeof(double))
            throw new InvalidOperationException("Stock terrain octave layout changed.");
        columnBody = typeof(GenTerra).GetNestedTypes(AccessTools.all).SelectMany(t => t.GetMethods(AccessTools.allDeclared))
            .Single(m => m.GetMethodBody() != null && PatchProcessor.GetOriginalInstructions(m).Any(i => i.Calls(ForColumn)));
        if (PatchProcessor.GetOriginalInstructions(Generate).Count(i => i.Calls(ParallelFor)) != 1 ||
            PatchProcessor.GetOriginalInstructions(Sign).Count(i => i.Calls(Evaluate)) != 1)
            throw new InvalidOperationException("Stock terrain noise call sites changed.");
        unsafeHandlers = AccessTools.Method(typeof(WorldgenPipeline), "Unsafe").CreateDelegate<Func<IWorldGenHandler, string?>>();
        ValidateDensity();
        _ = ServerTypes.Method(ServerTypes.EventApi, "TriggerInitWorldGen");
    }, harmony =>
    {
        harmony.Patch(Generate, prefix: new HarmonyMethod(typeof(GpuTerrainNoise), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(GpuTerrainNoise), nameof(End)),
            transpiler: new HarmonyMethod(typeof(GpuTerrainNoise), nameof(GenerateTranspiler)));
        harmony.Patch(columnBody, transpiler: new HarmonyMethod(typeof(GpuTerrainNoise), nameof(ColumnTranspiler)));
        harmony.Patch(Sign, transpiler: new HarmonyMethod(typeof(GpuTerrainNoise), nameof(SignTranspiler)));
        harmony.Patch(ServerTypes.Method(ServerTypes.EventApi, "TriggerInitWorldGen"),
            postfix: new HarmonyMethod(typeof(GpuTerrainNoise), nameof(Admit)));
    }, Uninstall: () => { admitted = false; Volatile.Write(ref backend, null); Volatile.Write(ref densityBackend, null); },
        WorldLeft: () => { admitted = false; Volatile.Write(ref backend, null); Volatile.Write(ref densityBackend, null); });

    private static void Admit(object __result) => admitted = __result is IWorldGenHandler handlers && unsafeHandlers!(handlers) == null;

    private static void Begin(GenTerra __instance, IServerChunk[] __0, out Root? __state)
    {
        __state = root; root = null;
        HeadlessTerrainComparison.Probe? probe = HeadlessTerrainComparison.ForChunks(__0);
        if (probe != null) probe.NoiseHook = true;
        string? denied = probe?.Gpu == false ? "cpu-reference" :
            __instance.GetType() != typeof(GenTerra) ? "non-stock-generator" :
            !admitted ? "worldgen-handlers-not-admitted" :
            PerformancePatchSet.Current?.GpuTerrainNoise != true ? "setting-disabled" :
            Volatile.Read(ref backend) == null ? "backend-unavailable: " + Volatile.Read(ref backendAvailability) :
            __0.Length > 64 ? "height-capacity" : null;
        if (denied != null && probe == null) return;
        // The pipeline admits only stock handler sets. Recheck these additional raw-noise dependencies,
        // including late patches, before touching a column; foreign policies always retain their CPU path.
        foreach (Type type in new[] { typeof(GenTerra), typeof(NewNormalizedSimplexFractalNoise),
            typeof(NewNormalizedSimplexFractalNoise.ColumnNoise), typeof(NewSimplexNoiseLayer) }.Concat(typeof(GenTerra).GetNestedTypes(AccessTools.all)))
            foreach (MethodInfo method in type.GetMethods(AccessTools.allDeclared))
                if (Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true)
                    denied ??= "foreign-noise-patch";
        int height = ((ICoreServerAPI)Api.GetValue(__instance)!).WorldManager.MapSizeY;
        if (height <= 2 || height != __0.Length * 32) denied ??= "height-layout";
        var noise = (NewNormalizedSimplexFractalNoise)Terrain.GetValue(__instance)!;
        if (noise.GetType() != typeof(NewNormalizedSimplexFractalNoise)) denied ??= "non-stock-noise";
        if (probe != null) { probe.Admitted = denied == null; if (denied != null) probe.Reason(denied); }
        if (denied == null || probe != null) root = new Root(noise, height, __instance, probe, denied == null, denied);
    }
    private static Exception? End(Root? __state, Exception? __exception) { root = __state; return __exception; }

    private static ParallelLoopResult RunColumns(int from, int to, ParallelOptions options, Action<int> action)
    {
        Root? capture = root;
        if (capture == null) return Parallel.For(from, to, options, action);
        if (capture.Allowed && Volatile.Read(ref densityBackend) != null && from == 0 && to == 1024 && DensitySlots.Wait(0))
        {
            try { return RunDensityColumns(capture, options, action); }
            finally { DensitySlots.Release(); }
        }
        return Parallel.For(from, to, options, index =>
        {
            Column? previous = column;
            using var scope = new Column(capture); column = scope;
            try { action(index); }
            finally { column = previous; }
        });
    }

    private static NewNormalizedSimplexFractalNoise.ColumnNoise Prepare(NewNormalizedSimplexFractalNoise noise,
        double relative, double[] amplitudes, double[] thresholds, double nx, double nz)
    {
        var result = noise.ForColumn(relative, amplitudes, thresholds, nx, nz);
        if (densityCapture != null) { densityCapture.Noise = result; return result; }
        Column? state = column;
        TerrainNoiseBackend? submit = Volatile.Read(ref backend);
        if (state == null) return result;
        // Stock consumes one ForColumn result entirely inside each parallel body.
        // If that body prepares another column, retire its previous snapshot first.
        state.Dispose();
        state.Fallback = null;
        if (!state.Root.Allowed) { state.Fallback = state.Root.Denied; return result; }
        if (!admitted) { state.Fallback = "worldgen-handlers-not-admitted"; return result; }
        if (!ReferenceEquals(noise, state.Root.Noise)) { state.Fallback = "noise-instance"; return result; }
        if (submit == null) { state.Fallback = "backend-unavailable"; return result; }
        if (PerformancePatchSet.Current?.GpuTerrainNoise != true) { state.Fallback = "setting-disabled"; return result; }
        if (!Slots.Wait(0)) { state.Fallback = "in-flight-capacity"; return result; }
        state.Leased = true;
        try
        {
            if (!SnapshotSlots.Wait(0)) { state.Fallback = "snapshot-capacity"; return result; }
            state.SnapshotLeased = true;
            lock (Workspaces) state.Work = Workspaces.Count != 0 ? Workspaces.Pop() : new Workspace();
            lock (Inputs) state.Input = Inputs.Count != 0 ? Inputs.Pop() : new TerrainNoiseInput[Capacity];
            Workspace work = state.Work;
            Array entries = (Array)Entries.GetValue(result)!;
            int height = state.Root.Height - 2;
            if (height <= 0 || entries.Length == 0 || entries.Length > Capacity / height)
            { state.Fallback = "sample-capacity"; return result; }
            int count = entries.Length * height;
            var inputs = state.Input;
            for (int octave = 0; octave < entries.Length; octave++)
            {
                object entry = entries.GetValue(octave)!;
                long s = (long)seed!.GetValue(entry)!;
                double ex = (double)x!.GetValue(entry)!, ez = (double)z!.GetValue(entry)!, fy = (double)frequencyY!.GetValue(entry)!;
                for (int y = 1; y <= height; y++)
                {
                    double ey = y * fy;
                    int index = octave * height + y - 1;
                    if (!Input(s, ex, ey, ez, out inputs[index])) { state.Fallback = "input-range"; return result; }
                    work.Indices.TryAdd(new Key(s, BitConverter.DoubleToInt64Bits(ex), BitConverter.DoubleToInt64Bits(ey), BitConverter.DoubleToInt64Bits(ez)), index);
                }
            }
            // Submission happens before the original threshold/density work. Readback waits only when
            // NoiseSign actually asks for a raw octave. Two independent columns can remain in flight.
            state.Batch = submit(inputs.AsSpan(0, count));
            if (state.Batch == null) state.Fallback = "submission-refused";
        }
        catch (Exception error)
        {
            state.Fallback = "preparation-failed: " + error.GetType().Name;
        }
        finally
        {
            if (state.Batch == null) state.Dispose();
        }
        return result;
    }

    private static bool Input(long seed, double x, double y, double z, out TerrainNoiseInput input)
    {
        input = default;
        double xz = x + z, s2 = xz * -0.211324865405187, yy = y * 0.577350269189626;
        double xr = x + s2 + yy, zr = z + s2 + yy, yr = xz * -0.577350269189626 + yy;
        if (!double.IsFinite(xr) || !double.IsFinite(yr) || !double.IsFinite(zr) ||
            xr < int.MinValue || xr >= int.MaxValue || yr < int.MinValue || yr >= int.MaxValue || zr < int.MinValue || zr >= int.MaxValue) return false;
        int ix = (int)Math.Floor(xr), iy = (int)Math.Floor(yr), iz = (int)Math.Floor(zr);
        input.Seed = seed;
        input.PrimeX = unchecked(ix * 0x5205402B9270C86FL);
        input.PrimeY = unchecked(iy * 0x598CD327003817B5L);
        input.PrimeZ = unchecked(iz * 0x5BCC226E9FA0BACBL);
        input.X = (float)(xr - ix); input.Y = (float)(yr - iy); input.Z = (float)(zr - iz);
        return true;
    }

    private static float Raw(long seed, double x, double y, double z)
    {
        if (densityCpuEvaluation) return NewSimplexNoiseLayer.Evaluate_ImprovedXZ(seed, x, y, z);
        Column? state = column;
        HeadlessTerrainComparison.Probe? probe = state?.Root.Probe;
        if (probe != null) Interlocked.Increment(ref probe.RequestedRaw);
        if (state?.Work != null && (PerformancePatchSet.Current?.GpuTerrainNoise != true || !admitted || Volatile.Read(ref backend) == null))
        {
            state.Fallback = "terrain-backend-inactive";
            state.Dispose();
        }
        Workspace? work = state?.Work;
        if (state != null && (state.Completed || state.Batch != null) && work != null &&
            work.Indices.TryGetValue(new Key(seed, BitConverter.DoubleToInt64Bits(x), BitConverter.DoubleToInt64Bits(y), BitConverter.DoubleToInt64Bits(z)), out int index))
        {
            if (!state.Completed)
            {
                try
                {
                    // Complete copies every sample before the batch/slot is retired.
                    // Work remains private until this stock column's consumers finish.
                    try { state.Completed = state.Batch!.Complete(work.Results); }
                    finally { state.RetireBatch(); }
                }
                catch (Exception error)
                {
                    state.Completed = false;
                    state.Fallback = "readback-failed: " + error.GetType().Name;
                }
            }
            if (state.Completed)
            {
                float value = work.Results[index];
                if (probe != null)
                {
                    // Temporary terrain probes verify actual raw GPU bits as well as
                    // final columns. The ordinary generator never repeats GPU work.
                    float stock = NewSimplexNoiseLayer.Evaluate_ImprovedXZ(seed, x, y, z);
                    uint actual = BitConverter.SingleToUInt32Bits(value), expected = BitConverter.SingleToUInt32Bits(stock);
                    if (actual != expected)
                        throw new InvalidOperationException($"Terrain GPU noise mismatch: seed={seed}, x={x:R}, y={y:R}, z={z:R}, GPU=0x{actual:X8}, CPU=0x{expected:X8}.");
                }
                state.Applied++;
                if (probe != null) Interlocked.Increment(ref probe.AppliedRaw);
                return value;
            }
            state.Fallback ??= "readback-refused";
            state.Dispose();
        }
        probe?.Reason(state?.Fallback ?? "raw-input-not-in-batch");
        return NewSimplexNoiseLayer.Evaluate_ImprovedXZ(seed, x, y, z);
    }
    private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> body, MethodInfo original, string replacement)
    {
        int count = 0;
        foreach (var instruction in body)
        {
            if (instruction.Calls(original)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(GpuTerrainNoise), replacement); count++; }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Stock terrain call site changed: " + original.Name);
    }
    private static IEnumerable<CodeInstruction> GenerateTranspiler(IEnumerable<CodeInstruction> body) => Replace(body, ParallelFor, nameof(RunColumns));
    private static IEnumerable<CodeInstruction> ColumnTranspiler(IEnumerable<CodeInstruction> body) =>
        Replace(Replace(body, ForColumn, nameof(Prepare)), Sign, nameof(CaptureDensitySign));
    private static IEnumerable<CodeInstruction> SignTranspiler(IEnumerable<CodeInstruction> body) => Replace(body, Evaluate, nameof(Raw));
}
