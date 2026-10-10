using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Resource purpose used for VMA pools and placement policy.</summary>
internal enum MemoryPoolClass
{
    DeviceImages = 0,
    DeviceBuffers = 1,
    Staging = 2,
    ReBar = 3,
    Transient = 4,
    Dedicated = 5,
}

/// <summary>One VMA allocation, released only after all GPU users complete.</summary>
internal readonly struct MemoryAllocation
{
    public DeviceMemory Memory { get; init; }
    public ulong Offset { get; init; }
    public ulong Size { get; init; }
    public IntPtr Mapped { get; init; }
    internal ulong Allocation { get; init; }
    public bool IsValid => Allocation != 0;
}

/// <summary>Actual VMA memory-block totals and physical-device heap budgets.</summary>
internal readonly record struct MemorySnapshot(
    int Blocks,
    int DedicatedBlocks,
    ulong ReBarUsed,
    ulong ReBarCap,
    long ReBarMisses,
    long EmptyBlocksFreed,
    bool BudgetExtension,
    ulong[] ClassBytes,
    ulong[] HeapUsed,
    ulong[] HeapBudget,
    uint[] HeapFlags,
    ulong[] HeapDriverUsage);

/// <summary>
/// VMA owns Vulkan memory, mapping, suballocation, granularity, dedicated memory
/// and empty-block reclamation. This adapter retains the renderer's placement
/// policy and its existing GPU-safe resource retirement interface.
/// </summary>
internal sealed unsafe class VulkanAllocator : IDisposable
{
    public const int PoolClassCount = 6;
    private const ulong MiB = 1024UL * 1024;
    public const int EmptyBlockFrames = 120;
    public const ulong ReBarCapCeiling = 192 * MiB;
    public const double FallbackBudgetShare = 0.7;
    private const int LoggedMissLimit = 32;
    private static readonly bool AlwaysDedicated =
        Environment.GetEnvironmentVariable("VULKANSTORY_VULKAN_DEDICATED_MEMORY") == "1";
    private static readonly bool ReBarDisabled =
        Environment.GetEnvironmentVariable("VULKANSTORY_VULKAN_NO_REBAR") == "1";
    private readonly object _gate = new();
    private readonly VmaRuntime _vma;
    private readonly PhysicalDeviceMemoryProperties _memoryProperties;
    private readonly Dictionary<ulong, VmaAllocationInfo> _allocations = new();
    private readonly VmaHeapStats[] _heaps;
    private readonly VmaClassStats[] _classes = new VmaClassStats[PoolClassCount];
    private ulong _transientPeak;
    private long _reBarMisses;
    private long _systemMemoryFallbacks;
    private long _allocationFailures;
    private long _emptyBlocksFreed;
    private uint _frame;
    private int _knownBlocks;
    private ulong _knownPooledBlocks;
    private bool _disposed;

