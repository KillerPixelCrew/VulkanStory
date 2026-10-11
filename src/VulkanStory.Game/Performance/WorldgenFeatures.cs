using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;

namespace VulkanStory.Game.Performance;

/// <summary>Server types this file binds by name; several are internal to the game.</summary>
internal static class ServerTypes
{
    internal static Type Supply => AccessTools.TypeByName("Vintagestory.Server.ServerSystemSupplyChunks") ??
        throw new TypeLoadException("Vintagestory.Server.ServerSystemSupplyChunks");
    internal static Type Unload => AccessTools.TypeByName("Vintagestory.Server.ServerSystemUnloadChunks") ??
        throw new TypeLoadException("Vintagestory.Server.ServerSystemUnloadChunks");
    internal static Type EventApi => AccessTools.TypeByName("Vintagestory.Server.ServerEventAPI") ??
        throw new TypeLoadException("Vintagestory.Server.ServerEventAPI");
    internal static MethodInfo Method(Type type, string name, params Type[] parameters) =>
        AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
    internal static System.Func<object, T> Getter<T>(Type type, string field)
    {
        FieldInfo info = AccessTools.Field(type, field) ?? throw new MissingFieldException(type.FullName, field);
        var instance = Expression.Parameter(typeof(object));
        return Expression.Lambda<System.Func<object, T>>(Expression.Convert(Expression.Field(Expression.Convert(instance, type), info), typeof(T)), instance).Compile();
    }
}

/// <summary>W1: singleplayer worldgen workers with the safety the game's own multi-threaded mode lacks.</summary>
/// <remarks>
/// The game already runs one worker per worldgen pass when <c>additionalWorldGenThreadsCount</c> is above zero (off by default), so
/// different passes of different columns overlap. This feature sets that count and adds what makes the overlap safe:
/// a 3x3 column footprint lease around every population (passes write into neighbouring columns), one lock shared by the
/// Vegetation and SunLightFlood passes (they share the world-gen light illuminator), a lock per generator instance across
/// all of its chunk, map-chunk and map-region handlers, atomic entity ids from generators, and a pause of the workers while
/// half-generated columns are written out. When a handler comes from another mod or carries another owner's Harmony patch,
/// or a worker fails, generation falls back to the chunk thread alone.
/// </remarks>
internal static class WorldgenPipeline
{
    private static readonly HashSet<string> KnownAssemblies = new(StringComparer.Ordinal) { "VintagestoryLib", "VSEssentials", "VSSurvivalMod", "VSCreativeMod" };
    private static readonly AccessTools.FieldRef<ChunkColumnLoadRequest, int> ChunkX = AccessTools.FieldRefAccess<ChunkColumnLoadRequest, int>("chunkX");
    private static readonly AccessTools.FieldRef<ChunkColumnLoadRequest, int> ChunkZ = AccessTools.FieldRefAccess<ChunkColumnLoadRequest, int>("chunkZ");
    private static readonly AccessTools.FieldRef<ServerMain, bool> Stopped = AccessTools.FieldRefAccess<ServerMain, bool>("stopped");
    private static readonly ConcurrentDictionary<long, int> Leased = new();
    private static readonly ConditionalWeakTable<object, object> TargetLocks = new();
    private static readonly object LightPasses = new();
    [ThreadStatic] private static List<long>? held;
    [ThreadStatic] private static int depth;
    [ThreadStatic] private static int expectedStart, expectedEnd;
    private static ChunkServerThread? chunkThread;
    private static ServerMain? server;
    private static volatile bool managed, serial;
    private static int poolCapacity, originalWorkerCount;
    private static System.Func<object, object>? supplyOfThread;
    private static MethodInfo? pauseWorkers, resumeWorkers;

