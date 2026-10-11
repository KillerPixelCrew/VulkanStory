using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Common.Database;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>C3: the occlusion culler's own ray casts, precomputed once into a shared prefix tree and walked once per cull.</summary>
/// <remarks>
/// <para>The original casts three rays from fixed offsets inside the camera chunk to every position of a view-distance shell and walks
/// each ray chunk by chunk, choosing the exit face with plane intersection math, marking chunks visible and stopping at the first chunk
/// that is not traversable from its entry face to its exit face. A ray's sequence of steps (chunk offset, entry face, exit face)
/// depends only on the shell, never on the camera position or the world, so it is computed once per view distance here.</para>
/// <para>All rays are merged into one prefix tree: rays that share their first steps share those nodes. A cull walks the tree once,
/// performing exactly the original's per-step world tests (chunk lookup, mark visible, traversability, valid position) and pruning a
/// whole subtree where the original would stop every ray through it. The visible set is the original's; only the per-step plane
/// math and the repeated walking of shared prefixes are gone. Where a ray crosses exactly through a chunk edge, the original's face
/// choice depends on floating-point rounding at the absolute camera position; the tree makes the same choice for every position.
/// With runtime diagnostics on, every cull also replays the original rays and counts chunks on which the two results differ.</para>
/// <para>The camera-chunk change test, the always-visible 3x4x3 neighbourhood and the buffer swap are the original's. Below the
/// original's chunk-count threshold, or with occlusion culling off, the original method runs unchanged. The chunk dictionary is read
/// without its lock during the walk, exactly as the original ray walk does.</para>
/// </remarks>
internal static class ChunkVisibilityWalk
{
    /// <summary>Prefix tree of ray steps; node 0 is the root and carries no step.</summary>
    private sealed class RayTree
    {
        internal readonly List<int> X = [0], Y = [0], Z = [0], FirstChild = [-1], NextSibling = [-1];
        internal readonly List<sbyte> Entry = [-1], Exit = [-1];
        internal readonly List<short> Manhattan = [0];
        internal Vec3i[]? Shell;
        internal int Rays, Steps;

        internal int Child(int parent, int x, int y, int z, int entry, int exit)
        {
            for (int node = FirstChild[parent]; node >= 0; node = NextSibling[node])
                if (X[node] == x && Y[node] == y && Z[node] == z && Entry[node] == entry && Exit[node] == exit) return node;
            int added = X.Count;
            X.Add(x); Y.Add(y); Z.Add(z); Entry.Add((sbyte)entry); Exit.Add((sbyte)exit);
            Manhattan.Add((short)(Math.Abs(x) + Math.Abs(y) + Math.Abs(z)));
            FirstChild.Add(-1); NextSibling.Add(FirstChild[parent]);
            FirstChild[parent] = added;
            return added;
        }
    }

    private static readonly AccessTools.FieldRef<ChunkCuller, ClientMain> Game = AccessTools.FieldRefAccess<ChunkCuller, ClientMain>("game");
    private static readonly AccessTools.FieldRef<ChunkCuller, Vec3i> Center = AccessTools.FieldRefAccess<ChunkCuller, Vec3i>("centerpos");
    private static readonly AccessTools.FieldRef<ChunkCuller, Vec3i[]> ShellPositions = AccessTools.FieldRefAccess<ChunkCuller, Vec3i[]>("cubicShellPositions");
    private static readonly AccessTools.FieldRef<ChunkCuller, bool> AboveLimit = AccessTools.FieldRefAccess<ChunkCuller, bool>("isAboveHeightLimit");
    private static readonly AccessTools.FieldRef<ChunkCuller, int> RegenCount = AccessTools.FieldRefAccess<ChunkCuller, int>("qCount");
    private static readonly AccessTools.FieldRef<ChunkCuller, bool> NowOff = AccessTools.FieldRefAccess<ChunkCuller, bool>("nowOff");
    private static readonly AccessTools.FieldRef<ClientWorldMap, Dictionary<long, ClientChunk>> Chunks =
        AccessTools.FieldRefAccess<ClientWorldMap, Dictionary<long, ClientChunk>>("chunks");
    private static readonly AccessTools.FieldRef<ClientWorldMap, object> ChunksLock = AccessTools.FieldRefAccess<ClientWorldMap, object>("chunksLock");
    private static readonly AccessTools.FieldRef<ClientMain, ChunkRenderer> Renderer = AccessTools.FieldRefAccess<ClientMain, ChunkRenderer>("chunkRenderer");
    // The original's three rays per shell position: (y offset, x offset); z is always 0.5.
    private static readonly (double Y, double X)[] RayOffsets = [(0.25, 0.5), (0.75, 0.5), (0.75, 0.0)];
    private static Action<ClientChunk, bool>? setVisible;
    private static Action<ChunkRenderer>? swapBuffers;
    private static Func<ClientWorldMap, int, int, int, bool>? validChunk;
    private static RayTree? tree;
    private static readonly Stack<int> Pending = new();
    private static readonly HashSet<long> Walked = new(), Replayed = new();
    // Culling thread only.
    private static long cullStarted, replayTicks;
    private static int cullRegen;
    private static Vec3i cullCenter = new();
    private static bool lastBfs, lastWalk;
    private static readonly Dictionary<(int X, int Y, int Z), byte> BfsArrivals = new();
    private static readonly Queue<(int X, int Y, int Z, int Face)> BfsQueue = new();
    private static readonly Dictionary<long, ClientChunk> BfsChunks = new();

