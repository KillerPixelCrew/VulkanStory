using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Vintagestory.API.Config;

namespace VulkanStory.Game.Performance;

/// <summary>Bounded 8x8-chunk map pages, with coalesced background Brotli writes and atomic cache files.
/// The game's MapDB remains authoritative; dirty events delete both palette variants before stale writes can publish.</summary>
internal sealed class MapPageStore : IDisposable
{
    internal const int Chunks = 8, Size = 256, Capacity = 128;
    private const int Magic = 0x32504d56;
    private sealed class Page
    {
        internal readonly int[] Pixels = new int[Size * Size];
        internal ulong Mask;
        internal long Revision;
        internal bool Dirty;
        internal LinkedListNode<(int X, int Z)>? Node;
    }
    internal readonly record struct Image(long Revision, int[] Pixels);
    private readonly object gate = new(), files = new();
    private readonly Dictionary<(int X, int Z), Page> pages = new();
    private readonly LinkedList<(int X, int Z)> lru = new();
    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource stop = new();
    private readonly string family, directory, database;
    private readonly Task writer;
    private long revision, epoch;
    private volatile bool disposed;
    private volatile bool diskEnabled = true;
    internal MapPageStore(string world, string database, bool accurate)
    {
        this.database = database;
        string identity = world + "|" + GameVersion.ShortGameVersion + "|" + File.GetCreationTimeUtc(database).Ticks;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        family = Path.Combine(GamePaths.DataPath, "ModData", "vulkanstory-map", hash);
        directory = Path.Combine(family, accurate ? "accurate" : "normal");
        writer = Task.Run(WriteLoop);
    }
    internal static (int X, int Z) Key(int x, int z) => (Floor(x), Floor(z));
    private static int Floor(int value)
    { int quotient = Math.DivRem(value, Chunks, out int remainder); return remainder < 0 ? quotient - 1 : quotient; }
    private static int Local(int value) => (value % Chunks + Chunks) % Chunks;
    private string PathFor((int X, int Z) key, string? root = null) => Path.Combine(root ?? directory, key.X + "_" + key.Z + ".vmp");
    private long SourceStamp() => Math.Max(File.GetLastWriteTimeUtc(database).Ticks,
        File.Exists(database + "-wal") ? File.GetLastWriteTimeUtc(database + "-wal").Ticks : 0);