    private static ConstructorInfo Constructor() => AccessTools.Constructor(typeof(ChunkServerThread),
        [typeof(ServerMain), typeof(string), typeof(CancellationToken)]) ?? throw new MissingMethodException(typeof(ChunkServerThread).FullName, ".ctor");
    private static FieldInfo LastEntityId() => AccessTools.Field(AccessTools.Field(typeof(ServerMain), "SaveGameData")!.FieldType, "LastEntityId") ??
        throw new MissingFieldException("SaveGame", "LastEntityId");

    internal static PerformanceFeature Create() => new("worldgen-pipeline", settings => settings.WorldgenWorkers != "off", () =>
    {
        _ = Constructor();
        if (AccessTools.Field(typeof(ChunkServerThread), nameof(ChunkServerThread.additionalWorldGenThreadsCount)) == null)
            throw new MissingFieldException(typeof(ChunkServerThread).FullName, "additionalWorldGenThreadsCount");
        _ = ServerTypes.Method(ServerTypes.Supply, "GenerateChunkColumns_OnSeparateThread", typeof(int), typeof(int));
        _ = ServerTypes.Method(ServerTypes.Supply, "PopulateChunk", typeof(ChunkColumnLoadRequest));
        _ = ServerTypes.Method(ServerTypes.EventApi, "TriggerInitWorldGen");
        _ = ServerTypes.Method(ServerTypes.Unload, "UnloadGeneratingChunkColumns", typeof(long));
        pauseWorkers = ServerTypes.Method(ServerTypes.Supply, "PauseAllWorldgenThreads", typeof(int));
        resumeWorkers = ServerTypes.Method(ServerTypes.Supply, "ResumeAllWorldgenThreads");
        supplyOfThread = ServerTypes.Getter<object>(typeof(ChunkServerThread), "loadsavechunks");
        if (EntityIdSpan(PatchProcessor.GetOriginalInstructions(ServerTypes.Method(typeof(BlockAccessorWorldGen), nameof(BlockAccessorWorldGen.AddEntity),
            typeof(Entity)))).Start < 0) throw new InvalidOperationException("Worldgen entity id anchor changed.");
    }, harmony =>
    {
        harmony.Patch(Constructor(), postfix: new HarmonyMethod(typeof(WorldgenPipeline), nameof(Configure)));
        harmony.Patch(ServerTypes.Method(ServerTypes.Supply, "OnBeginGameReady", typeof(SaveGame)),
            postfix: new HarmonyMethod(typeof(WorldgenPipeline), nameof(ApplyPolicy)));
        harmony.Patch(ServerTypes.Method(ServerTypes.Supply, "GetUpdateInterval"),
            postfix: new HarmonyMethod(typeof(WorldgenPipeline), nameof(PositiveInterval)));
        harmony.Patch(ServerTypes.Method(ServerTypes.Supply, "GenerateChunkColumns_OnSeparateThread", typeof(int), typeof(int)),
            prefix: new HarmonyMethod(typeof(WorldgenPipeline), nameof(WorkerStep)),
            finalizer: new HarmonyMethod(typeof(WorldgenPipeline), nameof(EndWorkerStep)));
        harmony.Patch(ServerTypes.Method(ServerTypes.Supply, "PopulateChunk", typeof(ChunkColumnLoadRequest)),
            prefix: new HarmonyMethod(typeof(WorldgenPipeline), nameof(BeginPopulate)),
            finalizer: new HarmonyMethod(typeof(WorldgenPipeline), nameof(EndPopulate)));
        harmony.Patch(ServerTypes.Method(ServerTypes.EventApi, "TriggerInitWorldGen"),
            postfix: new HarmonyMethod(typeof(WorldgenPipeline), nameof(Prepare)));
        harmony.Patch(ServerTypes.Method(ServerTypes.Unload, "UnloadGeneratingChunkColumns", typeof(long)),
            prefix: new HarmonyMethod(typeof(WorldgenPipeline), nameof(BeginUnload)),
            finalizer: new HarmonyMethod(typeof(WorldgenPipeline), nameof(EndUnload)));
        harmony.Patch(ServerTypes.Method(typeof(BlockAccessorWorldGen), nameof(BlockAccessorWorldGen.AddEntity), typeof(Entity)),
            transpiler: new HarmonyMethod(typeof(WorldgenPipeline), nameof(EntityIdTranspiler)));
    });

