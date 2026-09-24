// Source: Optimum.Render.Vulkan.Tests/AllocatorPolicyTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

using Buffer = Silk.NET.Vulkan.Buffer;

/// <summary>
/// Phase 1B step 5: pool classes and budget. A static mesh lands in device-local
/// memory that is not host visible (when the device has such a type) and never in
/// ReBAR; the driver's dedicated requirement is honoured; the ReBAR class is capped
/// and a miss is a counted, logged fall-through that still renders; empty blocks
/// survive 120 frames (or go at once under budget pressure); the heap report
/// carries used/budget per heap.
/// </summary>
public class AllocatorPolicyTests
{
    private const ulong MiB = 1024UL * 1024;

    private readonly ITestOutputHelper _output;

    public AllocatorPolicyTests(ITestOutputHelper output) => _output = output;

    private static int CreateTarget(IOptimumGraphicsDevice seam, int size)
    {
        int texture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 1);
        return framebuffer;
    }

    private static unsafe byte[] Read(IOptimumGraphicsDevice seam, int framebuffer, int size)
    {
        var pixels = new byte[size * size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
        }
        return pixels;
    }

    /// <summary>A quad from x = -1 to <paramref name="right" />, full height.</summary>
    private static MeshData Quad(float right) => new(4, 6)
    {
        xyz = new[] { -1f, -1f, 0f, right, -1f, 0f, right, 1f, 0f, -1f, 1f, 0f },
        VerticesCount = 4,
        Indices = new[] { 0, 1, 2, 0, 2, 3 },
        IndicesCount = 6,
    };

    private static void AssertLeftHalf(byte[] pixels, int size, string what)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int at = (y * size + x) * 4;
                byte expected = x < size / 2 ? (byte)255 : (byte)0;
                Assert.True(pixels[at] == expected && pixels[at + 3] == 255,
                    what + ": pixel " + x + "," + y + " is " + pixels[at] + ", expected " + expected);
            }
        }
    }

    private const string SolidVertex = """
        #version 330 core
        layout(location = 0) in vec3 position;
        void main() { gl_Position = vec4(position, 1); }
        """;

    private const string SolidFragment = """
        #version 330 core
        out vec4 color;
        void main() { color = vec4(1); }
        """;

    private static void PrepareDraw(IOptimumGraphicsDevice seam, int target, int program, int size)
    {
        seam.BindFramebuffer(target);
        seam.UseProgram(program);
        seam.SetViewport(0, 0, size, size);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
    }

    /// <summary>
    /// The named defect: static meshes sat in ReBAR. Through the device (upload
    /// manager present) every buffer of a static mesh is DeviceBuffers-class,
    /// device-local, in a type that is not host visible when the device has one,
    /// and the ReBAR class does not grow with them. Drawn over several frames with
    /// Present between them and read back after, the geometry is right.
    /// </summary>
    [SkippableFact]
    public void AStaticMeshLandsInDeviceLocalMemoryOffReBarAndStillDraws()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 8;
            VulkanContext context = device!.ContextForTests;
            VulkanAllocator allocator = context.Allocator;

            int program = GpuTest.LinkProgram(seam, SolidVertex, SolidFragment, "policy-static");
            int target = CreateTarget(seam, size);

            seam.BeginFrame();
            seam.Present();
            ulong reBarBefore = allocator.ReBarUsed;

            var meshes = new List<int>();
            for (int i = 0; i < 16; i++) meshes.Add(seam.CreateMesh(Quad(0f), true));
            int dynamicMesh = seam.CreateMesh(Quad(0f), false);

            Assert.Equal(reBarBefore, allocator.ReBarUsed);

            foreach (int mesh in meshes)
            {
                foreach (int slot in new[] { MeshManager.BufferXyz, -1 })
                {
                    VulkanBuffer buffer = device.MeshesForTests.BufferOf(mesh, slot)!;
                    MemoryBlock block = buffer.Allocation.Block!;
                    MemoryPropertyFlags flags = allocator.FlagsOf(block.TypeIndex);
                    MemoryRequirements requirements = VulkanAllocator.BufferRequirements(context, buffer.Handle, out _);

                    Assert.Equal(MemoryPoolClass.DeviceBuffers, block.Class);
                    Assert.True((flags & MemoryPropertyFlags.DeviceLocalBit) != 0, "static mesh memory is not device local: " + flags);
                    if (allocator.HasMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit,
                            MemoryPropertyFlags.HostVisibleBit))
                    {
                        Assert.True((flags & MemoryPropertyFlags.HostVisibleBit) == 0,
                            "a static mesh took host-visible memory although a device-local-only type exists: " + flags);
                        Assert.Equal(IntPtr.Zero, buffer.Mapped);
                    }
                }
            }

            // Dynamic meshes stay host mapped (the chunk tesselator writes through
            // the pointer) and are not ReBAR either.
            VulkanBuffer dynamicXyz = device.MeshesForTests.BufferOf(dynamicMesh, MeshManager.BufferXyz)!;
            Assert.NotEqual(IntPtr.Zero, dynamicXyz.Mapped);
            Assert.Equal(MemoryPoolClass.DeviceBuffers, dynamicXyz.Allocation.Block!.Class);

            // Several frames, Present between them, then read back.
            for (int frame = 0; frame < 4; frame++)
            {
                seam.BeginFrame();
                PrepareDraw(seam, target, program, size);
                seam.DrawMesh(meshes[frame]);
                seam.Present();
            }

            seam.BeginFrame();
            AssertLeftHalf(Read(seam, target, size), size, "static device-local quad");
            seam.Present();

            _output.WriteLine(VulkanAllocator.FormatMemoryLine(allocator.Snapshot()));
            foreach (int mesh in meshes) seam.DeleteMesh(mesh);
            seam.DeleteMesh(dynamicMesh);
            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// VkMemoryDedicatedRequirements is honoured: a request marked dedicated gets
    /// its own block naming the resource (validation checks the size and handle
    /// match), binds, holds its bytes and gives the block back on free. The size
    /// rule is per class: at or above a quarter of the class's block.
    /// </summary>
    [SkippableFact]
    public unsafe void TheDedicatedRequirementAndTheQuarterBlockRuleGiveOwnBlocks()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            VulkanAllocator allocator = context!.Allocator;
            Vk api = context.Api;

            var createInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = 4096,
                Usage = BufferUsageFlags.VertexBufferBit,
                SharingMode = SharingMode.Exclusive,
            };
            Assert.Equal(Result.Success, api.CreateBuffer(context.Device, &createInfo, null, out Buffer handle));

            MemoryRequirements requirements = VulkanAllocator.BufferRequirements(context, handle, out bool driverWants);
            _output.WriteLine("driver requires or prefers dedicated for a 4 KiB vertex buffer: " + driverWants);

            int before = allocator.BlockCount;
            MemoryAllocation allocation = allocator.Allocate(requirements,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, linear: true,
                "a dedicated-requirement probe", MemoryPoolClass.DeviceBuffers, requiresDedicated: true, handle, default);

            Assert.True(allocation.Block!.Dedicated);
            Assert.Equal(requirements.Size, allocation.Block.Size);
            Assert.Equal(before + 1, allocator.BlockCount);
            Assert.Equal(Result.Success, api.BindBufferMemory(context.Device, handle, allocation.Memory, allocation.Offset));
            Assert.NotEqual(IntPtr.Zero, allocation.Mapped);
            *(int*)allocation.Mapped = 0x5EED;
            Assert.Equal(0x5EED, *(int*)allocation.Mapped);

            api.DestroyBuffer(context.Device, handle, null);
            allocator.Free(allocation);
            Assert.Equal(before, allocator.BlockCount);

            // Quarter-block rule per class: DeviceBuffers blocks are 64 MiB, Staging 32.
            using (var pooled = new VulkanBuffer(context, 8 * MiB, BufferUsageFlags.VertexBufferBit,
                       MemoryPropertyFlags.HostVisibleBit, MemoryPoolClass.DeviceBuffers))
            using (var ownBuffers = new VulkanBuffer(context, 16 * MiB, BufferUsageFlags.VertexBufferBit,
                       MemoryPropertyFlags.HostVisibleBit, MemoryPoolClass.DeviceBuffers))
            using (var ownStaging = new VulkanBuffer(context, 8 * MiB, BufferUsageFlags.TransferSrcBit,
                       MemoryPropertyFlags.HostVisibleBit, MemoryPoolClass.Staging))
            {
                Assert.False(pooled.Allocation.Block!.Dedicated && !DriverWantsDedicated(context, pooled));
                Assert.True(ownBuffers.Allocation.Block!.Dedicated);
                Assert.True(ownStaging.Allocation.Block!.Dedicated);
                Assert.Equal(MemoryPoolClass.Staging, ownStaging.Allocation.Block.Class);
            }

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    private static bool DriverWantsDedicated(VulkanContext context, VulkanBuffer buffer)
    {
        VulkanAllocator.BufferRequirements(context, buffer.Handle, out bool dedicated);
        return dedicated;
    }

    /// <summary>
    /// The ReBAR class stays under its cap. Past it (or on a device with no ReBAR
    /// type at all) a request falls through to host-visible Staging memory, is
    /// counted in the allocator and in VulkanStats.RebarFallbacks, and is logged;
    /// the fallen-through memory is mapped and holds its bytes.
    /// </summary>
    [SkippableFact]
    public unsafe void TheReBarCapHoldsAndAMissIsACountedLoggedFallThrough()
    {
        Skip.IfNot(GpuTest.TryCreateContext(_output, null, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            VulkanAllocator allocator = context!.Allocator;
            var logged = new List<string>();
            allocator.Log = line => { lock (logged) logged.Add(line); };
            allocator.ReBarCapOverrideForTests = 16 * MiB;

            bool hasReBar = allocator.HasMemoryType(uint.MaxValue,
                MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit, 0);
            long statsBefore = VulkanStats.RebarFallbacks;

            var buffers = new List<VulkanBuffer>();
            try
            {
                for (int i = 0; i < 40; i++)
                {
                    buffers.Add(new VulkanBuffer(context, MiB, BufferUsageFlags.UniformBufferBit,
                        MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit
                        | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.ReBar));
                }

                Assert.True(allocator.ReBarUsed <= 16 * MiB, "ReBAR use " + allocator.ReBarUsed + " passed the cap");
                Assert.True(allocator.ReBarMisses > 0, "40 MiB of ReBAR requests under a 16 MiB cap never missed");
                Assert.True(VulkanStats.RebarFallbacks - statsBefore >= allocator.ReBarMisses,
                    "misses were not counted in VulkanStats");
                Assert.NotEmpty(logged);
                Assert.Contains("ReBAR miss", logged[0]);

                int inReBar = 0;
                int fellThrough = 0;
                for (int i = 0; i < buffers.Count; i++)
                {
                    MemoryBlock block = buffers[i].Allocation.Block!;
                    if (block.Class == MemoryPoolClass.ReBar)
                    {
                        inReBar++;
                        MemoryPropertyFlags flags = allocator.FlagsOf(block.TypeIndex);
                        Assert.True((flags & MemoryPropertyFlags.DeviceLocalBit) != 0
                                    && (flags & MemoryPropertyFlags.HostVisibleBit) != 0);
                    }
                    else
                    {
                        fellThrough++;
                        Assert.Equal(MemoryPoolClass.Staging, block.Class);
                    }

                    Assert.NotEqual(IntPtr.Zero, buffers[i].Mapped);
                    *(int*)buffers[i].Mapped = i * 31;
                }
                for (int i = 0; i < buffers.Count; i++) Assert.Equal(i * 31, *(int*)buffers[i].Mapped);

                _output.WriteLine("ReBAR type present: " + hasReBar + "; in ReBAR " + inReBar + ", fell through " +
                    fellThrough + "; " + VulkanAllocator.FormatMemoryLine(allocator.Snapshot()));
                Assert.True(fellThrough > 0);
                if (hasReBar) Assert.True(inReBar > 0, "a ReBAR type exists but nothing landed in it under the cap");
                else Assert.Equal(0, inReBar);
            }
            finally
            {
                foreach (VulkanBuffer buffer in buffers) buffer.Dispose();
            }
        }
    }

    /// <summary>
    /// A miss is a fall-through, not a failure: with the ReBAR cap at zero the
    /// device's indirect ring (per-frame dynamic data, created at the first
    /// multi-draw) lands in host staging memory, the miss is counted, and the
    /// multi-draw still renders the right pixels across frames.
    /// </summary>
    [SkippableFact]
    public void AMultiDrawWhoseIndirectRingMissedReBarStillRenders()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 8;
            VulkanAllocator allocator = device!.ContextForTests.Allocator;
            allocator.ReBarCapOverrideForTests = 0;
            long missesBefore = allocator.ReBarMisses;

            int program = GpuTest.LinkProgram(seam, SolidVertex, SolidFragment, "policy-rebar-miss");
            int target = CreateTarget(seam, size);
            int mesh = seam.CreateMesh(Quad(0f), false);

            for (int frame = 0; frame < 3; frame++)
            {
                seam.BeginFrame();
                PrepareDraw(seam, target, program, size);
                seam.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, false);
                seam.Present();
            }

            Assert.True(allocator.ReBarMisses > missesBefore, "the indirect ring did not ask for ReBAR");

            seam.BeginFrame();
            AssertLeftHalf(Read(seam, target, size), size, "multi-draw through a fallen-through indirect ring");
            seam.Present();

            seam.DeleteMesh(mesh);
            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// No general defrag: an emptied pooled block survives 119 frames and is freed
    /// on the 120th; a refill in between restarts the count. With a heap over its
    /// budget, empty blocks go at the next frame boundary.
    /// </summary>
    [SkippableFact]
    public void AnEmptyBlockIsFreedAfter120EmptyFramesOrAtOnceUnderPressure()
    {
        Skip.IfNot(GpuTest.TryCreateContext(_output, null, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            VulkanAllocator allocator = context!.Allocator;
            const MemoryPropertyFlags host = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

            int baseline = allocator.BlockCount;
            var first = new VulkanBuffer(context, MiB, BufferUsageFlags.VertexBufferBit, host, MemoryPoolClass.DeviceBuffers);
            Assert.Equal(baseline + 1, allocator.BlockCount);
            first.Dispose();
            Assert.Equal(baseline + 1, allocator.BlockCount);

            for (int i = 0; i < 60; i++) allocator.AdvanceFrame();

            // Refilled and emptied again: the count starts over.
            var refill = new VulkanBuffer(context, MiB, BufferUsageFlags.VertexBufferBit, host, MemoryPoolClass.DeviceBuffers);
            Assert.Equal(baseline + 1, allocator.BlockCount);
            refill.Dispose();

            long freedBefore = allocator.Snapshot().EmptyBlocksFreed;
            for (int i = 0; i < VulkanAllocator.EmptyBlockFrames - 1; i++) allocator.AdvanceFrame();
            Assert.Equal(baseline + 1, allocator.BlockCount);

            allocator.AdvanceFrame();
            Assert.Equal(baseline, allocator.BlockCount);
            Assert.Equal(freedBefore + 1, allocator.Snapshot().EmptyBlocksFreed);

            // Budget pressure: every heap's budget at one byte.
            var pressured = new VulkanBuffer(context, MiB, BufferUsageFlags.VertexBufferBit, host, MemoryPoolClass.DeviceBuffers);
            pressured.Dispose();
            Assert.Equal(baseline + 1, allocator.BlockCount);
            allocator.HeapBudgetOverrideForTests = 1;
            allocator.AdvanceFrame();
            Assert.Equal(baseline, allocator.BlockCount);
        }
    }

    /// <summary>
    /// The heap report: one used/budget pair per memory heap, budgets from
    /// VK_EXT_memory_budget when enabled (never above the heap) or heap x 0.7, and
    /// a pooled block's bytes show on its heap and its class.
    /// </summary>
    [SkippableFact]
    public void TheHeapReportCarriesUsedAndBudgetPerHeap()
    {
        Skip.IfNot(GpuTest.TryCreateContext(_output, null, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            VulkanAllocator allocator = context!.Allocator;
            context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out PhysicalDeviceMemoryProperties memory);

            using var buffer = new VulkanBuffer(context, MiB, BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.DeviceBuffers);
            MemoryBlock block = buffer.Allocation.Block!;

            MemorySnapshot snapshot = allocator.Snapshot();
            string line = VulkanAllocator.FormatMemoryLine(snapshot);
            _output.WriteLine("VK_EXT_memory_budget: " + allocator.BudgetExtension + "; " + line);

            Assert.Equal((int)memory.MemoryHeapCount, snapshot.HeapUsed.Length);
            Assert.Equal((int)memory.MemoryHeapCount, snapshot.HeapBudget.Length);
            for (int heap = 0; heap < memory.MemoryHeapCount; heap++)
            {
                Assert.True(snapshot.HeapBudget[heap] > 0, "heap " + heap + " has no budget");
                if (!allocator.BudgetExtension)
                {
                    Assert.Equal((ulong)(memory.MemoryHeaps[heap].Size * VulkanAllocator.FallbackBudgetShare),
                        snapshot.HeapBudget[heap]);
                }
            }

            Assert.True(snapshot.HeapUsed[block.HeapIndex] >= block.Size);
            Assert.True(snapshot.ClassBytes[(int)MemoryPoolClass.DeviceBuffers] >= block.Size);
            Assert.StartsWith("stats.memory blocks=", line);
            Assert.Contains(" heaps=", line);
            Assert.Contains(" budget_ext=" + (allocator.BudgetExtension ? "1" : "0"), line);
            Assert.Equal((int)memory.MemoryHeapCount - 1, line.Substring(line.IndexOf(" heaps=", StringComparison.Ordinal)).Split(',').Length - 1);
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/AllocatorTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The suballocator. A device allocation is a driver-tracked object whose cost
/// is paid on every submit, so what matters is the count, not the bytes: a
/// loaded world at one allocation per resource reached eighteen thousand of them
/// and 200 ms frames.
/// </summary>
public class AllocatorTests
{
    private readonly ITestOutputHelper _output;

    public AllocatorTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(ITestOutputHelper output, out VulkanContext? context) =>
        GpuTest.TryCreateContext(output, null, out context);

    /// <summary>
    /// The regression that matters. A chunk mesh is several buffers, and a world
    /// is thousands of chunks; that has to stay in the tens of allocations.
    /// </summary>
    [SkippableFact]
    public void AWorldsWorthOfBuffersCostsTensOfAllocationsNotThousands()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            int before = VulkanMemory.LiveAllocations;
            var buffers = new List<VulkanBuffer>();

            // 2000 buffers of 64 KB: about the shape of a loaded world's meshes.
            for (int i = 0; i < 2000; i++)
            {
                buffers.Add(new VulkanBuffer(context!, 64 * 1024,
                    BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
            }

            int used = VulkanMemory.LiveAllocations - before;
            _output.WriteLine($"2000 buffers cost {used} device allocations, " +
                $"{context!.Allocator.BlockCount} blocks live");

            // 2000 * 64 KB is 128 MB, so two 64 MB blocks is the floor; a handful
            // of extra for alignment is fine. Anything near 2000 is the old bug.
            Assert.InRange(used, 1, 16);

            foreach (VulkanBuffer buffer in buffers) buffer.Dispose();
        }
    }

    [SkippableFact]
    public void EveryBufferGetsDistinctMappedMemoryWithinItsBlock()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var buffers = new List<VulkanBuffer>();
            for (int i = 0; i < 64; i++)
            {
                buffers.Add(new VulkanBuffer(context!, 4096,
                    BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
            }

            // Each buffer writes its own index through its own pointer; if two
            // shared a region, or a pointer were the block base rather than the
            // buffer's slice, the values would not survive.
            for (int i = 0; i < buffers.Count; i++)
            {
                Assert.NotEqual(IntPtr.Zero, buffers[i].Mapped);
                unsafe { *(int*)buffers[i].Mapped = i; }
            }
            for (int i = 0; i < buffers.Count; i++)
            {
                unsafe { Assert.Equal(i, *(int*)buffers[i].Mapped); }
            }

            foreach (VulkanBuffer buffer in buffers) buffer.Dispose();
        }
    }

    /// <summary>
    /// No two live resources may ever share a byte.
    ///
    /// Distinct pointers are not the same claim: two regions can start in
    /// different places and still overlap, and the free list is where that goes
    /// wrong - a range merged with a neighbour it does not touch, or a split
    /// that loses its alignment padding, hands the same bytes out twice. On the
    /// GPU that is one resource writing over another's contents, which reads as
    /// corrupt geometry or textures rather than as a crash.
    ///
    /// So this runs the shape chunk streaming actually has - many live at once,
    /// mixed sizes and alignments, a churn of frees and fresh allocations - and
    /// checks every live pair after every round.
    /// </summary>
    [SkippableFact]
    public void LiveAllocationsNeverOverlapUnderChurn()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            // Sizes a chunk pool really asks for: the xyz slot is large, the
            // flag and colour streams small, and index buffers in between.
            int[] sizes = { 1024, 4096, 12_288, 65_536, 262_144, 3072, 48_000, 6144 };

            var live = new List<VulkanBuffer>();
            var random = new Random(20260909);

            void AssertNoOverlap(int round)
            {
                // Grouped by memory, since regions in different allocations are
                // unrelated however their offsets compare.
                var byMemory = new Dictionary<ulong, List<(ulong Start, ulong End, int Index)>>();

                for (int i = 0; i < live.Count; i++)
                {
                    MemoryAllocation allocation = live[i].Allocation;
                    ulong memory = allocation.Memory.Handle;
                    if (!byMemory.TryGetValue(memory, out var regions))
                    {
                        regions = new List<(ulong, ulong, int)>();
                        byMemory[memory] = regions;
                    }
                    regions.Add((allocation.Offset, allocation.Offset + allocation.Size, i));
                }

                foreach ((ulong memory, var regions) in byMemory)
                {
                    regions.Sort((a, b) => a.Start.CompareTo(b.Start));
                    for (int i = 1; i < regions.Count; i++)
                    {
                        Assert.True(regions[i].Start >= regions[i - 1].End,
                            $"round {round}: buffers {regions[i - 1].Index} and {regions[i].Index} overlap in " +
                            $"memory {memory:x}: [{regions[i - 1].Start}, {regions[i - 1].End}) and " +
                            $"[{regions[i].Start}, {regions[i].End})");
                    }
                }
            }

            for (int round = 0; round < 12; round++)
            {
                for (int i = 0; i < 120; i++)
                {
                    live.Add(new VulkanBuffer(context!, (ulong)sizes[random.Next(sizes.Length)],
                        BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit,
                        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
                }

                AssertNoOverlap(round);

                // Drop a scattered half, so the free list has to merge some
                // neighbours and keep others apart.
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    if (random.Next(2) != 0) continue;
                    live[i].Dispose();
                    live.RemoveAt(i);
                }

                AssertNoOverlap(round);
            }

            // And the bytes themselves survive, which no offset arithmetic can
            // fake: each buffer stamps its own identity and reads it back.
            for (int i = 0; i < live.Count; i++)
            {
                unsafe { *(int*)live[i].Mapped = i * 7919; }
            }
            for (int i = 0; i < live.Count; i++)
            {
                unsafe
                {
                    Assert.Equal(i * 7919, *(int*)live[i].Mapped);
                }
            }

            _output.WriteLine($"{live.Count} live buffers in {context!.Allocator.BlockCount} blocks");
            foreach (VulkanBuffer buffer in live) buffer.Dispose();
        }
    }

    /// <summary>
    /// Chunk streaming frees and reallocates constantly. Freed space has to come
    /// back, or a long session grows blocks without bound.
    /// </summary>
    [SkippableFact]
    public void FreedSpaceIsReusedRatherThanGrowingThePool()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const int batch = 200;
            for (int round = 0; round < 5; round++)
            {
                var buffers = new List<VulkanBuffer>();
                for (int i = 0; i < batch; i++)
                {
                    buffers.Add(new VulkanBuffer(context!, 128 * 1024,
                        BufferUsageFlags.VertexBufferBit,
                        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
                }
                foreach (VulkanBuffer buffer in buffers) buffer.Dispose();

                _output.WriteLine($"round {round}: {context!.Allocator.BlockCount} blocks");
            }

            // Five rounds of the same batch must not leave five rounds of blocks.
            Assert.InRange(context!.Allocator.BlockCount, 0, 4);
        }
    }

    /// <summary>
    /// A block only ever holds one kind of resource, which is what makes
    /// bufferImageGranularity a non-issue. Mixing them in one block is the
    /// classic source of corruption that only shows on some hardware.
    /// </summary>
    [SkippableFact]
    public void ImagesAndBuffersNeverShareABlock()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);

            var buffer = new VulkanBuffer(context!, 4096,
                BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            int textureId = textures.Create(64, 64, Format.R8G8B8A8Unorm);
            VulkanTexture? texture = textures.Get(textureId);
            Assert.NotNull(texture);

            Assert.NotEqual(buffer.MemoryHandleForTest, texture!.Allocation.Memory.Handle);

            buffer.Dispose();
        }
    }

    /// <summary>A resource too large to pool gets its own block rather than forcing a huge one.</summary>
    [SkippableFact]
    public void AnOversizedResourceGetsItsOwnBlockAndGivesItBack()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            int before = context!.Allocator.BlockCount;

            // Larger than the pooling threshold: an atlas is this shape.
            var big = new VulkanBuffer(context, 48UL * 1024 * 1024,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            Assert.Equal(before + 1, context.Allocator.BlockCount);

            big.Dispose();
            Assert.Equal(before, context.Allocator.BlockCount);
        }
    }
}
}
