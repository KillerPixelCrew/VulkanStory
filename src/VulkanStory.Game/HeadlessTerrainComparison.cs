using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;
using Vintagestory.ServerMods;
using VulkanStory.Game.Performance;

namespace VulkanStory.Game;

/// <summary>Serial, temporary Terrain-pass CPU/GPU pairs. Never installs, saves or deletes chunks.</summary>
internal static class HeadlessTerrainComparison
{
    private const string Marker = "vulkanstory-headless-terrain-probe";
    private static readonly object Gate = new();
    private static readonly Harmony Hooks = new("vulkanstory.headless.terrain-probe");
    private static readonly AccessTools.FieldRef<WorldChunk, object> StorageLock =
        AccessTools.FieldRefAccess<WorldChunk, object>("packUnpackLock");
    private static bool installed, started;
    private static Run? run;
    private static string? failure;
    private static bool complete, equal, gpuEquivalent;
    private static int written;
    [ThreadStatic] private static Probe? generating;

    internal sealed class Probe(int x, int z, bool gpu)
    {
        internal readonly int X = x, Z = z;
        internal readonly bool Gpu = gpu;
        internal readonly string Token = Guid.NewGuid().ToString("N");
        internal IServerChunk[]? Chunks;
        internal int Callbacks, Generations;
        internal bool Generated, SmoothingRequested, SmoothingPrevented, SmoothingObserved;
        internal bool NoiseHook, Admitted;
        internal long RequestedRaw, AppliedRaw;
        internal string? Error, Hash;
        internal readonly Dictionary<string, long> Fallback = new(StringComparer.Ordinal);
        internal void Reason(string reason)
        {
            lock (Fallback) Fallback[reason] = Fallback.GetValueOrDefault(reason) + 1;
        }
        internal Dictionary<string, long> Reasons()
        {
            // Cancellation can report while parallel noise callbacks still add fallback reasons.
            lock (Fallback) return new(Fallback, StringComparer.Ordinal);
        }
    }
    private sealed class Run(ServerMain server, string directory, List<Probe> probes)
    {
        internal ServerMain? Server = server;
        internal readonly string Directory = directory;
        internal readonly List<Probe> Probes = probes;
        internal int Next;
        internal Probe? Active;
    }

    internal static bool Pending { get { lock (Gate) return run != null; } }
    internal static string? Failure { get { lock (Gate) return failure; } }
    internal static bool Complete { get { lock (Gate) return complete; } }
    internal static bool Equal { get { lock (Gate) return equal; } }
    internal static bool GpuEquivalent { get { lock (Gate) return gpuEquivalent; } }
    internal static int Written { get { lock (Gate) return written; } }

    // Identity is the actual temporary chunks, not a coordinate or a global counter delta.
    internal static Probe? ForChunks(IServerChunk[] chunks)
    {
        if (!HeadlessHarnessOptions.Enabled || !Volatile.Read(ref started)) return null;
        lock (Gate)
        {
            Probe? probe = run?.Active;
            return probe?.Chunks is { Length: > 0 } known && SameChunks(known, chunks) ? probe : null;
        }
    }

    private static bool SameChunks(IServerChunk[] known, IServerChunk[] chunks)
    {
        if (known.Length != chunks.Length) return false;
        for (int i = 0; i < known.Length; i++)
            if (!ReferenceEquals(known[i], chunks[i])) return false;
        return true;
    }