    // Footprint protection remains installed while the reusable worker pool exists, including live off/on transitions.
    private static bool Active => managed && chunkThread != null;
    internal static bool ParallelActive => Active && !serial && chunkThread!.additionalWorldGenThreadsCount > 0;
    internal static int WorkerCount => poolCapacity;

    /// <summary>The in-process server whose chunk thread was last constructed (headless harness directives only).</summary>
    internal static ServerMain? IntegratedServer => server;

    /// <summary>Worker count for the policy; Auto keeps small machines on the single chunk thread.</summary>
    private static int Workers(string policy) => policy switch
    {
        "1" => 1, "2" => 2, "3" => 3,
        _ => Environment.ProcessorCount <= 8 ? 0 : Environment.ProcessorCount < 16 ? 1 : 2,
    };

    private static void Configure(ChunkServerThread __instance, ServerMain server)
    {
        chunkThread = __instance; WorldgenPipeline.server = server; serial = false;
        managed = !server.ReducedServerThreads;
        originalWorkerCount = __instance.additionalWorldGenThreadsCount;
        poolCapacity = Math.Max(3, originalWorkerCount);
        // Start the game's own reusable workers once. Per-call admission applies the current live policy.
        // Even an initially disabled option can then be enabled without reconstructing or restarting the world.
        if (managed) __instance.additionalWorldGenThreadsCount = poolCapacity;
    }
    private static void ApplyPolicy()
    {
        if (!managed || chunkThread == null || serial) return;
        RendererSettings? settings = PerformancePatchSet.Current;
        string policy = settings?.WorldgenWorkers ?? "off";
        int requested = policy == "off" ? 0 : policy == "auto" ? Math.Max(originalWorkerCount, Workers(policy)) : Workers(policy);
        // Terrain alone owns pass 1; the chunk thread continues all later passes. Idle pool workers can assist Terrain.
        if (settings?.ParallelTerrainGeneration == true) requested = Math.Max(1, requested);
        chunkThread.additionalWorldGenThreadsCount = Math.Min(poolCapacity, requested);
    }
    private static void PositiveInterval(ref int __result) => __result = Math.Max(1, __result);

    private static bool WorkerStep(int stageStart, int stageEnd, ref int __result, out (int Start, int End) __state)
    {
        __state = (expectedStart, expectedEnd);
        expectedStart = stageStart; expectedEnd = stageEnd;
        ApplyPolicy();
        if (!managed) return true;
        if (!serial && stageStart <= chunkThread!.additionalWorldGenThreadsCount) return true;
        __result = 0;
        return false;
    }
    private static void EndWorkerStep((int Start, int End) __state)
    { expectedStart = __state.Start; expectedEnd = __state.End; }

    /// <summary>Runs once the world type's handlers are known and before spawn chunks load; repeated by <c>/wgen regen</c>.</summary>
    private static void Prepare(object __result)
    {
        if (!managed || chunkThread is not { } thread || __result is not IWorldGenHandler handlers) return;
        string? reason = Unsafe(handlers);
        if (reason != null)
        {
            Fallback("Worldgen workers disabled: " + reason);
            return;
        }
        for (int pass = 0; pass < handlers.OnChunkColumnGen.Length; pass++)
        {
            var list = handlers.OnChunkColumnGen[pass];
            if (list == null) continue;
            bool light = pass is (int)EnumWorldGenPass.Vegetation or (int)EnumWorldGenPass.NeighbourSunLightFlood;
            for (int index = 0; index < list.Count; index++)
                if (list[index].Target is not ColumnHandler)
                    list[index] = new ColumnHandler(list[index], LockOf(list[index]), light).Invoke;
        }
        for (int index = 0; index < handlers.OnMapChunkGen.Count; index++)
            if (handlers.OnMapChunkGen[index].Target is not MapChunkHandler)
                handlers.OnMapChunkGen[index] = new MapChunkHandler(handlers.OnMapChunkGen[index], LockOf(handlers.OnMapChunkGen[index])).Invoke;
        for (int index = 0; index < handlers.OnMapRegionGen.Count; index++)
            if (handlers.OnMapRegionGen[index].Target is not MapRegionHandler)
                handlers.OnMapRegionGen[index] = new MapRegionHandler(handlers.OnMapRegionGen[index], LockOf(handlers.OnMapRegionGen[index])).Invoke;
        ServerMain.Logger.Notification("VulkanStory: {0} worldgen worker(s) active.", thread.additionalWorldGenThreadsCount);
    }