    internal void Put(int chunkX, int chunkZ, int[] pixels)
    {
        if (pixels.Length != 1024 || disposed) return;
        var key = Key(chunkX, chunkZ);
        lock (gate)
        {
            Page page = GetOrAdd(key);
            int x = Local(chunkX) * 32, z = Local(chunkZ) * 32;
            for (int row = 0; row < 32; row++) Array.Copy(pixels, row * 32, page.Pixels, (z + row) * Size + x, 32);
            page.Mask |= 1UL << (Local(chunkZ) * Chunks + Local(chunkX));
            page.Revision = ++revision; page.Dirty = true;
        }
        signal.Writer.TryWrite(true);
    }
    internal bool TryChunk(int chunkX, int chunkZ, out int[] pixels)
    {
        pixels = null!;
        var key = Key(chunkX, chunkZ);
        lock (gate)
        {
            if (!pages.ContainsKey(key))
            {
                Page? loaded = ReadPage(key);
                if (loaded == null) return false;
                Add(key, loaded);
            }
            Page page = pages[key]; Touch(page);
            if ((page.Mask & (1UL << (Local(chunkZ) * Chunks + Local(chunkX)))) == 0) return false;
            pixels = new int[1024];
            int x = Local(chunkX) * 32, z = Local(chunkZ) * 32;
            for (int row = 0; row < 32; row++) Array.Copy(page.Pixels, (z + row) * Size + x, pixels, row * 32, 32);
            return true;
        }
    }
    internal Image? Snapshot((int X, int Z) key, long uploadedRevision)
    {
        lock (gate)
        {
            if (!pages.TryGetValue(key, out var page)) return null;
            Touch(page);
            return new(page.Revision, page.Revision == uploadedRevision ? [] : (int[])page.Pixels.Clone());
        }
    }
    internal void Invalidate(int chunkX, int chunkZ)
    {
        var key = Key(chunkX, chunkZ);
        Interlocked.Increment(ref epoch);
        lock (gate)
            if (pages.TryGetValue(key, out var page))
            {
                int x = Local(chunkX) * 32, z = Local(chunkZ) * 32;
                for (int row = 0; row < 32; row++) Array.Clear(page.Pixels, (z + row) * Size + x, 32);
                page.Mask &= ~(1UL << (Local(chunkZ) * Chunks + Local(chunkX)));
                page.Revision = ++revision; page.Dirty = true;
            }
        lock (files)
            foreach (string profile in new[] { "normal", "accurate" })
                try { File.Delete(PathFor(key, Path.Combine(family, profile))); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DiskFailure(error); }
        signal.Writer.TryWrite(true);
    }
    internal void Purge()
    {
        Interlocked.Increment(ref epoch);
        lock (gate) { pages.Clear(); lru.Clear(); }
        lock (files)
            foreach (string profile in new[] { "normal", "accurate" })
            {
                string path = Path.Combine(family, profile);
                if (!Directory.Exists(path)) continue;
                try { foreach (string file in Directory.EnumerateFiles(path, "*.vmp")) File.Delete(file); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DiskFailure(error); }
            }
    }
    private Page GetOrAdd((int X, int Z) key)
    {
        if (pages.TryGetValue(key, out var page)) { Touch(page); return page; }
        page = ReadPage(key) ?? new Page(); Add(key, page); return page;
    }
    private void Add((int X, int Z) key, Page page)
    {
        if (pages.Count == Capacity && lru.First is { } oldest) { pages.Remove(oldest.Value); lru.RemoveFirst(); }
        page.Revision = ++revision; page.Node = lru.AddLast(key); pages.Add(key, page);
    }
    private void Touch(Page page) { if (page.Node != null) { lru.Remove(page.Node); lru.AddLast(page.Node); } }
    private Page? ReadPage((int X, int Z) key)
    {
        if (!diskEnabled) return null;
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path) || new FileInfo(path).Length > Size * Size * 8) return null;
            using var file = File.OpenRead(path);
            using var header = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
            if (header.ReadInt32() != Magic || header.ReadInt32() != key.X || header.ReadInt32() != key.Z) return null;
            ulong mask = header.ReadUInt64();
            if (header.ReadInt64() != SourceStamp()) return null;
            using var compressed = new BrotliStream(file, CompressionMode.Decompress);
            byte[] raw = new byte[Size * Size * 4]; compressed.ReadExactly(raw);
            if (compressed.ReadByte() != -1) return null;
            var page = new Page { Mask = mask }; Buffer.BlockCopy(raw, 0, page.Pixels, 0, raw.Length);
            return page;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }
    private async Task WriteLoop()
    {
        try
        {
            while (await signal.Reader.WaitToReadAsync(stop.Token))
            {
                while (signal.Reader.TryRead(out _)) { }
                await Task.Delay(100, stop.Token); // Coalesce chunk updates into one write per changed page.
                while (true)
                {
                    ((int X, int Z) Key, ulong Mask, int[] Pixels, long Epoch)? work = null;
                    lock (gate)
                        foreach (var entry in pages)
                            if (entry.Value.Dirty)
                            {
                                entry.Value.Dirty = false;
                                work = (entry.Key, entry.Value.Mask, (int[])entry.Value.Pixels.Clone(), Volatile.Read(ref epoch));
                                break;
                            }
                    if (work is not { } item) break;
                    if (!diskEnabled) continue;
                    string? temporary = null;
                    try
                    {
                        Directory.CreateDirectory(directory);
                        temporary = PathFor(item.Key) + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            using (var header = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
                            { header.Write(Magic); header.Write(item.Key.X); header.Write(item.Key.Z); header.Write(item.Mask); header.Write(SourceStamp()); }
                            await using var compressed = new BrotliStream(file, CompressionLevel.Fastest, leaveOpen: true);
                            byte[] bytes = new byte[item.Pixels.Length * 4]; Buffer.BlockCopy(item.Pixels, 0, bytes, 0, bytes.Length);
                            await compressed.WriteAsync(bytes, stop.Token);
                        }
                        lock (files)
                            if (!disposed && item.Epoch == Volatile.Read(ref epoch)) File.Move(temporary, PathFor(item.Key), overwrite: true);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DiskFailure(error); }
                    finally
                    {
                        if (temporary != null) try { File.Delete(temporary); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DiskFailure(error); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void DiskFailure(Exception error)
    {
        if (!diskEnabled) return;
        diskEnabled = false;
        Console.Error.WriteLine("VulkanStory: map disk cache disabled for this session: " + error.Message);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Interlocked.Increment(ref epoch); stop.Cancel(); signal.Writer.TryComplete();
        // Compression/I/O owns only snapshots. It never touches GPU resources or the game after cancellation.
    }
}