    private static System.Reflection.MethodInfo Target() => AccessTools.Method(typeof(ChunkCuller), nameof(ChunkCuller.CullInvisibleChunks), Type.EmptyTypes) ??
        throw new MissingMethodException(typeof(ChunkCuller).FullName, "CullInvisibleChunks");

    private static bool Diagnostics => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VULKANSTORY_RUNTIME_DIAGNOSTICS"));

    internal static PerformanceFeature Create() => new("chunk-visibility-walk", settings => settings.ChunkVisibilityWalk || settings.BfsChunkVisibility, () =>
    {
        _ = Target();
        setVisible = (AccessTools.Method(typeof(ClientChunk), "SetVisible", [typeof(bool)]) ??
            throw new MissingMethodException(typeof(ClientChunk).FullName, "SetVisible")).CreateDelegate<Action<ClientChunk, bool>>();
        swapBuffers = (AccessTools.Method(typeof(ChunkRenderer), "SwapVisibleBuffers", Type.EmptyTypes) ??
            throw new MissingMethodException(typeof(ChunkRenderer).FullName, "SwapVisibleBuffers")).CreateDelegate<Action<ChunkRenderer>>();
        validChunk = (AccessTools.Method(typeof(ClientWorldMap), "IsValidChunkPosFast", [typeof(int), typeof(int), typeof(int)]) ??
            throw new MissingMethodException(typeof(ClientWorldMap).FullName, "IsValidChunkPosFast")).CreateDelegate<Func<ClientWorldMap, int, int, int, bool>>();
        if (BlockFacing.ALLFACES.Length != 6 || BlockFacing.ALLFACES.Where((face, index) => face.Index != index).Any())
            throw new InvalidOperationException("Block face table changed.");
    }, harmony => harmony.Patch(Target(), prefix: new HarmonyMethod(typeof(ChunkVisibilityWalk), nameof(Prefix)),
        postfix: new HarmonyMethod(typeof(ChunkVisibilityWalk), nameof(CountVisible))));