    private static object LockOf(Delegate handler) => TargetLocks.GetValue(handler.Target ?? handler.Method.DeclaringType!, _ => new object());

    private static IEnumerable<Delegate> Originals(IWorldGenHandler handlers)
    {
        foreach (var list in handlers.OnChunkColumnGen) if (list != null) foreach (var handler in list) yield return Unwrap(handler);
        foreach (var handler in handlers.OnMapChunkGen) yield return Unwrap(handler);
        foreach (var handler in handlers.OnMapRegionGen) yield return Unwrap(handler);
    }

    private static Delegate Unwrap(Delegate handler) => handler.Target switch
    {
        ColumnHandler column => column.Original,
        MapChunkHandler chunk => chunk.Original,
        MapRegionHandler region => region.Original,
        _ => handler,
    };

    /// <summary>Returns why concurrent generation is unsafe, or null: foreign generators and third-party patches keep generation serial.</summary>
    private static string? Unsafe(IWorldGenHandler handlers)
    {
        foreach (var handler in Originals(handlers))
        {
            Type? type = handler.Method.DeclaringType;
            string? assembly = type?.Assembly.GetName().Name;
            if (assembly == null || !KnownAssemblies.Contains(assembly)) return "generator from " + (assembly ?? "an unknown assembly");
            try
            {
                if (ForeignPatch(handler.Method)) return "Harmony patch on " + type!.FullName;
                foreach (var nested in new[] { type! }.Concat(type!.GetNestedTypes(AccessTools.all)))
                    foreach (var method in nested.GetMethods(AccessTools.allDeclared))
                        if (ForeignPatch(method)) return "Harmony patch on " + nested.FullName + "." + method.Name;
            }
            catch (Exception error) { return "patch inspection failed: " + error.Message; }
        }
        return null;
    }

    private static bool ForeignPatch(MethodBase method) =>
        Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true;

    private static void Fallback(string reason)
    {
        serial = true;
        if (chunkThread != null) chunkThread.additionalWorldGenThreadsCount = 0;
        ServerMain.Logger.Warning("VulkanStory: {0}; the chunk thread generates alone.", reason);
    }

    private static bool BeginPopulate(ChunkColumnLoadRequest chunkRequest)
    {
        if (!Active || depth++ > 0) return true;
        int x = ChunkX(chunkRequest), z = ChunkZ(chunkRequest);
        var keys = held ??= new List<long>(9);
        while (true)
        {
            keys.Clear();
            bool acquired = true;
            for (int dx = -1; dx <= 1 && acquired; dx++)
                for (int dz = -1; dz <= 1 && acquired; dz++)
                {
                    long key = ((long)(x + dx) << 32) | (uint)(z + dz);
                    if (Leased.TryAdd(key, Environment.CurrentManagedThreadId)) keys.Add(key);
                    else acquired = false;
                }
            if (acquired)
                return expectedEnd == 0 || chunkRequest.CurrentIncompletePass_AsInt >= expectedStart && chunkRequest.CurrentIncompletePass_AsInt < expectedEnd;
            foreach (long key in keys) Leased.TryRemove(key, out _);
            keys.Clear();
            Interlocked.Increment(ref PerformanceCounters.WorldgenLeaseConflicts);
            if (server == null || Stopped(server) || serial) return false;
            Thread.Sleep(1);
        }
    }

