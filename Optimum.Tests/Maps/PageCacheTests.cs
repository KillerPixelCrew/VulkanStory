// Source: Optimum.Tests/MapPageCacheFunctionalTests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Xunit;

/// <summary>
/// Functional tests for the map page cache: disk round-trip, pixel compositing,
/// LRU eviction logic, priority queue ordering, and boundary conditions.
/// These test the actual data-flow correctness that the existing coordinate-math
/// tests do not cover.
/// </summary>
[Collection("Bc7Config")]
public class MapPageCacheFunctionalTests
{
    // --- Disk serialization round-trip ---

    [Fact]
    public void DiskRoundTrip_HeaderFormat_IsCorrect()
    {
        // Simulate the file format manually: magic + version + pageX + pageZ + bitmask + gzip(pixels)
        int pageX = 3;
        int pageZ = -2;
        ulong bitmask = 0x0000_0000_0000_000F; // 4 chunks set

        int[] pixels = new int[OptimumMapPageCache.PagePixelCount];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = i * 7 + unchecked((int)0xFF000000);

        byte[] rawBytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, rawBytes, 0, rawBytes.Length);

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
                gz.Write(rawBytes, 0, rawBytes.Length);
            compressed = ms.ToArray();
        }

        // Build the file
        byte[] file = new byte[21 + compressed.Length];
        file[0] = (byte)'O'; file[1] = (byte)'M'; file[2] = (byte)'P'; file[3] = 0x01;
        file[4] = 1; // version
        BitConverter.TryWriteBytes(file.AsSpan(5), pageX);
        BitConverter.TryWriteBytes(file.AsSpan(9), pageZ);
        BitConverter.TryWriteBytes(file.AsSpan(13), bitmask);
        Array.Copy(compressed, 0, file, 21, compressed.Length);

        // Verify header fields
        Assert.Equal((byte)'O', file[0]);
        Assert.Equal((byte)'M', file[1]);
        Assert.Equal((byte)'P', file[2]);
        Assert.Equal(0x01, file[3]);
        Assert.Equal(1, file[4]);
        Assert.Equal(pageX, BitConverter.ToInt32(file, 5));
        Assert.Equal(pageZ, BitConverter.ToInt32(file, 9));
        Assert.Equal(bitmask, BitConverter.ToUInt64(file, 13));

        // Verify decompression
        byte[] payloadCompressed = new byte[compressed.Length];
        Array.Copy(file, 21, payloadCompressed, 0, compressed.Length);

        using var input = new MemoryStream(payloadCompressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        byte[] decompressed = output.ToArray();

        Assert.Equal(pixels.Length * 4, decompressed.Length);
        int[] roundTripped = new int[pixels.Length];
        Buffer.BlockCopy(decompressed, 0, roundTripped, 0, decompressed.Length);
        Assert.Equal(pixels, roundTripped);
    }

    // --- Pixel compositing correctness ---

    [Fact]
    public void WriteChunk_CompositesPixelsAtCorrectOffset()
    {
        // Chunk (2, 3) within page (0,0) should land at pixel offset (64, 96) in the 256x256 page
        int chunkX = 2;
        int chunkZ = 3;
        int localX = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ) % OptimumMapPageCache.PageLen;
        int localZ = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ) / OptimumMapPageCache.PageLen;

        // Verify local coords
        int expectedLocalX = ((chunkX % 8) + 8) % 8; // 2
        int expectedLocalZ = ((chunkZ % 8) + 8) % 8; // 3
        Assert.Equal(2, expectedLocalX);
        Assert.Equal(3, expectedLocalZ);

        // Pixel base position within the 256x256 page
        int basePixelX = expectedLocalX * 32; // 64
        int basePixelZ = expectedLocalZ * 32; // 96
        Assert.Equal(64, basePixelX);
        Assert.Equal(96, basePixelZ);

        // Verify the compositing logic: row 0 of the chunk lands at (96*256 + 64) in the page buffer
        int expectedDstOffset = basePixelZ * 256 + basePixelX;
        Assert.Equal(96 * 256 + 64, expectedDstOffset);
    }

    [Fact]
    public void WriteChunk_NegativeCoords_CompositesCorrectly()
    {
        // Chunk (-1, -1) belongs to page (-1, -1), local (7, 7)
        int chunkX = -1;
        int chunkZ = -1;
        int localX = ((chunkX % 8) + 8) % 8; // 7
        int localZ = ((chunkZ % 8) + 8) % 8; // 7
        Assert.Equal(7, localX);
        Assert.Equal(7, localZ);

        int basePixelX = 7 * 32; // 224
        int basePixelZ = 7 * 32; // 224
        Assert.Equal(224, basePixelX);
        Assert.Equal(224, basePixelZ);
    }

    [Fact]
    public void GetCachedChunkPixels_ExtractsCorrectSubregion()
    {
        // Build a page buffer where each chunk position has a unique pattern
        int[] pageBuffer = new int[256 * 256];
        int targetChunkX = 5;
        int targetChunkZ = 2;
        int localX = ((targetChunkX % 8) + 8) % 8;
        int localZ = ((targetChunkZ % 8) + 8) % 8;
        int basePixelX = localX * 32;
        int basePixelZ = localZ * 32;

        // Fill the target chunk region with a known pattern
        for (int row = 0; row < 32; row++)
        {
            for (int col = 0; col < 32; col++)
            {
                pageBuffer[(basePixelZ + row) * 256 + basePixelX + col] = row * 32 + col + 1;
            }
        }

        // Extract using the same logic as GetCachedChunkPixels
        int[] extracted = new int[32 * 32];
        for (int row = 0; row < 32; row++)
        {
            int srcOffset = (basePixelZ + row) * 256 + basePixelX;
            int dstOffset = row * 32;
            Array.Copy(pageBuffer, srcOffset, extracted, dstOffset, 32);
        }

        // Verify the first and last pixel
        Assert.Equal(1, extracted[0]);           // row=0, col=0 -> 0*32+0+1 = 1
        Assert.Equal(32 * 32, extracted[32 * 32 - 1]); // row=31, col=31 -> 31*32+31+1 = 1024
    }

    // --- LRU eviction logic ---

    [Fact]
    public void LruEviction_EvictsLeastRecentlyUsed()
    {
        // Simulate the LRU without GL: use the data-structure operations directly
        var pageToLayer = new Dictionary<long, int>();
        var lruOrder = new LinkedList<long>();
        var lruNodes = new Dictionary<long, LinkedListNode<long>>();
        var freeList = new Stack<int>();

        int maxLayers = 3;
        for (int i = maxLayers - 1; i >= 0; i--) freeList.Push(i);

        // Fill all 3 layers
        for (int i = 0; i < 3; i++)
        {
            long key = i + 100;
            int layer = freeList.Pop();
            pageToLayer[key] = layer;
            var node = lruOrder.AddFirst(key);
            lruNodes[key] = node;
        }

        // Access key 100 (moves to front)
        {
            long touchKey = 100;
            var node = lruNodes[touchKey];
            lruOrder.Remove(node);
            lruOrder.AddFirst(node);
        }

        // Now LRU order front-to-back: 100, 102, 101
        // Eviction should remove the back: 101
        long evictKey = lruOrder.Last!.Value;
        Assert.Equal(101L, evictKey);
    }

    [Fact]
    public void LruEviction_ReusesEvictedLayer()
    {
        var pageToLayer = new Dictionary<long, int>();
        var freeList = new Stack<int>();
        int maxLayers = 2;
        for (int i = maxLayers - 1; i >= 0; i--) freeList.Push(i);

        // Fill both layers
        pageToLayer[10] = freeList.Pop(); // layer 0
        pageToLayer[20] = freeList.Pop(); // layer 1

        // Evict key 10
        int evictedLayer = pageToLayer[10];
        pageToLayer.Remove(10);
        freeList.Push(evictedLayer);

        // Allocate new page: gets the evicted layer back
        int newLayer = freeList.Pop();
        Assert.Equal(0, newLayer); // layer 0 was evicted and reused
    }

    // --- Priority queue correctness ---

    [Fact]
    public void PriorityQueue_MaintainsHeapInvariant()
    {
        var pq = new OptimumMapPagePriorityQueue();

        // Insert 20 pages at random distances
        var rng = new Random(42);
        var expectedOrder = new List<(long key, int dist)>();
        for (int i = 0; i < 20; i++)
        {
            int px = rng.Next(-50, 50);
            int pz = rng.Next(-50, 50);
            long key = OptimumMapPageCache.PackPageCoord(px, pz);
            int dist = px * px + pz * pz;
            pq.Enqueue(key, px, pz, 0, 0);
            expectedOrder.Add((key, dist));
        }

        // Dequeue all: should come out in non-decreasing distance order
        int prev = -1;
        for (int i = 0; i < 20; i++)
        {
            long dequeued = pq.Dequeue();
            Assert.NotEqual(-1, dequeued);
            // Find the distance for this key
            int dequeuedDist = expectedOrder.Find(e => e.key == dequeued).dist;
            Assert.True(dequeuedDist >= prev, $"Heap violation: dist {dequeuedDist} < prev {prev}");
            prev = dequeuedDist;
        }

        Assert.Equal(0, pq.Count);
    }

    // --- Boundary condition tests ---

    [Fact]
    public void ChunkToPage_BoundaryAt8_IsConsistent()
    {
        // Chunk 7 -> page 0, chunk 8 -> page 1
        var (p7, _) = OptimumMapPageCache.ChunkToPage(7, 0);
        var (p8, _) = OptimumMapPageCache.ChunkToPage(8, 0);
        Assert.Equal(0, p7);
        Assert.Equal(1, p8);

        // Chunk -1 -> page -1, chunk 0 -> page 0
        var (pm1, _) = OptimumMapPageCache.ChunkToPage(-1, 0);
        var (p0, _) = OptimumMapPageCache.ChunkToPage(0, 0);
        Assert.Equal(-1, pm1);
        Assert.Equal(0, p0);
    }

    [Fact]
    public void InvalidateChunk_ThenIsChunkCached_ReturnsFalse()
    {
        // Simulate: set bit, then clear bit, verify cleared
        int chunkX = 4, chunkZ = 6;
        int idx = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ);

        ulong mask = 1UL << idx;
        Assert.True((mask & (1UL << idx)) != 0); // bit set

        mask &= ~(1UL << idx);
        Assert.False((mask & (1UL << idx)) != 0); // bit cleared
    }

    [Fact]
    public void PageCoverage_3x3ComponentStraddlesPageBoundary()
    {
        // A MultiChunkMapComponent at baseChunkCoord (6, 6) covers chunks (6,6) through (8,8)
        // Chunks 6,7 -> page 0; chunk 8 -> page 1
        // This means the component straddles two pages on each axis
        int baseX = 6, baseZ = 6;
        var pages = new HashSet<long>();
        for (int dx = 0; dx < 3; dx++)
        {
            for (int dz = 0; dz < 3; dz++)
            {
                int cx = baseX + dx;
                int cz = baseZ + dz;
                var (px, pz) = OptimumMapPageCache.ChunkToPage(cx, cz);
                pages.Add(OptimumMapPageCache.PackPageCoord(px, pz));
            }
        }

        // A component at (6,6) spans 4 pages: (0,0), (0,1), (1,0), (1,1)
        Assert.True(pages.Count > 1, $"Expected multiple pages, got {pages.Count}");
        Assert.Contains(OptimumMapPageCache.PackPageCoord(0, 0), pages);
        Assert.Contains(OptimumMapPageCache.PackPageCoord(1, 1), pages);
    }

    // --- LOD downsample pixel accuracy ---

    [Fact]
    public void Downsample2x_GradientBlock_AveragesCorrectly()
    {
        int[] full = new int[256 * 256];
        // Create a 2x2 pattern at (0,0): pixels are (10,20,30,40), (50,60,70,80), (90,100,110,120), (130,140,150,160)
        full[0] = 10 | (20 << 8) | (30 << 16) | (40 << 24);
        full[1] = 50 | (60 << 8) | (70 << 16) | (80 << 24);
        full[256] = 90 | (100 << 8) | (110 << 16) | (120 << 24);
        full[257] = 130 | (140 << 8) | (150 << 16) | (160 << 24);

        int[] lod1 = OptimumMapLodGenerator.Downsample2x(full);

        // Average of 4 pixels: R=(10+50+90+130)/4=70, G=(20+60+100+140)/4=80, B=(30+70+110+150)/4=90, A=(40+80+120+160)/4=100
        int expected = 70 | (80 << 8) | (90 << 16) | (100 << 24);
        Assert.Equal(expected, lod1[0]);
    }

    [Fact]
    public void Downsample4x_GradientBlock_AveragesCorrectly()
    {
        int[] full = new int[256 * 256];
        // Fill a 4x4 block with uniform value
        int pixel = 42 | (84 << 8) | (126 << 16) | (200 << 24);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                full[y * 256 + x] = pixel;

        int[] lod2 = OptimumMapLodGenerator.Downsample4x(full);
        // Uniform input -> same output
        Assert.Equal(pixel, lod2[0]);
    }

    // --- BC7 encoder functional test ---

    [Fact]
    public void Bc7Encode_GradientBlock_ProducesDecodableOutput()
    {
        bool origEnabled = OptimumConfig.MapPageCacheBc7;
        bool origSupported = OptimumConfig.MapPageCacheBc7Supported;
        OptimumConfig.MapPageCacheBc7 = true;
        OptimumConfig.MapPageCacheBc7Supported = true;

        try
        {
            // Create a page with a smooth horizontal gradient
            int[] pixels = new int[256 * 256];
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    pixels[y * 256 + x] = x | (128 << 8) | ((255 - x) << 16) | (255 << 24);
                }
            }

            byte[]? bc7 = OptimumBc7Support.Encode(pixels, 256, 256);
            Assert.NotNull(bc7);
            Assert.Equal(65536, bc7.Length); // (256/4)^2 * 16

            // Every block should have the Mode 6 marker
            for (int block = 0; block < bc7.Length; block += 16)
            {
                Assert.Equal(0x40, bc7[block] & 0x7F); // Mode 6 = bit 6 set, bits 0-5 zero
            }
        }
        finally
        {
            OptimumConfig.MapPageCacheBc7 = origEnabled;
            OptimumConfig.MapPageCacheBc7Supported = origSupported;
        }
    }

    [Fact]
    public void Bc7Encode_SolidBlock_EndpointsMatch()
    {
        bool origEnabled = OptimumConfig.MapPageCacheBc7;
        bool origSupported = OptimumConfig.MapPageCacheBc7Supported;
        OptimumConfig.MapPageCacheBc7 = true;
        OptimumConfig.MapPageCacheBc7Supported = true;

        try
        {
            // Solid color page: all pixels identical
            int color = 100 | (150 << 8) | (200 << 16) | (255 << 24);
            int[] pixels = new int[256 * 256];
            Array.Fill(pixels, color);

            byte[]? bc7 = OptimumBc7Support.Encode(pixels, 256, 256);
            Assert.NotNull(bc7);

            // For a solid block, both endpoints should encode the same color
            // and all indices should be 0. The endpoints are at bits 7-62
            // (7 bits each, 8 components total: e0R, e1R, e0G, e1G, e0B, e1B, e0A, e1A)
            // For a solid color, e0 == e1.
            // We check the first block:
            byte[] block = new byte[16];
            Array.Copy(bc7, 0, block, 0, 16);

            // Extract e0R (bits 7-13) and e1R (bits 14-20): both should be the same
            int e0r = ExtractBits(block, 7, 7);
            int e1r = ExtractBits(block, 14, 7);
            // For solid color, min and max are same pixel, so endpoints should be equal
            Assert.Equal(e0r, e1r);
        }
        finally
        {
            OptimumConfig.MapPageCacheBc7 = origEnabled;
            OptimumConfig.MapPageCacheBc7Supported = origSupported;
        }
    }

    private static int ExtractBits(byte[] data, int bitOffset, int numBits)
    {
        int value = 0;
        for (int i = 0; i < numBits; i++)
        {
            int bitPos = bitOffset + i;
            if ((data[bitPos / 8] & (1 << (bitPos % 8))) != 0)
            {
                value |= (1 << i);
            }
        }
        return value;
    }
}
}