    // Diagnostics gauge for both the original and the walk: chunks visible in the buffer the renderer now reads.
    private static void CountVisible(ChunkCuller __instance)
    {
        if (!Diagnostics) return;
        // A cull ran when the camera chunk or the regeneration count changed; time it without the diagnostics replay.
        if (!Center(__instance).Equals(cullCenter) || RegenCount(__instance) != cullRegen)
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - cullStarted - replayTicks;
            Interlocked.Add(ref PerformanceCounters.CullMicroseconds, elapsed * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);
            Interlocked.Increment(ref PerformanceCounters.CullsTimed);
        }
        int index = ClientChunk.bufIndex, visible = 0;
        try
        {
            // The chunk dictionary changes on other threads; the original's own reset holds this lock too.
            ClientWorldMap map = Game(__instance).WorldMap;
            lock (ChunksLock(map))
                foreach (var chunk in Chunks(map).Values) if (chunk.CullVisible[index]) visible++;
        }
        catch { return; } // Diagnostics must never stop the culling thread; the next cull samples again.
        Interlocked.Exchange(ref PerformanceCounters.VisibleChunks, visible);
    }

    // Culling thread.
    private static bool Prefix(ChunkCuller __instance)
    {
        cullStarted = System.Diagnostics.Stopwatch.GetTimestamp(); replayTicks = 0;
        cullRegen = RegenCount(__instance); cullCenter = Center(__instance).Clone();
        var settings = PerformancePatchSet.Current;
        bool bfs = settings?.BfsChunkVisibility == true, walk = settings?.ChunkVisibilityWalk == true;
        bool modeChanged = bfs != lastBfs || walk != lastWalk;
        lastBfs = bfs; lastWalk = walk;
        if (!bfs && !walk)
        {
            // Force the original to rebuild on a runtime switch even if the camera did not move.
            if (modeChanged) RegenCount(__instance) = int.MinValue / 2;
            return true;
        }
        ClientMain game = Game(__instance);
        ClientWorldMap map = game.WorldMap;
        Dictionary<long, ClientChunk> chunks = Chunks(map);
        // The original handles occlusion culling being off (and switching it back on) itself.
        if (!ClientSettings.Occlusionculling || chunks.Count < 100 || game.player?.Entity == null) return true;
        Vec3i[] shell = ShellPositions(__instance);
        if (shell == null) return true;
        NowOff(__instance) = false;
        Vec3d camera = game.player.Entity.CameraPos;
        Vec3i center = Center(__instance);
        if (!modeChanged && center.Equals((int)camera.X / 32, (int)camera.Y / 32, (int)camera.Z / 32) &&
            Math.Abs(game.chunkPositionsForRegenTrav.Count - RegenCount(__instance)) < 10) return false;
        RegenCount(__instance) = game.chunkPositionsForRegenTrav.Count;
        center.Set((int)(camera.X / 32.0), (int)(camera.Y / 32.0), (int)(camera.Z / 32.0));
        bool above = AboveLimit(__instance) = center.Y >= map.ChunkMapSizeY;
        lock (ChunksLock(map))
        {
            foreach (var chunk in chunks.Values) setVisible!(chunk, false);
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 2; y++)
                    for (int z = -1; z <= 1; z++)
                        if (chunks.TryGetValue(map.ChunkIndex3D(new ChunkPos(x + center.X, y + center.Y, z + center.Z)), out var near))
                            setVisible!(near, true);
        }
        // The shell array is replaced when the view distance changes; rebuild the tree for it.
        if (!bfs && !ReferenceEquals(tree?.Shell, shell)) tree = Build(shell);
        bool diagnostics = Diagnostics;
        int visited = bfs ? WalkBfs(map, chunks, center, above, shell) : WalkTree(tree!, map, chunks, center, above, diagnostics ? Walked : null);
        if (diagnostics && !bfs)
        {
            long replayStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Compare(map, chunks, center, above, shell);
            replayTicks = System.Diagnostics.Stopwatch.GetTimestamp() - replayStarted;
        }
        swapBuffers!(Renderer(game));
        Interlocked.Increment(ref PerformanceCounters.VisibilityWalks);
        Interlocked.Add(ref PerformanceCounters.VisibilityChunksVisited, visited);
        return false;
    }

    /// <summary>Face-state flood: each newly reached incoming face is expanded, including arrivals discovered later.
    /// Unlike the donor's direction mask, paths may turn back; connected passages cannot disappear because of visit order.
    /// Frustum rejection is left to the draw culler so it cannot cut a path to a visible chunk.</summary>
    private static int WalkBfs(ClientWorldMap map, Dictionary<long, ClientChunk> chunks, Vec3i center, bool above, Vec3i[] shell)
    {
        BfsArrivals.Clear(); BfsQueue.Clear(); BfsChunks.Clear();
        lock (ChunksLock(map)) foreach (var pair in chunks) BfsChunks.Add(pair.Key, pair.Value);
        int radiusX = 2, radiusY = 2, radiusZ = 2;
        foreach (var offset in shell)
        {
            radiusX = Math.Max(radiusX, Math.Abs(offset.X) + 2);
            radiusY = Math.Max(radiusY, Math.Abs(offset.Y) + 2);
            radiusZ = Math.Max(radiusZ, Math.Abs(offset.Z) + 2);
        }
        // Unknown/unloaded space must not hide terrain beyond it. Seed exposed faces instead of flooding millions
        // of virtual empty coordinates. Work is bounded by the loaded chunks and at most six arrivals per chunk.
        foreach (long key in BfsChunks.Keys)
        {
            int x = (int)(key % map.index3dMulX);
            long yz = key / map.index3dMulX;
            int z = (int)(yz % map.index3dMulZ), y = (int)(yz / map.index3dMulZ);
            if (!InRange(x, y, z)) continue;
            int distance = Math.Abs(x - center.X) + Math.Abs(y - center.Y) + Math.Abs(z - center.Z);
            if (distance <= 1) Enqueue(x, y, z, -1);
            else
                for (int face = 0; face < 6; face++)
                {
                    var n = BlockFacing.ALLFACES[face].Normali;
                    int nx = x + n.X, ny = y + n.Y, nz = z + n.Z;
                    if (InRange(nx, ny, nz) && !BfsChunks.ContainsKey(Key(map, nx, ny, nz))) Enqueue(x, y, z, face);
                }
        }
        int visited = 0;
        while (BfsQueue.TryDequeue(out var current))
        {
            long key = Key(map, current.X, current.Y, current.Z);
            BfsChunks.TryGetValue(key, out var chunk);
            int distance = Math.Abs(current.X - center.X) + Math.Abs(current.Y - center.Y) + Math.Abs(current.Z - center.Z);
            if (chunk == null) continue;
            setVisible!(chunk, true); visited++;
            for (int exit = 0; exit < 6; exit++)
            {
                var face = BlockFacing.ALLFACES[exit];
                if (chunk != null && distance > 1 && current.Face >= 0 &&
                    !chunk.IsTraversable(BlockFacing.ALLFACES[current.Face], face)) continue;
                var normal = face.Normali;
                int x = current.X + normal.X, y = current.Y + normal.Y, z = current.Z + normal.Z;
                if (InRange(x, y, z) && BfsChunks.ContainsKey(Key(map, x, y, z))) Enqueue(x, y, z, face.Opposite.Index);
            }
        }
        BfsChunks.Clear(); // Do not retain unloaded chunks between walks.
        return visited;
        bool InRange(int x, int y, int z) => Math.Abs(x - center.X) <= radiusX && Math.Abs(y - center.Y) <= radiusY &&
            Math.Abs(z - center.Z) <= radiusZ && (validChunk!(map, x, y, z) || above && y > 0);
        void Enqueue(int x, int y, int z, int incoming)
        {
            var position = (x, y, z);
            byte bit = incoming < 0 ? (byte)63 : (byte)(1 << incoming);
            BfsArrivals.TryGetValue(position, out byte arrivals);
            if ((arrivals & bit) == bit) return;
            BfsArrivals[position] = (byte)(arrivals | bit);
            BfsQueue.Enqueue((x, y, z, incoming));
        }
    }

    /// <summary>Records every ray's steps exactly as the original walks them, relative to the camera chunk, and merges them into a tree.</summary>
    private static RayTree Build(Vec3i[] shell)
    {
        var built = new RayTree { Shell = shell };
        foreach (Vec3i target in shell)
            foreach (var (yOffset, xOffset) in RayOffsets)
            {
                built.Rays++;
                double ox = xOffset, oy = yOffset, oz = 0.5;
                double dx = target.X + xOffset, dy = target.Y + yOffset, dz = target.Z + 0.5;
                int limit = Math.Abs(target.X) + Math.Abs(target.Y) + Math.Abs(target.Z) + 2;
                int x = 0, y = 0, z = 0, entry = -1, node = 0;
                // The original's loop bound; the step cap only guards against a pathological ray.
                for (int step = 0; Math.Abs(x) + Math.Abs(y) + Math.Abs(z) <= limit && step < 4 * (limit + 2); step++)
                {
                    int exit = ExitingFace(x, y, z, ox, oy, oz, dx, dy, dz);
                    if (exit < 0) break;
                    node = built.Child(node, x, y, z, entry, exit);
                    built.Steps++;
                    var normal = BlockFacing.ALLFACES[exit].Normali;
                    x += normal.X; y += normal.Y; z += normal.Z;
                    entry = BlockFacing.ALLFACES[exit].Opposite.Index;
                }
            }
        return built;
    }

    /// <summary>The original's exit-face test (same face order, epsilon and tolerances) for a chunk at a camera-relative position.</summary>
    private static int ExitingFace(int px, int py, int pz, double ox, double oy, double oz, double dx, double dy, double dz)
    {
        for (int index = 0; index < 6; index++)
        {
            BlockFacing face = BlockFacing.ALLFACES[index];
            Vec3i normal = face.Normali;
            double along = normal.X * dx + normal.Y * dy + normal.Z * dz;
            if (along <= 1E-05) continue;
            double planeX = px + face.PlaneCenter.X, planeY = py + face.PlaneCenter.Y, planeZ = pz + face.PlaneCenter.Z;
            double t = ((planeX - ox) * normal.X + (planeY - oy) * normal.Y + (planeZ - oz) * normal.Z) / along;
            if (t >= 0 && Math.Abs(ox + dx * t - planeX) <= 0.5 && Math.Abs(oy + dy * t - planeY) <= 0.5 && Math.Abs(oz + dz * t - planeZ) <= 0.5)
                return index;
        }
        return -1;
    }

    /// <summary>Walks the tree with the original's per-step world tests, pruning where the original stops every ray through a step.</summary>
    private static int WalkTree(RayTree rays, ClientWorldMap map, Dictionary<long, ClientChunk> chunks, Vec3i center, bool above, HashSet<long>? marked)
    {
        BlockFacing[] faces = BlockFacing.ALLFACES;
        Pending.Clear(); marked?.Clear();
        for (int child = rays.FirstChild[0]; child >= 0; child = rays.NextSibling[child]) Pending.Push(child);
        int visited = 0;
        while (Pending.Count > 0)
        {
            int node = Pending.Pop();
            visited++;
            int x = center.X + rays.X[node], y = center.Y + rays.Y[node], z = center.Z + rays.Z[node];
            long key = Key(map, x, y, z);
            chunks.TryGetValue(key, out ClientChunk? chunk);
            int exit = rays.Exit[node];
            if (chunk != null)
            {
                setVisible!(chunk, true);
                marked?.Add(key);
                if (rays.Manhattan[node] > 1 && !chunk.IsTraversable(faces[rays.Entry[node]], faces[exit])) continue;
            }
            Vec3i normal = faces[exit].Normali;
            int nx = x + normal.X, ny = y + normal.Y, nz = z + normal.Z;
            if (!validChunk!(map, nx, ny, nz) && (!above || ny <= 0)) continue;
            for (int child = rays.FirstChild[node]; child >= 0; child = rays.NextSibling[child]) Pending.Push(child);
        }
        return visited;
    }

    /// <summary>Diagnostics only: replays the original rays at the absolute camera position and counts chunks marked by only one side.</summary>
    private static void Compare(ClientWorldMap map, Dictionary<long, ClientChunk> chunks, Vec3i center, bool above, Vec3i[] shell)
    {
        Replayed.Clear();
        BlockFacing[] faces = BlockFacing.ALLFACES;
        foreach (Vec3i target in shell)
            foreach (var (yOffset, xOffset) in RayOffsets)
            {
                double ox = center.X + xOffset, oy = center.Y + yOffset, oz = center.Z + 0.5;
                double dx = target.X + xOffset, dy = target.Y + yOffset, dz = target.Z + 0.5;
                int limit = Math.Abs(target.X) + Math.Abs(target.Y) + Math.Abs(target.Z);
                int x = center.X, y = center.Y, z = center.Z, entry = -1, distance;
                while ((distance = Math.Abs(x - center.X) + Math.Abs(y - center.Y) + Math.Abs(z - center.Z)) <= limit + 2)
                {
                    int exit = ExitingFace(x, y, z, ox, oy, oz, dx, dy, dz);
                    if (exit < 0) break;
                    long key = Key(map, x, y, z);
                    if (chunks.TryGetValue(key, out ClientChunk? chunk))
                    {
                        Replayed.Add(key);
                        if (distance > 1 && !chunk.IsTraversable(entry >= 0 ? faces[entry] : null, faces[exit])) break;
                    }
                    Vec3i normal = faces[exit].Normali;
                    x += normal.X; y += normal.Y; z += normal.Z;
                    entry = faces[exit].Opposite.Index;
                    if (!validChunk!(map, x, y, z) && (!above || y <= 0)) break;
                }
            }
        int differing = Walked.Count(key => !Replayed.Contains(key)) + Replayed.Count(key => !Walked.Contains(key));
        Interlocked.Add(ref PerformanceCounters.VisibilityMismatches, differing);
        Interlocked.Add(ref PerformanceCounters.VisibilityRayChunks, Replayed.Count);
    }

    private static long Key(ClientWorldMap map, int x, int y, int z) => ((long)y * map.index3dMulZ + z) * map.index3dMulX + x;
}
