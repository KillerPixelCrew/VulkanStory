using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.Server;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>Bounded observation of one real server edit through the ordinary terrain pipeline.</summary>
internal static class HeadlessChunkMeshingProbe
{
    private static Run? active;
    private static int runs;
    private sealed class Run(ClientMain client, string path, int number)
    {
        internal readonly WeakReference<ClientMain> Client = new(client);
        internal readonly WeakReference<GameRenderSession> Session = new(RuntimeBootstrap.Current.Session);
        internal readonly string Path = path;
        internal readonly int Number = number;
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal readonly ConcurrentQueue<string> Lines = new();
        internal readonly object Gate = new();
        internal Timer? Timer;
        internal int X, Y, Z, Dimension, Block, Batches, Rows;
        internal bool Closed;
        internal void Record(string stage, object detail, long batch = 0)
        {
            lock (Gate)
            {
                if (Closed || Rows >= 1024 || Rows >= 1023 && stage != "closed") return;
                var clock = Session.TryGetTarget(out var session) ? session.ChunkMeshingProbeClock : default;
                Lines.Enqueue(JsonSerializer.Serialize(new { probe = Number, sequence = ++Rows,
                    milliseconds = Stopwatch.GetElapsedTime(Started).TotalMilliseconds,
                    clock.WorldFrame, clock.ScenarioTick, clock.RendererFrame, stage, batch, detail }));
            }
        }
    }

    internal static bool Active => HeadlessHarnessOptions.Enabled && Volatile.Read(ref active) != null;

    // !meshing-cube dx dy dz; no client SetBlock, direct renderer input, or priority/lease override.
    internal static void Start(ServerMain server, ICoreClientAPI api, int dx, int dy, int dz)
    {
        if (!HeadlessHarnessOptions.Enabled || HeadlessHarnessOptions.MainMenuContext ||
            HeadlessHarnessOptions.FrameDirectory is not { } directory || api.World is not ClientMain client)
            throw new InvalidOperationException("Meshing cube requires an isolated world harness with frame output.");
        if (Math.Abs((long)dx) > 16 || Math.Abs((long)dy) > 16 || Math.Abs((long)dz) > 16)
            throw new FormatException("Meshing cube offsets must be within 16 blocks of the player.");
        if (Active) throw new InvalidOperationException("A meshing probe is already active (90 second lifetime).");
        int number = Interlocked.Increment(ref runs);
        if (number > 8) throw new InvalidOperationException("At most eight meshing probes per harness process.");
        Directory.CreateDirectory(directory);
        var run = new Run(client, Path.Combine(directory, $"meshing-probe-{number:D2}.jsonl"), number);
        // Never silently append to evidence from a previous process.
        using (File.Open(run.Path, FileMode.CreateNew, FileAccess.Write)) { }
        if (Interlocked.CompareExchange(ref active, run, null) != null)
            throw new InvalidOperationException("A meshing probe is already active.");
        run.Record("scheduled", new { dx, dy, dz, client = RuntimeHelpers.GetHashCode(client),
            server = RuntimeHelpers.GetHashCode(server), lifetimeSeconds = 90, maxRows = 1024, maxBatches = 64 });
        run.Timer = new Timer(_ => Flush(run), null, 250, 250);
        try { server.EnqueueMainThreadTask(() => Edit(run, server, dx, dy, dz)); }
        catch (Exception error) { run.Record("server-edit-failed", new { error = error.ToString() }); Close(run, "enqueue-failed"); }
    }