    // !terrain-peek x z [x z ...]: one to four explicit 2x2 origins, paired CPU then GPU.
    internal static void Start(ServerMain server, string[] args)
    {
        if (!HeadlessHarnessOptions.Enabled || HeadlessHarnessOptions.MainMenuContext ||
            HeadlessHarnessOptions.FrameDirectory is not { } output)
            throw new InvalidOperationException("Terrain peeks require an isolated world harness output directory.");
        if (args.Length < 3 || args.Length > 9 || args.Length % 2 != 1)
            throw new FormatException("!terrain-peek needs one to four explicit 2x2 origins: x z [x z ...].");
        var manager = ((ICoreServerAPI)server.Api).WorldManager;
        if (manager.MapSizeY < 32 || manager.MapSizeY > 2048 || manager.MapSizeY % 32 != 0)
            throw new InvalidOperationException("Terrain probe height must be a multiple of 32, at most 2048.");
        var coordinates = new HashSet<(int X, int Z)>();
        var probes = new List<Probe>();
        for (int i = 1; i < args.Length; i += 2)
        {
            if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int z) ||
                x < 0 || z < 0 || x >= manager.MapSizeX / 32 - 1 || z >= manager.MapSizeZ / 32 - 1)
                throw new FormatException("Terrain probe origin must leave a valid 2x2 area inside the world.");
            for (int dz = 0; dz < 2; dz++)
                for (int dx = 0; dx < 2; dx++)
                {
                    if (!coordinates.Add((x + dx, z + dz))) throw new FormatException("Terrain probe areas must not overlap.");
                    probes.Add(new(x + dx, z + dz, false));
                    probes.Add(new(x + dx, z + dz, true));
                }
        }
        lock (Gate)
        {
            if (started) throw new InvalidOperationException("Only one bounded terrain-peek command is allowed per harness process.");
            started = true;
            try
            {
                Install();
                string directory = Path.Combine(output, "terrain-comparison");
                Directory.CreateDirectory(directory);
                ISaveGame save = manager.SaveGame;
                var config = save.WorldConfiguration.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .ToDictionary(p => p.Key, p => p.Value.ToJsonToken(), StringComparer.Ordinal);
                string configJson = JsonSerializer.Serialize(config);
                File.WriteAllText(Path.Combine(directory, "world.json"), JsonSerializer.Serialize(new
                {
                    schema = 1, seed = save.Seed, save.WorldType, save.PlayStyle, save.CreatedGameVersion,
                    save.LastSavedGameVersion, manager.MapSizeX, manager.MapSizeY, manager.MapSizeZ,
                    configuration = config, configurationSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson))),
                    untilPass = "Terrain", temporary = true, maxConcurrentPeeks = 1,
                    maxColumnsPerMode = 16, maxHeight = 2048,
                    binaryLayout = "little-endian: int32 schema,x,z,height; y/z/x voxels as int32 solid(layer=1),fluid,sunlight,blocklight; three int32-length-prefixed ushort maps (terrain,rain,sedimentary); int32-length-prefixed cave bytes (-1=null); ushort YMax",
                    origins = args.Skip(1).ToArray(), gpuSettingEnabled = PerformancePatchSet.Current?.GpuTerrainNoise == true,
                    gameLibrary = typeof(ServerMain).Assembly.Location
                }));
                var current = new Run(server, directory, probes);
                run = current;
                server.EnqueueMainThreadTask(() => Next(current));
            }
            catch (Exception error) { failure = error.GetBaseException().Message; run = null; throw; }
        }
    }

    private static void Install()
    {
        if (installed) return;
        var runner = ServerTypes.Method(ServerTypes.Supply, "runGenerators", typeof(ChunkColumnLoadRequest), typeof(int));
        var generate = AccessTools.Method(typeof(GenTerra), "generate", [typeof(IServerChunk[]), typeof(int), typeof(int), typeof(bool)])
            ?? throw new MissingMethodException("GenTerra.generate");
        try
        {
            Hooks.Patch(runner, transpiler: new(typeof(HeadlessTerrainComparison), nameof(GuardGenerators)));
            Hooks.Patch(generate, prefix: new(typeof(HeadlessTerrainComparison), nameof(BeginGenerate)),
                finalizer: new(typeof(HeadlessTerrainComparison), nameof(EndGenerate)));
            Hooks.Patch(AccessTools.Method(typeof(GenTerra), "PreventSmoothing"),
                postfix: new(typeof(HeadlessTerrainComparison), nameof(SmoothingPolicy)));
            Hooks.Patch(AccessTools.Method(typeof(GameRenderSession), "NoteWorldLeft"),
                prefix: new(typeof(HeadlessTerrainComparison), nameof(WorldLeft)));
            Hooks.Patch(AccessTools.Method(typeof(GameRenderSession), "StopAndDrain"),
                prefix: new(typeof(HeadlessTerrainComparison), nameof(WorldLeft)));
            installed = true;
        }
        catch { Hooks.UnpatchAll(Hooks.Id); throw; }
    }

    private static IEnumerable<CodeInstruction> GuardGenerators(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo invoke = AccessTools.Method(typeof(ChunkColumnGenerationDelegate), "Invoke");
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(invoke))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HeadlessTerrainComparison), nameof(InvokeGenerator));
                count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Terrain callback error observation binding changed.");
    }

    private static void InvokeGenerator(ChunkColumnGenerationDelegate callback, IChunkColumnGenerateRequest request)
    {
        Probe? probe;
        lock (Gate)
        {
            probe = run?.Active;
            if (probe == null || request.ChunkGenParams?.GetString(Marker) != probe.Token) probe = null;
            else { probe.Chunks = request.Chunks; probe.Callbacks++; }
        }
        Probe? previous = generating;
        generating = probe;
        try { callback(request); }
        catch (Exception error) { if (probe != null) probe.Error = error.ToString(); throw; }
        finally { generating = previous; }
    }

    private static void BeginGenerate(IServerChunk[] __0, bool __3, out Probe? __state)
    {
        __state = ForChunks(__0);
        if (__state == null) return;
        __state.Generations++;
        __state.SmoothingRequested = __3;
    }
    private static Exception? EndGenerate(Probe? __state, Exception? __exception)
    {
        if (__state != null)
        {
            __state.Generated = __exception == null;
            if (__exception != null) __state.Error = __exception.ToString();
        }
        return __exception;
    }
    private static void SmoothingPolicy(bool __result)
    {
        if (generating is not { } probe) return;
        probe.SmoothingObserved = true;
        probe.SmoothingPrevented = __result;
    }

    private static void Next(Run? current)
    {
        lock (Gate)
        {
            if (current == null || !ReferenceEquals(run, current)) return;
            try
            {
                if (current.Next == current.Probes.Count)
                {
                    equal = current.Probes.Chunk(2).All(pair => pair[0].Hash == pair[1].Hash);
                    gpuEquivalent = equal && current.Probes.Where(p => p.Gpu).All(p => p.Admitted && p.RequestedRaw > 0 && p.AppliedRaw > 0);
                    complete = true;
                    WriteResult(current);
                    current.Server = null;
                    run = null;
                    return;
                }
                Probe probe = current.Probes[current.Next++];
                current.Active = probe;
                var parameters = new TreeAttribute(); parameters.SetString(Marker, probe.Token);
                ((ICoreServerAPI)current.Server!.Api).WorldManager.PeekChunkColumn(probe.X, probe.Z, new ChunkPeekOptions
                {
                    UntilPass = EnumWorldGenPass.Terrain, ChunkGenParams = parameters,
                    OnGenerated = columns =>
                    {
                        lock (Gate)
                        {
                            try
                            {
                                if (!ReferenceEquals(run, current) || !ReferenceEquals(current.Active, probe)) return;
                                if (columns.Count != 1 || !columns.TryGetValue(new(probe.X, probe.Z), out var chunks) ||
                                    chunks.Length != ((ICoreServerAPI)current.Server!.Api).WorldManager.MapSizeY / 32 ||
                                    probe.Chunks == null || !SameChunks(probe.Chunks, chunks) ||
                                    probe.Callbacks == 0 || probe.Generations != 1 || !probe.Generated || probe.Error != null)
                                    throw new InvalidOperationException("Terrain callback failed/incomplete: " + probe.Error);
                                Dump(current, probe, chunks);
                                written++;
                                probe.Chunks = null;
                                current.Active = null;
                                current.Server!.EnqueueMainThreadTask(() => Next(current));
                            }
                            catch (Exception error) { Abort(current, error.ToString()); }
                            finally
                            {
                                // Stock PeekChunkAreaLocking removes these temporary columns from its queues
                                // before handing them to this callback; it neither installs nor disposes them.
                                // Release them here even when world exit cancelled the run before delivery.
                                Exception? cleanupError = null;
                                foreach (var column in columns.Values)
                                    foreach (var chunk in column)
                                        try { chunk.Dispose(); }
                                        catch (Exception error) { cleanupError ??= error; }
                                probe.Chunks = null;
                                if (cleanupError != null && ReferenceEquals(run, current))
                                    Abort(current, "Terrain peek cleanup failed: " + cleanupError);
                            }
                        }
                    }
                });
            }
            catch (Exception error) { Abort(current, error.ToString()); }
        }
    }

    private static void Dump(Run current, Probe probe, IServerChunk[] chunks)
    {
        string name = $"{probe.X}-{probe.Z}-{(probe.Gpu ? "gpu" : "cpu")}";
        string path = Path.Combine(current.Directory, name + ".bin");
        var blockIds = new HashSet<int>();
        var voxels = new int[32768 * 4];
        using (var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
        {
            // LE int32 header: schema, x, z, height. All voxels are four LE int32s: solid, fluid, sunlight, blocklight.
            writer.Write(1); writer.Write(probe.X); writer.Write(probe.Z); writer.Write(chunks.Length * 32);
            foreach (var chunk in chunks)
            {
                if (chunk is not WorldChunk stock) throw new InvalidOperationException("Unexpected terrain chunk storage owner.");
                // Stock WorldChunk.Unpack_ReadOnly returns 'was packed', not success: CreateNew
                // already owns ChunkData and returns false here. Its packUnpackLock protects
                // unpack/pack and Dispose's return of that storage to ChunkDataPool.
                lock (StorageLock(stock))
                {
                    if (chunk.Disposed) throw new InvalidOperationException("Disposed terrain chunk.");
                    chunk.Unpack_ReadOnly();
                    IChunkBlocks data = chunk.Data;
                    IChunkLight light = chunk.Lighting;
                    if (data == null || data.Length != 32768 || light == null || chunk.Disposed)
                        throw new InvalidOperationException("Incomplete/disposed terrain chunk storage.");
                    // The completed peek exclusively owns these columns: no generator can still
                    // write them. Use the real layer getters (including their null-layer zeros),
                    // never Empty/MaybeBlocks or a synthesized all-air/light representation.
                    for (int index = 0; index < 32768; index++)
                    {
                        int offset = index * 4;
                        voxels[offset] = data.GetBlockId(index, 1);
                        voxels[offset + 1] = data.GetFluid(index);
                        voxels[offset + 2] = light.GetSunlight(index);
                        voxels[offset + 3] = light.GetBlocklight(index);
                    }
                    if (chunk.Disposed) throw new InvalidOperationException("Terrain chunk disposed during export.");
                }
                // Do disk I/O after releasing the stock storage lock. Linear stock indices are y/z/x.
                for (int offset = 0; offset < voxels.Length; offset += 4)
                {
                    blockIds.Add(voxels[offset]); blockIds.Add(voxels[offset + 1]);
                    writer.Write(voxels[offset]); writer.Write(voxels[offset + 1]);
                    writer.Write(voxels[offset + 2]); writer.Write(voxels[offset + 3]);
                }
            }
            var map = chunks[0].MapChunk;
            void Heights(ushort[] values)
            {
                if (values.Length != 1024) throw new InvalidOperationException("Terrain height map layout changed.");
                writer.Write(values.Length); foreach (ushort value in values) writer.Write(value);
            }
            Heights(map.WorldGenTerrainHeightMap); Heights(map.RainHeightMap); Heights(map.SedimentaryThicknessMap);
            byte[]? distort = map.CaveHeightDistort;
            if (distort?.Length > 32768) throw new InvalidOperationException("Terrain cave distortion exceeds probe bound.");
            writer.Write(distort?.Length ?? -1); if (distort != null) writer.Write(distort);
            writer.Write(map.YMax);
        }
        using (var stream = File.OpenRead(path)) probe.Hash = Convert.ToHexString(SHA256.HashData(stream));
        File.WriteAllText(Path.Combine(current.Directory, name + ".blocks.json"), JsonSerializer.Serialize(blockIds.Order().Select(id =>
        {
            var block = current.Server!.GetBlock(id) ?? throw new InvalidOperationException("Unknown generated block id " + id);
            return new { id, code = block.Code?.ToString(), material = block.BlockMaterial.ToString() };
        })));
        if (!probe.NoiseHook) probe.Reason("gpu-terrain-noise-hook-unavailable-or-disabled");
    }

    private static void WriteResult(Run current) => File.WriteAllText(Path.Combine(current.Directory, "result.json"),
        JsonSerializer.Serialize(new
        {
            schema = 1, complete, success = failure == null && complete, error = failure, written,
            outputEqual = equal, gpuEquivalent, adjacencyCaptured = true,
            oldWorldSmoothingObserved = current.Probes.Any(p => p.SmoothingRequested && p.SmoothingObserved && !p.SmoothingPrevented),
            columns = current.Probes.Select(p => new
            {
                x = p.X, z = p.Z, mode = p.Gpu ? "gpu" : "cpu", sha256 = p.Hash,
                callbacks = p.Callbacks, generations = p.Generations, generated = p.Generated, error = p.Error,
                smoothingRequested = p.SmoothingRequested, smoothingPolicyObserved = p.SmoothingObserved,
                smoothingPrevented = p.SmoothingPrevented, noiseHook = p.NoiseHook, admitted = p.Admitted,
                requestedRaw = p.RequestedRaw, appliedRaw = p.AppliedRaw,
                rawCoverage = !p.Gpu ? "cpu-reference" : p.AppliedRaw == 0 ? "all-fallback" :
                    p.AppliedRaw == p.RequestedRaw ? "all-gpu" : "mixed-gpu-cpu",
                fallbackReasons = p.Reasons()
            })
        }));

    private static void Abort(Run current, string reason)
    {
        failure = reason; complete = equal = gpuEquivalent = false;
        foreach (Probe probe in current.Probes) probe.Chunks = null;
        current.Active = null;
        current.Server = null;
        run = null;
        try { WriteResult(current); }
        catch (Exception error) { failure += "; terrain result write failed: " + error.Message; }
    }
    private static void WorldLeft() => Cancel("World exited before terrain probes completed.");
    internal static void Cancel(string reason)
    {
        lock (Gate) if (run is { } current) Abort(current, reason);
    }
}