    private static Exception? EndPopulate(Exception? __exception)
    {
        if (depth > 0 && --depth == 0 && held != null)
        {
            foreach (long key in held) Leased.TryRemove(key, out _);
            held.Clear();
            Interlocked.Increment(ref PerformanceCounters.WorldgenColumns);
        }
        if (__exception != null && Active && Thread.CurrentThread.Name?.StartsWith("worldgen", StringComparison.Ordinal) == true)
        {
            Interlocked.Increment(ref PerformanceCounters.WorldgenWorkerFaults);
            Fallback("a worldgen worker failed (" + __exception.Message + ")");
        }
        return __exception;
    }

    // Writing half-generated columns must not overlap a population that writes into them.
    private static bool BeginUnload(out bool __state)
    {
        __state = false;
        if (!Active || chunkThread == null || supplyOfThread!(chunkThread) is not { } supply) return true;
        if (!(bool)pauseWorkers!.Invoke(supply, [2000])!)
        {
            resumeWorkers!.Invoke(supply, null);
            return false;
        }
        __state = true;
        return true;
    }

    private static Exception? EndUnload(Exception? __exception, bool __state)
    {
        if (__state && chunkThread != null && supplyOfThread!(chunkThread) is { } supply) resumeWorkers!.Invoke(supply, null);
        return __exception;
    }

    /// <summary>Locates <c>dup; ldfld LastEntityId; ldc 1; conv.i8; add; stloc; ldloc; stfld LastEntityId; ldloc</c>.</summary>
    private static (int Start, int End) EntityIdSpan(IReadOnlyList<CodeInstruction> body)
    {
        FieldInfo field = LastEntityId();
        for (int index = 1; index < body.Count; index++)
        {
            if (!body[index].LoadsField(field) || body[index - 1].opcode != OpCodes.Dup) continue;
            int store = -1;
            for (int next = index + 1; next < Math.Min(body.Count, index + 8); next++) if (body[next].StoresField(field)) { store = next; break; }
            if (store < 0 || store + 1 >= body.Count || !body[store + 1].IsLdloc()) return (-1, -1);
            return (index - 1, store + 1);
        }
        return (-1, -1);
    }

    private static IEnumerable<CodeInstruction> EntityIdTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        var (start, end) = EntityIdSpan(body);
        if (start < 0) throw new InvalidOperationException("Incoming worldgen entity id anchor changed.");
        var replacement = new List<CodeInstruction>
        {
            new(OpCodes.Ldflda, LastEntityId()),
            new(OpCodes.Call, AccessTools.Method(typeof(Interlocked), nameof(Interlocked.Increment), [typeof(long).MakeByRefType()])),
        };
        replacement[0].labels.AddRange(body[start].labels);
        body.RemoveRange(start, end - start + 1);
        body.InsertRange(start, replacement);
        return body;
    }

    private sealed class ColumnHandler(ChunkColumnGenerationDelegate original, object gate, bool lightPass)
    {
        internal ChunkColumnGenerationDelegate Original => original;
        internal void Invoke(IChunkColumnGenerateRequest request)
        {
            if (!lightPass && ParallelTerrainGeneration.TryInvoke(original, request, gate)) return;
            if (lightPass) lock (LightPasses) lock (gate) original(request);
            else lock (gate) original(request);
        }
    }

    private sealed class MapChunkHandler(MapChunkGeneratorDelegate original, object gate)
    {
        internal MapChunkGeneratorDelegate Original => original;
        internal void Invoke(IMapChunk mapChunk, int chunkX, int chunkZ) { lock (gate) original(mapChunk, chunkX, chunkZ); }
    }

    private sealed class MapRegionHandler(MapRegionGeneratorDelegate original, object gate)
    {
        internal MapRegionGeneratorDelegate Original => original;
        internal void Invoke(IMapRegion mapRegion, int regionX, int regionZ, Vintagestory.API.Datastructures.ITreeAttribute chunkGenParams)
        { lock (gate) original(mapRegion, regionX, regionZ, chunkGenParams); }
    }
}