    private static void Edit(Run run, ServerMain server, int dx, int dy, int dz)
    {
        if (!ReferenceEquals(Volatile.Read(ref active), run)) return;
        try
        {
            IWorldAccessor world = server;
            var player = world.AllOnlinePlayers.FirstOrDefault()?.Entity ?? throw new InvalidOperationException("No player.");
            var pos = player.Pos.AsBlockPos.Add(dx, dy, dz);
            if (pos.dimension != 0) throw new InvalidOperationException("Meshing cube requires the ordinary dimension.");
            IBlockAccessor blocks = world.BlockAccessor;
            if (pos.X < 1 || pos.Z < 1 || pos.Y < 1 || pos.X >= blocks.MapSizeX - 1 ||
                pos.Z >= blocks.MapSizeZ - 1 || pos.Y >= blocks.MapSizeY - 1)
                throw new InvalidOperationException("Target neighborhood is outside the world.");
            Block rock = world.GetBlock(new AssetLocation("game:rock-granite")) ?? throw new InvalidOperationException("No granite.");
            if (rock.BlockId == 0) throw new InvalidOperationException("Granite resolved to air.");
            var save = ((ICoreServerAPI)server.Api).WorldManager.SaveGame;
            run.X = pos.X; run.Y = pos.Y; run.Z = pos.Z; run.Dimension = pos.dimension;
            Volatile.Write(ref run.Block, rock.BlockId);
            run.Record("target", new { x = run.X, y = run.Y, z = run.Z, dimension = run.Dimension,
                chunkX = pos.X / 32, chunkY = pos.InternalY / 32, chunkZ = pos.Z / 32,
                block = rock.BlockId, code = rock.Code.ToString(), drawType = rock.DrawType.ToString(),
                renderPass = rock.RenderPass.ToString(), aoType = rock.GetType().FullName,
                save.SavegameIdentifier, save.WorldName, save.Seed });
            // Preflight every touched cell before the first write: unloaded cells must never look like air.
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    for (int x = -1; x <= 1; x++)
                    {
                        var cell = pos.Copy().Add(x, y, z);
                        if (blocks.GetChunkAtBlockPos(cell) is not { Disposed: false })
                            throw new InvalidOperationException("Target neighborhood is not loaded on server.");
                    }
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    for (int x = -1; x <= 1; x++)
                    {
                        var cell = pos.Copy().Add(x, y, z);
                        blocks.SetBlock(0, cell, BlockLayersAccess.Fluid);
                        blocks.SetBlock(0, cell, BlockLayersAccess.Solid);
                    }
            blocks.SetBlock(rock.BlockId, pos, BlockLayersAccess.Solid);
            blocks.TriggerNeighbourBlockUpdate(pos);
            blocks.MarkBlockDirty(pos);
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    for (int x = -1; x <= 1; x++)
                    {
                        var cell = pos.Copy().Add(x, y, z);
                        int expected = (x | y | z) == 0 ? rock.BlockId : 0;
                        if (blocks.GetBlock(cell, BlockLayersAccess.Solid).BlockId != expected ||
                            blocks.GetBlock(cell, BlockLayersAccess.Fluid).BlockId != 0)
                            throw new InvalidOperationException("Server neighborhood readback differs from requested edit.");
                    }
            run.Record("server-edit-succeeded", new { serverChunk = RuntimeHelpers.GetHashCode(blocks.GetChunkAtBlockPos(pos)),
                cells = 27, fluid = 0, neighborSolid = 0, centerSolid = rock.BlockId });
        }
        catch (Exception error)
        {
            run.Record("server-edit-failed", new { error = error.ToString(), partialEditPossible = true });
            Close(run, "server-edit-failed");
        }
    }