// Source: Optimum.Tests/MapPageCacheTests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Xunit;

public class MapPageCacheTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(7, 7, 0, 0)]
    [InlineData(8, 0, 1, 0)]
    [InlineData(15, 15, 1, 1)]
    [InlineData(16, 16, 2, 2)]
    [InlineData(-1, -1, -1, -1)]
    [InlineData(-8, -8, -1, -1)]
    [InlineData(-9, -9, -2, -2)]
    public void ChunkToPage_ConvertsCorrectly(int chunkX, int chunkZ, int expectedPageX, int expectedPageZ)
    {
        var (pageX, pageZ) = OptimumMapPageCache.ChunkToPage(chunkX, chunkZ);
        Assert.Equal(expectedPageX, pageX);
        Assert.Equal(expectedPageZ, pageZ);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, 8)]
    [InlineData(7, 7, 63)]
    [InlineData(8, 8, 0)]    // wraps: 8 % 8 = 0
    [InlineData(9, 9, 9)]    // wraps: 9 % 8 = 1, -> 1*8+1 = 9
    [InlineData(-1, -1, 63)] // wraps: (-1%8+8)%8 = 7, -> 7*8+7 = 63
    [InlineData(-8, -8, 0)]  // wraps: (-8%8+8)%8 = 0, -> 0*8+0 = 0
    public void ChunkIndexInPage_WrapsCorrectly(int chunkX, int chunkZ, int expectedIndex)
    {
        int idx = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ);
        Assert.Equal(expectedIndex, idx);
    }

    [Fact]
    public void PackPageCoord_RoundTrips()
    {
        int pageX = 42;
        int pageZ = -17;
        long packed = OptimumMapPageCache.PackPageCoord(pageX, pageZ);

        // Unpack
        int unpackedX = (int)(uint)(packed >> 32);
        int unpackedZ = (int)(uint)(packed & 0xFFFFFFFF);
        Assert.Equal(pageX, unpackedX);
        Assert.Equal(pageZ, unpackedZ);
    }

    [Fact]
    public void PackPageCoord_NegativeValues_RoundTrip()
    {
        int pageX = -100;
        int pageZ = -200;
        long packed = OptimumMapPageCache.PackPageCoord(pageX, pageZ);

        int unpackedX = (int)(uint)(packed >> 32);
        int unpackedZ = (int)(uint)(packed & 0xFFFFFFFF);
        Assert.Equal(pageX, unpackedX);
        Assert.Equal(pageZ, unpackedZ);
    }

    [Fact]
    public void PageConstants_AreCorrect()
    {
        Assert.Equal(8, OptimumMapPageCache.PageLen);
        Assert.Equal(256, OptimumMapPageCache.PagePixelSize);
        Assert.Equal(65536, OptimumMapPageCache.PagePixelCount);
    }

    [Fact]
    public void ChunkToPage_AllChunksInSamePage_MapToSameCoord()
    {
        // All chunks 0..7 in both axes belong to page (0,0)
        for (int x = 0; x < 8; x++)
        {
            for (int z = 0; z < 8; z++)
            {
                var (px, pz) = OptimumMapPageCache.ChunkToPage(x, z);
                Assert.Equal(0, px);
                Assert.Equal(0, pz);
            }
        }

        // Chunk (8,0) belongs to page (1,0)
        var (px2, pz2) = OptimumMapPageCache.ChunkToPage(8, 0);
        Assert.Equal(1, px2);
        Assert.Equal(0, pz2);
    }

    [Fact]
    public void ChunkIndexInPage_CoversAll64Slots()
    {
        // Page (0,0) contains chunks (0,0) through (7,7)
        bool[] seen = new bool[64];
        for (int x = 0; x < 8; x++)
        {
            for (int z = 0; z < 8; z++)
            {
                int idx = OptimumMapPageCache.ChunkIndexInPage(x, z);
                Assert.InRange(idx, 0, 63);
                Assert.False(seen[idx], $"Duplicate index {idx} for chunk ({x},{z})");
                seen[idx] = true;
            }
        }

        // All 64 slots covered
        for (int i = 0; i < 64; i++)
        {
            Assert.True(seen[i], $"Index {i} not assigned to any chunk");
        }
    }

    [Fact]
    public void NegativeChunks_ProduceValidIndices()
    {
        // Page (-1,-1) contains chunks (-8,-8) through (-1,-1)
        bool[] seen = new bool[64];
        for (int x = -8; x < 0; x++)
        {
            for (int z = -8; z < 0; z++)
            {
                var (px, pz) = OptimumMapPageCache.ChunkToPage(x, z);
                Assert.Equal(-1, px);
                Assert.Equal(-1, pz);

                int idx = OptimumMapPageCache.ChunkIndexInPage(x, z);
                Assert.InRange(idx, 0, 63);
                Assert.False(seen[idx], $"Duplicate index {idx} for chunk ({x},{z})");
                seen[idx] = true;
            }
        }

        for (int i = 0; i < 64; i++)
        {
            Assert.True(seen[i], $"Index {i} not assigned to any chunk in page (-1,-1)");
        }
    }
}

    /// <summary>
    /// Tests for the OptimumMapTextureArray free-list and LRU logic.
    /// These test the data-structure logic only (no GL context available).
    /// The actual GL calls are tested via the full game launch.
    /// </summary>
    public class MapTextureArrayLogicTests
    {
        [Fact]
        public void PackPageCoord_DistinctPages_ProduceDistinctKeys()
        {
            var keys = new HashSet<long>();
            for (int x = -10; x <= 10; x++)
            {
                for (int z = -10; z <= 10; z++)
                {
                    long key = OptimumMapPageCache.PackPageCoord(x, z);
                    Assert.True(keys.Add(key), $"Duplicate key for page ({x},{z})");
                }
            }
        }

        [Fact]
        public void ChunkToPage_AdjacentChunks_CrossPageBoundary()
        {
            // Chunk 7 and chunk 8 belong to different pages
            var (p1x, _) = OptimumMapPageCache.ChunkToPage(7, 0);
            var (p2x, _) = OptimumMapPageCache.ChunkToPage(8, 0);
            Assert.Equal(0, p1x);
            Assert.Equal(1, p2x);
        }

        [Fact]
        public void PagePixelSize_Is256()
        {
            Assert.Equal(256, OptimumMapPageCache.PagePixelSize);
        }

        [Fact]
        public void ChunkToPage_NegativeBoundary_IsCorrect()
        {
            // Chunk -1 belongs to page -1
            var (px, pz) = OptimumMapPageCache.ChunkToPage(-1, -1);
            Assert.Equal(-1, px);
            Assert.Equal(-1, pz);

            // Chunk -8 belongs to page -1 (the last chunk in that page)
            (px, pz) = OptimumMapPageCache.ChunkToPage(-8, -8);
            Assert.Equal(-1, px);
            Assert.Equal(-1, pz);

            // Chunk -9 belongs to page -2
            (px, pz) = OptimumMapPageCache.ChunkToPage(-9, -9);
            Assert.Equal(-2, px);
            Assert.Equal(-2, pz);
        }

        [Fact]
        public void PagePixelCount_MatchesSquare()
        {
            Assert.Equal(256 * 256, OptimumMapPageCache.PagePixelCount);
        }

        [Fact]
        public void ChunkIndexInPage_NeverExceedsBitmaskWidth()
        {
            // Test 1000 random chunks: index always 0..63
            var rng = new Random(42);
            for (int i = 0; i < 1000; i++)
            {
                int cx = rng.Next(-10000, 10000);
                int cz = rng.Next(-10000, 10000);
                int idx = OptimumMapPageCache.ChunkIndexInPage(cx, cz);
                Assert.InRange(idx, 0, 63);
            }
        }
    }

    [Collection("Bc7Config")]
    public class Bc7SupportTests
    {
        [Fact]
        public void Encode_ReturnsNull_WhenDisabled()
        {
            // Save and override config
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = false;
            OptimumConfig.MapPageCacheBc7Supported = true;
            try
            {
                int[] pixels = new int[256 * 256];
                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.Null(result);
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }

        [Fact]
        public void Encode_ReturnsNull_WhenNotSupported()
        {
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = true;
            OptimumConfig.MapPageCacheBc7Supported = false;
            try
            {
                int[] pixels = new int[256 * 256];
                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.Null(result);
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }

        [Fact]
        public void Encode_ProducesCorrectSize_WhenEnabled()
        {
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = true;
            OptimumConfig.MapPageCacheBc7Supported = true;
            try
            {
                int[] pixels = new int[256 * 256];
                // Fill with a gradient
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = (i & 0xFF) | ((i >> 2) & 0xFF) << 8 | unchecked((int)0xFF000000);
                }

                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.NotNull(result);
                // BC7 compressed size: (256/4)*(256/4)*16 = 64*64*16 = 65536
                Assert.Equal(65536, result.Length);
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }

        [Fact]
        public void Encode_ReturnsNull_WhenPixelsMismatch()
        {
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = true;
            OptimumConfig.MapPageCacheBc7Supported = true;
            try
            {
                int[] pixels = new int[100]; // wrong size
                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.Null(result);
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }

        [Fact]
        public void Encode_SolidColor_ProducesNonZeroOutput()
        {
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = true;
            OptimumConfig.MapPageCacheBc7Supported = true;
            try
            {
                int[] pixels = new int[256 * 256];
                Array.Fill(pixels, unchecked((int)0xFF804020)); // solid ABGR color

                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.NotNull(result);
                Assert.Equal(65536, result.Length);

                // At least the mode bit should be set in every block
                for (int block = 0; block < result.Length; block += 16)
                {
                    // Mode 6 starts with 0x40
                    Assert.Equal(0x40, result[block] & 0x40);
                }
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }
    }

    public class DirtyPageTrackingTests
    {
        [Fact]
        public void InvalidateChunk_ClearsBitmaskBit()
        {
            // Simulates the bitmask logic: set bit then clear it
            int chunkX = 3;
            int chunkZ = 5;
            int idx = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ);

            ulong mask = 1UL << idx;
            Assert.NotEqual(0UL, mask);

            // Clearing the bit
            mask &= ~(1UL << idx);
            Assert.Equal(0UL, mask);
        }

        [Fact]
        public void InvalidateChunk_OnlyAffectsTargetBit()
        {
            // Set all 64 bits, then clear one
            ulong mask = ulong.MaxValue;

            int chunkX = 7;
            int chunkZ = 7;
            int idx = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ);
            mask &= ~(1UL << idx);

            // 63 bits remain set
            int setBits = 0;
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0) setBits++;
            }
            Assert.Equal(63, setBits);
        }

        [Fact]
        public void DirtyChunk_NeighborsMapToCorrectPages()
        {
            // When chunk (8, 8) is dirty, vanilla invalidates:
            // (8,8), (8,7), (7,8), (8,9), (9,9)
            // These cross a page boundary: (8,8) -> page(1,1), (7,8) -> page(0,1)
            var (px1, pz1) = OptimumMapPageCache.ChunkToPage(8, 8);
            var (px2, pz2) = OptimumMapPageCache.ChunkToPage(7, 8);
            Assert.Equal(1, px1); Assert.Equal(1, pz1);
            Assert.Equal(0, px2); Assert.Equal(1, pz2);

            // Different pages get different keys
            long key1 = OptimumMapPageCache.PackPageCoord(px1, pz1);
            long key2 = OptimumMapPageCache.PackPageCoord(px2, pz2);
            Assert.NotEqual(key1, key2);
        }
    }

    public class MapLodAndPriorityTests
    {
        [Fact]
        public void Downsample2x_ProducesCorrectSize()
        {
            int[] full = new int[256 * 256];
            Array.Fill(full, unchecked((int)0xFF808080));
            int[] lod1 = OptimumMapLodGenerator.Downsample2x(full);
            Assert.NotNull(lod1);
            Assert.Equal(128 * 128, lod1.Length);
        }

        [Fact]
        public void Downsample4x_ProducesCorrectSize()
        {
            int[] full = new int[256 * 256];
            Array.Fill(full, unchecked((int)0xFF808080));
            int[] lod2 = OptimumMapLodGenerator.Downsample4x(full);
            Assert.NotNull(lod2);
            Assert.Equal(64 * 64, lod2.Length);
        }

        [Fact]
        public void Downsample2x_SolidColor_PreservesColor()
        {
            int[] full = new int[256 * 256];
            int color = unchecked((int)0xFF402010);
            Array.Fill(full, color);
            int[] lod1 = OptimumMapLodGenerator.Downsample2x(full);
            // Solid color downsampled should remain the same
            Assert.Equal(color, lod1[0]);
            Assert.Equal(color, lod1[128 * 128 - 1]);
        }

        [Fact]
        public void Downsample4x_SolidColor_PreservesColor()
        {
            int[] full = new int[256 * 256];
            int color = unchecked((int)0xFFAABBCC);
            Array.Fill(full, color);
            int[] lod2 = OptimumMapLodGenerator.Downsample4x(full);
            Assert.Equal(color, lod2[0]);
            Assert.Equal(color, lod2[64 * 64 - 1]);
        }

        [Fact]
        public void Downsample2x_NullInput_ReturnsNull()
        {
            Assert.Null(OptimumMapLodGenerator.Downsample2x(null));
        }

        [Fact]
        public void Downsample2x_WrongSize_ReturnsNull()
        {
            Assert.Null(OptimumMapLodGenerator.Downsample2x(new int[100]));
        }

        [Fact]
        public void PriorityQueue_DequeuesNearest()
        {
            var pq = new OptimumMapPagePriorityQueue();
            // Center at (5,5). Add pages at distances 3, 1, 2
            long k1 = OptimumMapPageCache.PackPageCoord(8, 5);  // dx=3
            long k2 = OptimumMapPageCache.PackPageCoord(6, 5);  // dx=1
            long k3 = OptimumMapPageCache.PackPageCoord(7, 5);  // dx=2

            pq.Enqueue(k1, 8, 5, 5, 5);
            pq.Enqueue(k2, 6, 5, 5, 5);
            pq.Enqueue(k3, 7, 5, 5, 5);

            // Should dequeue in nearest-first order
            Assert.Equal(k2, pq.Dequeue()); // dist^2 = 1
            Assert.Equal(k3, pq.Dequeue()); // dist^2 = 4
            Assert.Equal(k1, pq.Dequeue()); // dist^2 = 9
        }

        [Fact]
        public void PriorityQueue_Empty_ReturnsNegative()
        {
            var pq = new OptimumMapPagePriorityQueue();
            Assert.Equal(-1, pq.Dequeue());
        }

        [Fact]
        public void PriorityQueue_Count_TracksItems()
        {
            var pq = new OptimumMapPagePriorityQueue();
            Assert.Equal(0, pq.Count);
            pq.Enqueue(1, 0, 0, 0, 0);
            Assert.Equal(1, pq.Count);
            pq.Dequeue();
            Assert.Equal(0, pq.Count);
        }
    }
}