/// <summary>S3: fetches a chunk column's raw chunk rows in parallel through read-only connections, then lets the original deserialize them in order.</summary>
/// <remarks>Only for saves in WAL mode (the game's corruption protection), and only while no write transaction holds the database's
/// transaction lock. The original <c>TryLoadChunkColumn</c> runs unchanged; its per-row reads are answered from the prefetch.</remarks>
internal static class ParallelChunkReads
{
    private const int Readers = 4;
    private sealed class Reader(DbConnection connection, DbCommand command, DbParameter position) : IDisposable
    {
        internal byte[]? Read(ulong key)
        {
            position.Value = key;
            using DbDataReader reader = command.ExecuteReader();
            return reader.Read() ? reader["data"] as byte[] : null;
        }
        public void Dispose() { command.Dispose(); connection.Dispose(); }
    }
    private sealed class Pool : IDisposable
    {
        internal readonly ConcurrentBag<Reader> Idle = new();
        internal bool Disabled;
        public void Dispose() { Disabled = true; while (Idle.TryTake(out var reader)) reader.Dispose(); }
    }

    private static readonly ConditionalWeakTable<GameDatabase, Pool> Pools = new();
    private static readonly AccessTools.FieldRef<ChunkServerThread, GameDatabase> Database = AccessTools.FieldRefAccess<ChunkServerThread, GameDatabase>("gameDatabase");
    private static readonly AccessTools.FieldRef<ChunkColumnLoadRequest, int> ChunkX = AccessTools.FieldRefAccess<ChunkColumnLoadRequest, int>("chunkX");
    private static readonly AccessTools.FieldRef<ChunkColumnLoadRequest, int> ChunkZ = AccessTools.FieldRefAccess<ChunkColumnLoadRequest, int>("chunkZ");
    private static readonly AccessTools.FieldRef<ChunkColumnLoadRequest, int> Dimension = AccessTools.FieldRefAccess<ChunkColumnLoadRequest, int>("dimension");
    [ThreadStatic] private static Dictionary<(int X, int Y, int Z, int Dimension), byte[]?>? prefetched;
    private static System.Func<object, ChunkServerThread>? threadOfSupply;
    private static System.Func<object, ServerMain>? serverOfSupply;
    private static System.Func<object, object>? connectionOf;
    private static FieldInfo? sqliteConnection, databaseFile;

    internal static PerformanceFeature Create() => new("parallel-chunk-reads", settings => settings.ParallelChunkReads, () =>
    {
        _ = ServerTypes.Method(ServerTypes.Supply, "TryLoadChunkColumn", typeof(ChunkColumnLoadRequest));
        _ = ServerTypes.Method(typeof(GameDatabase), nameof(GameDatabase.GetChunk), typeof(int), typeof(int), typeof(int), typeof(int));
        threadOfSupply = ServerTypes.Getter<ChunkServerThread>(ServerTypes.Supply, "chunkthread");
        serverOfSupply = ServerTypes.Getter<ServerMain>(ServerTypes.Supply, "server");
        connectionOf = ServerTypes.Getter<object>(typeof(GameDatabase), "conn");
        sqliteConnection = AccessTools.Field(typeof(SQLiteDBConnection), "sqliteConn") ?? throw new MissingFieldException("sqliteConn");
        databaseFile = AccessTools.Field(typeof(SQLiteDBConnection), "databaseFileName") ?? throw new MissingFieldException("databaseFileName");
    }, harmony =>
    {
        harmony.Patch(ServerTypes.Method(ServerTypes.Supply, "TryLoadChunkColumn", typeof(ChunkColumnLoadRequest)),
            prefix: new HarmonyMethod(typeof(ParallelChunkReads), nameof(Prefetch)),
            finalizer: new HarmonyMethod(typeof(ParallelChunkReads), nameof(Forget)));
        harmony.Patch(ServerTypes.Method(typeof(GameDatabase), nameof(GameDatabase.GetChunk), typeof(int), typeof(int), typeof(int), typeof(int)),
            prefix: new HarmonyMethod(typeof(ParallelChunkReads), nameof(Answer)));
        harmony.Patch(ServerTypes.Method(typeof(GameDatabase), nameof(GameDatabase.Dispose)),
            postfix: new HarmonyMethod(typeof(ParallelChunkReads), nameof(Close)));
    });