    public VulkanAllocator(VulkanContext context)
    {
        context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out _memoryProperties);
        _heaps = new VmaHeapStats[_memoryProperties.MemoryHeapCount];
        BudgetExtension = context.MemoryBudgetAvailable;
        _vma = new VmaRuntime(context);
        try { RefreshStatisticsLocked(); }
        catch { _vma.Dispose(); throw; }
    }

    public Action<string>? Log { get; set; }
    public Action<string>? Trace { get; set; }
    public bool BudgetExtension { get; }
    internal ulong? ReBarCapOverrideForTests { get; set; }
    internal ulong? HeapBudgetOverrideForTests { get; set; }
    internal ulong PersistentMeshReserveBytes { get; private set; }
    public int BlockCount { get { lock (_gate) { RefreshStatisticsLocked(); return _knownBlocks; } } }
    public long ReBarMisses { get { lock (_gate) return _reBarMisses; } }
    public ulong ReBarUsed { get { lock (_gate) { RefreshStatisticsLocked(); return _classes[(int)MemoryPoolClass.ReBar].BlockBytes; } } }
    public ulong TransientHeapBytes { get { lock (_gate) { RefreshStatisticsLocked(); return _classes[(int)MemoryPoolClass.Transient].BlockBytes; } } }

    /// <summary>Retained reserve-sizing policy; VMA selects actual backing block sizes.</summary>
    public static ulong BlockSizeOf(MemoryPoolClass poolClass) => poolClass switch
    {
        MemoryPoolClass.DeviceImages => 128 * MiB,
        MemoryPoolClass.DeviceBuffers => 64 * MiB,
        MemoryPoolClass.Staging => 32 * MiB,
        MemoryPoolClass.ReBar => 16 * MiB,
        MemoryPoolClass.Transient => 64 * MiB,
        _ => 64 * MiB,
    };

    public static MemoryPoolClass InferClass(MemoryPropertyFlags properties, bool linear)
    {
        if (!linear) return MemoryPoolClass.DeviceImages;
        const MemoryPropertyFlags reBar = MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit;
        return (properties & reBar) == reBar ? MemoryPoolClass.ReBar : MemoryPoolClass.DeviceBuffers;
    }

    public MemoryAllocation Allocate(
        MemoryRequirements requirements, MemoryPropertyFlags properties, bool linear, string what) =>
        Allocate(requirements, properties, linear, what, InferClass(properties, linear), false, default, default);

    /// <summary>Allocates VMA memory for the existing resource, retaining its required memory flags.</summary>
    public MemoryAllocation Allocate(
        MemoryRequirements requirements, MemoryPropertyFlags properties, bool linear, string what,
        MemoryPoolClass poolClass, bool requiresDedicated, Buffer buffer, Image image)
    {
        if (poolClass == MemoryPoolClass.Dedicated)
        {
            requiresDedicated = true;
            poolClass = InferClass(properties, linear);
        }
        if ((uint)poolClass >= PoolClassCount) throw new ArgumentOutOfRangeException(nameof(poolClass));
        if (requirements.Size == 0) throw new ArgumentOutOfRangeException(nameof(requirements));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (poolClass == MemoryPoolClass.ReBar)
                return AllocateReBarLocked(requirements, properties, linear, what, requiresDedicated, buffer, image);

            Result result = TryAllocateTypesLocked(requirements, properties, Avoided(properties), poolClass,
                linear, requiresDedicated, buffer, image, out MemoryAllocation allocation);
            if (result == Result.Success) return allocation;
            if (result == Result.ErrorOutOfDeviceMemory &&
                (properties & MemoryPropertyFlags.DeviceLocalBit) != 0 &&
                ((poolClass == MemoryPoolClass.DeviceImages && !linear && properties == MemoryPropertyFlags.DeviceLocalBit) ||
                 (poolClass == MemoryPoolClass.DeviceBuffers && linear)))
            {
                // Preserve coherent mapped writes for meshes while leaving VRAM
                // available to images and provider reconstruction/FG contexts.
                MemoryPropertyFlags systemProperties = (properties & ~MemoryPropertyFlags.DeviceLocalBit) |
                    MemoryPropertyFlags.HostVisibleBit;
                if (linear) systemProperties |= MemoryPropertyFlags.HostCoherentBit;
                Result fallback = TryAllocateTypesLocked(requirements, systemProperties,
                    MemoryPropertyFlags.DeviceLocalBit, poolClass, linear, requiresDedicated, buffer, image,
                    out allocation, allowAvoided: false);
                if (fallback == Result.Success)
                {
                    if (++_systemMemoryFallbacks <= LoggedMissLimit)
                        Log?.Invoke($"Vulkan VRAM headroom exhausted; allocated {what} on compatible system memory");
                    return allocation;
                }
                if (fallback != Result.ErrorFeatureNotPresent) result = fallback;
            }
            throw AllocationFailureLocked(result, requirements.Size, what);
        }
    }

    private MemoryAllocation AllocateReBarLocked(MemoryRequirements requirements, MemoryPropertyFlags properties,
        bool linear, string what, bool requiresDedicated, Buffer buffer, Image image)
    {
        MemoryPropertyFlags wanted = properties | MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit;
        MemoryAllocation preferred = default;
        Result result = ReBarDisabled ? Result.ErrorFeatureNotPresent :
            TryAllocateTypesLocked(requirements, wanted, 0, MemoryPoolClass.ReBar, linear, requiresDedicated,
                buffer, image, out preferred);
        // A disabled ReBAR request does not evaluate the native allocation call.
        if (!ReBarDisabled && result == Result.Success)
            return preferred;
        if (result is not (Result.ErrorOutOfDeviceMemory or Result.ErrorFeatureNotPresent))
            throw AllocationFailureLocked(result, requirements.Size, what);

        _reBarMisses++;
        VulkanStats.NoteRebarFallback();
        if (_reBarMisses <= LoggedMissLimit)
        {
            string reason = ReBarDisabled ? "VULKANSTORY_VULKAN_NO_REBAR=1" :
                result == Result.ErrorFeatureNotPresent ? "no compatible device-local host-visible memory" :
                "the ReBAR cap or heap budget has no physical allocation headroom";
            string line = $"[VulkanStory] ReBAR miss for {what}: {reason}; falling through to host-visible staging memory (miss {_reBarMisses})";
            Log?.Invoke(line);
            Trace?.Invoke(line);
        }
        MemoryPropertyFlags host = (properties & ~MemoryPropertyFlags.DeviceLocalBit) | MemoryPropertyFlags.HostVisibleBit;
        result = TryAllocateTypesLocked(requirements, host, MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.Staging,
            linear, requiresDedicated, buffer, image, out MemoryAllocation allocation);
        if (result != Result.Success) throw AllocationFailureLocked(result, requirements.Size, what);
        return allocation;
    }

    private Result TryAllocateTypesLocked(MemoryRequirements requirements, MemoryPropertyFlags properties,
        MemoryPropertyFlags avoid, MemoryPoolClass poolClass, bool linear, bool dedicated,
        Buffer buffer, Image image, out MemoryAllocation allocation, bool allowAvoided = true)
    {
        allocation = default;
        Result last = Result.ErrorFeatureNotPresent;
        // Prefer types without the avoided properties, then accept a compatible
        // unified-memory type when there is no discrete alternative.
        int passes = avoid != 0 && allowAvoided ? 2 : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            for (uint typeIndex = 0; typeIndex < _memoryProperties.MemoryTypeCount; typeIndex++)
            {
                if ((requirements.MemoryTypeBits & (1u << (int)typeIndex)) == 0) continue;
                MemoryType type = _memoryProperties.MemoryTypes[(int)typeIndex];
                if ((type.PropertyFlags & properties) != properties) continue;
                bool avoided = (type.PropertyFlags & avoid) != 0;
                if (avoid != 0 && (pass == 0 ? avoided : !avoided)) continue;
                var request = new VmaAllocateInfo
                {
                    Size = requirements.Size,
                    Alignment = requirements.Alignment,
                    Buffer = buffer.Handle,
                    Image = image.Handle,
                    MemoryTypeBits = 1u << (int)typeIndex,
                    RequiredFlags = (uint)properties,
                    PoolClass = (uint)poolClass,
                    Flags = ((type.PropertyFlags & MemoryPropertyFlags.HostVisibleBit) != 0 ? 1u | 16u : 0u) |
                        (AlwaysDedicated || dedicated ? 2u : 0u) |
                        (!linear && buffer.Handle == 0 && image.Handle == 0 ? 4u : 0u),
                    GrowthLimitBytes = 0,
                };
                // Reuse an existing VMA block before considering physical growth;
                // this remains valid even while external provider use is high.
                last = AllocateNativeLocked(request, out VmaAllocationInfo info);
                if (last == Result.ErrorOutOfDeviceMemory)
                {
                    // Provider contexts can consume VRAM several times within
                    // one renderer frame. VMA refreshes driver budget data when
                    // setting the frame index, including the current index.
                    _vma.SetFrameIndex(_frame);
                    RefreshStatisticsLocked();
                    request.GrowthLimitBytes = GrowthHeadroomLocked(type.HeapIndex, poolClass);
                    if (poolClass == MemoryPoolClass.ReBar)
                    {
                        ulong cap = ReBarCapLocked(typeIndex);
                        ulong used = _classes[(int)MemoryPoolClass.ReBar].BlockBytes;
                        request.GrowthLimitBytes = Math.Min(request.GrowthLimitBytes, used < cap ? cap - used : 0);
                    }
                    if (request.GrowthLimitBytes != 0)
                        last = AllocateNativeLocked(request, out info);
                }
                if (last == Result.Success)
                {
                    try { _allocations.Add(info.Allocation, info); }
                    catch { _vma.Free(info.Allocation); RefreshStatisticsLocked(); throw; }
                    allocation = new MemoryAllocation
                    {
                        Memory = new DeviceMemory(info.Memory),
                        Offset = info.Offset,
                        Size = info.Size,
                        Mapped = (IntPtr)info.Mapped,
                        Allocation = info.Allocation,
                    };
                    return Result.Success;
                }
                if (last != Result.ErrorOutOfDeviceMemory) return last;
            }
        }
        return last;
    }

    private Result AllocateNativeLocked(in VmaAllocateInfo request, out VmaAllocationInfo allocation)
    {
        Result result = _vma.Allocate(request, out allocation);
        try { RefreshStatisticsLocked(); }
        catch
        {
            if (result == Result.Success) _vma.Free(allocation.Allocation);
            throw;
        }
        return result;
    }

    private ulong GrowthHeadroomLocked(uint heap, MemoryPoolClass poolClass)
    {
        ulong budget = HeapBudgetLocked(heap);
        ulong used = HeapUsageLocked(heap);
        ulong available = used < budget ? budget - used : 0;
        bool local = (_memoryProperties.MemoryHeaps[(int)heap].Flags & MemoryHeapFlags.DeviceLocalBit) != 0;
        ulong reserve = local && poolClass == MemoryPoolClass.DeviceBuffers ? MeshReserveLocked(heap) : 0;
        return reserve <= available ? available - reserve : 0;
    }

    private ulong HeapBudgetLocked(uint heap) => HeapBudgetOverrideForTests ?? _heaps[heap].Budget;
    private ulong HeapUsageLocked(uint heap) => Math.Max(_heaps[heap].Usage, _heaps[heap].BlockBytes);
    private ulong ReBarCapLocked(uint typeIndex) => ReBarCapOverrideForTests ??
        Math.Min(ReBarCapCeiling, HeapBudgetLocked(_memoryProperties.MemoryTypes[(int)typeIndex].HeapIndex) / 4);

    private ulong MeshReserveLocked(uint heap)
    {
        ulong external = _heaps[heap].Usage > _heaps[heap].BlockBytes ?
            _heaps[heap].Usage - _heaps[heap].BlockBytes : 0;
        ulong images = _heaps[heap].ImageBlockBytes;
        ulong combined = images > ulong.MaxValue - external ? ulong.MaxValue : images + external;
        PersistentMeshReserveBytes = Math.Max(BlockSizeOf(MemoryPoolClass.DeviceImages), combined);
        return PersistentMeshReserveBytes;
    }

    /// <remarks>Call only after all GPU users of the allocation have completed.</remarks>
    public void Free(in MemoryAllocation allocation)
    {
        if (!allocation.IsValid) return;
        lock (_gate)
        {
            if (_disposed || !_allocations.Remove(allocation.Allocation)) return;
            _vma.Free(allocation.Allocation);
            RefreshStatisticsLocked();
        }
    }

    public void AdvanceFrame()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _vma.SetFrameIndex(++_frame);
            if (_frame % 60 == 0) RefreshStatisticsLocked();
        }
    }

    private void RefreshStatisticsLocked()
    {
        if (_disposed) return;
        _vma.GetStatistics(_heaps, _classes);
        int blocks = 0;
        foreach (VmaHeapStats heap in _heaps) blocks = checked(blocks + (int)heap.BlockCount);
        // Keep the shared allocation-failure counter tied to physical VMA blocks,
        // including dedicated allocations made elsewhere through VulkanMemory.
        for (int i = _knownBlocks; i < blocks; i++)
        {
            VulkanMemory.NoteAllocation();
            VulkanStats.NoteAllocation();
        }
        for (int i = blocks; i < _knownBlocks; i++) VulkanMemory.NoteFree();
        _knownBlocks = blocks;
        ulong pooledBlocks = 0;
        foreach (VmaClassStats purpose in _classes)
            pooledBlocks += purpose.BlockCount - Math.Min(purpose.BlockCount, purpose.DedicatedCount);
        if (pooledBlocks < _knownPooledBlocks) _emptyBlocksFreed += checked((long)(_knownPooledBlocks - pooledBlocks));
        _knownPooledBlocks = pooledBlocks;
        _transientPeak = Math.Max(_transientPeak, _classes[(int)MemoryPoolClass.Transient].BlockBytes);
    }

    public ulong TakeTransientHeapPeak()
    {
        lock (_gate)
        {
            RefreshStatisticsLocked();
            ulong peak = _transientPeak;
            _transientPeak = _classes[(int)MemoryPoolClass.Transient].BlockBytes;
            return peak;
        }
    }

    public MemorySnapshot Snapshot()
    {
        lock (_gate)
        {
            RefreshStatisticsLocked();
            var used = new ulong[_heaps.Length];
            var budgets = new ulong[_heaps.Length];
            var flags = new uint[_heaps.Length];
            var driverUsage = new ulong[_heaps.Length];
            for (int i = 0; i < _heaps.Length; i++)
            {
                used[i] = _heaps[i].BlockBytes;
                budgets[i] = HeapBudgetLocked((uint)i);
                flags[i] = _heaps[i].HeapFlags;
                driverUsage[i] = _heaps[i].Usage;
            }
            var classBytes = new ulong[PoolClassCount];
            ulong dedicated = 0;
            for (int i = 0; i < PoolClassCount; i++)
            {
                if (i != (int)MemoryPoolClass.Dedicated)
                    classBytes[i] = _classes[i].BlockBytes - Math.Min(_classes[i].BlockBytes, _classes[i].DedicatedBytes);
                else
                    classBytes[i] += _classes[i].BlockBytes - Math.Min(_classes[i].BlockBytes, _classes[i].DedicatedBytes);
                classBytes[(int)MemoryPoolClass.Dedicated] += _classes[i].DedicatedBytes;
                dedicated += _classes[i].DedicatedCount;
            }
            ulong cap = 0;
            if (TryFindMemoryType(uint.MaxValue, MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit,
                0, out uint reBarType)) cap = ReBarCapLocked(reBarType);
            return new MemorySnapshot(_knownBlocks, checked((int)dedicated),
                _classes[(int)MemoryPoolClass.ReBar].BlockBytes, cap, _reBarMisses, _emptyBlocksFreed,
                BudgetExtension, classBytes, used, budgets, flags, driverUsage);
        }
    }

    internal string DiagnosticMemoryLine() => FormatMemoryLine(Snapshot());

    private VulkanMemoryAllocationException AllocationFailureLocked(Result result, ulong size, string what)
    {
        string line = $"VMA allocation failed for {what} with {result} ({size} bytes requested); {FormatMemoryLine(Snapshot())}";
        if (++_allocationFailures <= LoggedMissLimit) { Log?.Invoke(line); Trace?.Invoke(line); }
        return new VulkanMemoryAllocationException(result, line);
    }

    private static MemoryPropertyFlags Avoided(MemoryPropertyFlags properties)
    {
        bool local = (properties & MemoryPropertyFlags.DeviceLocalBit) != 0;
        bool visible = (properties & MemoryPropertyFlags.HostVisibleBit) != 0;
        return local && !visible ? MemoryPropertyFlags.HostVisibleBit :
            visible && !local ? MemoryPropertyFlags.DeviceLocalBit : 0;
    }

    private bool TryFindMemoryType(uint bits, MemoryPropertyFlags properties, MemoryPropertyFlags avoid, out uint index)
    {
        for (uint i = 0; i < _memoryProperties.MemoryTypeCount; i++)
        {
            MemoryPropertyFlags flags = _memoryProperties.MemoryTypes[(int)i].PropertyFlags;
            if ((bits & (1u << (int)i)) != 0 && (flags & properties) == properties && (flags & avoid) == 0)
            { index = i; return true; }
        }
        index = 0;
        return false;
    }

    public bool HasLargeHostVisibleDeviceMemory(ulong minimumHeapBytes)
    {
        const MemoryPropertyFlags wanted = MemoryPropertyFlags.DeviceLocalBit |
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        for (int i = 0; i < _memoryProperties.MemoryTypeCount; i++)
        {
            MemoryType type = _memoryProperties.MemoryTypes[i];
            if ((type.PropertyFlags & wanted) == wanted &&
                _memoryProperties.MemoryHeaps[(int)type.HeapIndex].Size >= minimumHeapBytes) return true;
        }
        return false;
    }

    internal bool HasPersistentMeshHeadroom(ulong bytes)
    {
        const MemoryPropertyFlags wanted = MemoryPropertyFlags.DeviceLocalBit |
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        lock (_gate)
        {
            if (_disposed) return false;
            RefreshStatisticsLocked();
            for (uint i = 0; i < _memoryProperties.MemoryTypeCount; i++)
            {
                MemoryType type = _memoryProperties.MemoryTypes[(int)i];
                if ((type.PropertyFlags & wanted) == wanted &&
                    bytes <= GrowthHeadroomLocked(type.HeapIndex, MemoryPoolClass.DeviceBuffers)) return true;
            }
            return false;
        }
    }

    public MemoryPropertyFlags FlagsOf(uint typeIndex) => _memoryProperties.MemoryTypes[(int)typeIndex].PropertyFlags;
    public bool HasMemoryType(uint typeBits, MemoryPropertyFlags properties, MemoryPropertyFlags avoid) =>
        TryFindMemoryType(typeBits, properties, avoid, out _);

    /// <summary>A buffer's requirements plus whether the driver requires or prefers a dedicated allocation.</summary>
    public static MemoryRequirements BufferRequirements(VulkanContext context, Buffer buffer, out bool dedicated)
    {
        var dedicatedRequirements = new MemoryDedicatedRequirements
        {
            SType = StructureType.MemoryDedicatedRequirements,
        };
        var requirements = new MemoryRequirements2
        {
            SType = StructureType.MemoryRequirements2,
            PNext = &dedicatedRequirements,
        };
        var info = new BufferMemoryRequirementsInfo2
        {
            SType = StructureType.BufferMemoryRequirementsInfo2,
            Buffer = buffer,
        };
        context.Api.GetBufferMemoryRequirements2(context.Device, &info, &requirements);
        dedicated = dedicatedRequirements.RequiresDedicatedAllocation || dedicatedRequirements.PrefersDedicatedAllocation;
        return requirements.MemoryRequirements;
    }

    /// <summary>An image's requirements plus whether the driver requires or prefers a dedicated allocation.</summary>
    public static MemoryRequirements ImageRequirements(VulkanContext context, Image image, out bool dedicated)
    {
        var dedicatedRequirements = new MemoryDedicatedRequirements
        {
            SType = StructureType.MemoryDedicatedRequirements,
        };
        var requirements = new MemoryRequirements2
        {
            SType = StructureType.MemoryRequirements2,
            PNext = &dedicatedRequirements,
        };
        var info = new ImageMemoryRequirementsInfo2
        {
            SType = StructureType.ImageMemoryRequirementsInfo2,
            Image = image,
        };
        context.Api.GetImageMemoryRequirements2(context.Device, &info, &requirements);
        dedicated = dedicatedRequirements.RequiresDedicatedAllocation || dedicatedRequirements.PrefersDedicatedAllocation;
        return requirements.MemoryRequirements;
    }

    /// <summary>The <c>stats.memory</c> line: blocks, ReBAR use, bytes and Vulkan flags per heap.</summary>
    public static string FormatMemoryLine(MemorySnapshot snapshot)
    {
        var line = new StringBuilder("stats.memory");
        line.Append(" blocks=").Append(snapshot.Blocks.ToString(CultureInfo.InvariantCulture));
        line.Append(" dedicated=").Append(snapshot.DedicatedBlocks.ToString(CultureInfo.InvariantCulture));
        line.Append(" rebar_used=").Append(snapshot.ReBarUsed.ToString(CultureInfo.InvariantCulture));
        line.Append(" rebar_cap=").Append(snapshot.ReBarCap.ToString(CultureInfo.InvariantCulture));
        line.Append(" rebar_misses=").Append(snapshot.ReBarMisses.ToString(CultureInfo.InvariantCulture));
        line.Append(" empty_blocks_freed=").Append(snapshot.EmptyBlocksFreed.ToString(CultureInfo.InvariantCulture));
        line.Append(" budget_ext=").Append(snapshot.BudgetExtension ? '1' : '0');
        line.Append(" class_bytes=");
        for (int i = 0; i < PoolClassCount; i++)
        {
            if (i > 0) line.Append(',');
            ulong bytes = snapshot.ClassBytes != null && i < snapshot.ClassBytes.Length ? snapshot.ClassBytes[i] : 0;
            line.Append(bytes.ToString(CultureInfo.InvariantCulture));
        }
        line.Append(" heaps=");
        int heaps = snapshot.HeapUsed?.Length ?? 0;
        for (int i = 0; i < heaps; i++)
        {
            if (i > 0) line.Append(',');
            line.Append(snapshot.HeapUsed![i].ToString(CultureInfo.InvariantCulture)).Append('/');
            ulong budget = snapshot.HeapBudget != null && i < snapshot.HeapBudget.Length ? snapshot.HeapBudget[i] : 0;
            line.Append(budget.ToString(CultureInfo.InvariantCulture));
        }
        line.Append(" heap_flags=");
        for (int i = 0; i < heaps; i++)
        {
            if (i > 0) line.Append(',');
            uint flags = snapshot.HeapFlags != null && i < snapshot.HeapFlags.Length
                ? snapshot.HeapFlags[i] : 0;
            line.Append("0x").Append(flags.ToString("X", CultureInfo.InvariantCulture));
        }
        line.Append(" driver_heap_usage=");
        if (!snapshot.BudgetExtension) line.Append("unavailable");
        else
        {
            for (int i = 0; i < heaps; i++)
            {
                if (i > 0) line.Append(',');
                ulong usage = snapshot.HeapDriverUsage != null && i < snapshot.HeapDriverUsage.Length
                    ? snapshot.HeapDriverUsage[i] : 0;
                line.Append(usage.ToString(CultureInfo.InvariantCulture));
            }
        }
        return line.ToString();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (ulong allocation in _allocations.Keys) _vma.Free(allocation);
            _allocations.Clear();
            _vma.Dispose();
            for (int i = 0; i < _knownBlocks; i++) VulkanMemory.NoteFree();
            _knownBlocks = 0;
            Array.Clear(_heaps);
            Array.Clear(_classes);
            _disposed = true;
        }
    }
}