    internal static long Snapshot(ClientMain client, int cx, int cy, int cz, ClientChunk chunk,
        bool visible, IChunkMeshBatch? lease, bool edgeOnly, Block[] solids, Block[] fluids)
    {
        Run? run = Volatile.Read(ref active);
        if (run == null || Volatile.Read(ref run.Block) == 0 || !run.Client.TryGetTarget(out var known) || !ReferenceEquals(known, client) ||
            cx != run.X / 32 || cy != run.Y / 32 || cz != run.Z / 32) return 0;
        int attempt = Interlocked.Increment(ref run.Batches);
        if (attempt > 64) return 0;
        long batch = ((long)run.Number << 32) | (uint)attempt;
        int index = (((run.Y & 31) + 1) * 34 + (run.Z & 31) + 1) * 34 + (run.X & 31) + 1;
        if (solids.Length != 39304 || fluids.Length != 39304)
        { run.Record("client-snapshot-invalid", new { solids = solids.Length, fluids = fluids.Length }, batch); return batch; }
        var solid = new int[27]; var fluid = new int[27]; bool matches = true; int n = 0;
        for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
                for (int x = -1; x <= 1; x++, n++)
                {
                    int i = index + y * 1156 + z * 34 + x;
                    solid[n] = solids[i]?.BlockId ?? -1; fluid[n] = fluids[i]?.BlockId ?? -1;
                    matches &= solid[n] == ((x | y | z) == 0 ? run.Block : 0) && fluid[n] == 0;
                }
        run.Record("client-snapshot", new { clientChunk = RuntimeHelpers.GetHashCode(chunk), visible, edgeOnly,
            targetAndAirMatch = matches, solid, fluid, order = "y/z/x, offsets -1..1", index }, batch);
        run.Record(lease != null ? "gpu-admitted" : "cpu-no-gpu-admission",
            new { snapshotMatches = matches, leaseIdentity = lease == null ? (int?)null : RuntimeHelpers.GetHashCode(lease) }, batch);
        return batch;
    }

    internal static void Candidates(long batch, int x, int y, int z, ReadOnlySpan<ChunkMeshFace> faces)
    {
        Run? run = For(batch);
        if (run == null || x != run.X || y != run.Y || z != run.Z) return;
        run.Record("target-candidates", new { x, y, z, count = faces.Length,
            faces = faces.ToArray().Select(f => new { index = f.Index, side = f.Side, bin = f.Bin }).ToArray() }, batch);
    }
    internal static void Generated(long batch, ChunkMeshRange[] ranges, ChunkMeshBin[] bins) =>
        For(batch)?.Record("generated-ranges", new { ranges, bins, faces = ranges.Sum(r => r.FaceCount) }, batch);
    internal static void Event(long batch, string stage, object detail) => For(batch)?.Record(stage, detail, batch);
    private static Run? For(long batch)
    {
        Run? run = Volatile.Read(ref active);
        return batch != 0 && run?.Number == (batch >> 32) ? run : null;
    }
    internal static void WorldLeft()
    { if (Volatile.Read(ref active) is { } run) Close(run, "world-left"); }

    private static void Close(Run run, string reason)
    {
        lock (run.Gate)
        {
            if (run.Closed) return;
            run.Record("closed", new { reason, batchesObserved = Math.Min(run.Batches, 64),
                batchesTruncated = run.Batches > 64, rowsTruncated = run.Rows >= 1023 });
            run.Closed = true;
            Interlocked.CompareExchange(ref active, null, run);
        }
        run.Timer?.Dispose();
        ThreadPool.QueueUserWorkItem(_ => Flush(run));
    }
    private static void Flush(Run run)
    {
        // Disk writes are never performed by producer/pool hooks or under the meshing lock.
        try
        {
            lock (run.Lines)
            {
                using var writer = new StreamWriter(run.Path, append: true);
                while (run.Lines.TryDequeue(out string? line)) writer.WriteLine(line);
            }
        }
        catch (Exception error)
        {
            ServerMain.Logger.Error("VulkanStory meshing probe write failed: {0}", error.Message);
            lock (run.Gate) { run.Closed = true; Interlocked.CompareExchange(ref active, null, run); }
            run.Timer?.Dispose();
            return;
        }
        if (!run.Closed && Stopwatch.GetElapsedTime(run.Started).TotalSeconds >= 90) Close(run, "lifetime-ended");
    }
}

internal sealed partial class GameRenderSession
{
    // Sampled at the event, including on terrain workers; this is not completed-frame proof.
    internal (long WorldFrame, int? ScenarioTick, ulong? RendererFrame) ChunkMeshingProbeClock =>
        (Interlocked.Read(ref headlessWorldFrame), scenarioTick, device?.LatencyFrameId);
}