    private static void Prefetch(object __instance, ChunkColumnLoadRequest chunkRequest)
    {
        prefetched = null;
        if (PerformancePatchSet.Current?.ParallelChunkReads != true) return;
        try
        {
            GameDatabase database = Database(threadOfSupply!(__instance));
            if (connectionOf!(database) is not SQLiteDBConnection connection) return;
            Pool pool = Pools.GetValue(database, _ => new Pool());
            if (pool.Disabled) return;
            int height = serverOfSupply!(__instance).WorldMap.ChunkMapSizeY;
            int x = ChunkX(chunkRequest), z = ChunkZ(chunkRequest), dimension = Dimension(chunkRequest);
            var rows = new byte[]?[height];
            // A held transaction lock means a write is in progress; read through the original connection then.
            if (!Monitor.TryEnter(connection.transactionLock)) return;
            try
            {
                Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = Readers }, y =>
                {
                    Reader reader = Rent(pool, connection);
                    try { rows[y] = reader.Read(ChunkPos.ToChunkIndex(x, y, z, dimension)); }
                    finally { pool.Idle.Add(reader); }
                });
            }
            finally { Monitor.Exit(connection.transactionLock); }
            var answers = new Dictionary<(int, int, int, int), byte[]?>(height);
            for (int y = 0; y < height; y++) answers[(x, y, z, dimension)] = rows[y];
            prefetched = answers;
            Interlocked.Increment(ref PerformanceCounters.ParallelChunkReads);
        }
        catch (Exception error)
        {
            prefetched = null;
            ServerMain.Logger.Warning("VulkanStory: parallel chunk reads disabled for this save: {0}", error.Message);
            if (threadOfSupply!(__instance) is { } thread && Pools.TryGetValue(Database(thread), out var pool)) pool.Dispose();
        }
    }

    private static Reader Rent(Pool pool, SQLiteDBConnection owner)
    {
        if (pool.Idle.TryTake(out var reader)) return reader;
        if (pool.Disabled) throw new ObjectDisposedException("chunk read pool");
        var original = (DbConnection)sqliteConnection!.GetValue(owner)!;
        var connection = (DbConnection)Activator.CreateInstance(original.GetType(),
            "Data Source=" + databaseFile!.GetValue(owner) + ";Mode=ReadOnly;Pooling=False")!;
        connection.Open();
        using (var mode = connection.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode";
            if (!string.Equals(mode.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                connection.Dispose();
                pool.Disabled = true;
                throw new InvalidOperationException("the save is not in WAL mode");
            }
        }
        DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM chunk WHERE position=@position";
        DbParameter position = command.CreateParameter();
        position.ParameterName = "position";
        command.Parameters.Add(position);
        return new Reader(connection, command, position);
    }

    private static Exception? Forget(Exception? __exception) { prefetched = null; return __exception; }

    private static bool Answer(int x, int y, int z, int dimension, ref byte[]? __result)
    {
        if (prefetched == null || !prefetched.Remove((x, y, z, dimension), out var bytes)) return true;
        __result = bytes;
        return false;
    }

    private static void Close(GameDatabase __instance)
    {
        if (Pools.TryGetValue(__instance, out var pool)) { pool.Dispose(); Pools.Remove(__instance); }
    }
}
