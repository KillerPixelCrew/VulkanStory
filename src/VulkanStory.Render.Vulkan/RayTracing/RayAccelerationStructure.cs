using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan.RayTracing;

/// <summary>An acceleration structure, its device-local storage and aligned build workspace.</summary>
internal sealed unsafe class RayAccelerationStructure : IDisposable
{
    private readonly VulkanContext context;
    private readonly KhrAccelerationStructure api;
    private VulkanBuffer? storage, scratch;
    private bool disposed;
    internal AccelerationStructureKHR Handle { get; private set; }
    internal ulong Address { get; private set; }
    internal ulong Capacity { get; }
    private readonly ulong scratchAddress;

    internal RayAccelerationStructure(VulkanContext context, KhrAccelerationStructure api,
        AccelerationStructureTypeKHR type, in AccelerationStructureBuildSizesInfoKHR sizes, uint scratchAlignment)
    {
        this.context = context;
        this.api = api;
        Capacity = sizes.AccelerationStructureSize;
        try
        {
            storage = new VulkanBuffer(context, Math.Max(Capacity, 256),
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
            ulong workspace = Math.Max(sizes.BuildScratchSize, sizes.UpdateScratchSize);
            ulong alignment = Math.Max(1u, scratchAlignment);
            scratch = new VulkanBuffer(context, checked(Math.Max(workspace, 1) + alignment - 1),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
            ulong address = BufferAddress(context, scratch);
            scratchAddress = checked((address + alignment - 1) / alignment * alignment);
            var create = new AccelerationStructureCreateInfoKHR
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = storage.Handle,
                Size = Capacity,
                Type = type,
            };
            AccelerationStructureKHR handle;
            VulkanResult.Check(api.CreateAccelerationStructure(context.Device, &create, null, &handle),
                "vkCreateAccelerationStructureKHR");
            Handle = handle;
            var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
            {
                SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
                AccelerationStructure = handle,
            };
            Address = api.GetAccelerationStructureDeviceAddress(context.Device, &addressInfo);
            if (Address == 0) throw new InvalidOperationException("Acceleration structure has no device address.");
        }
        catch { Dispose(); throw; }
    }

    internal void Build(CommandBuffer commands, AccelerationStructureBuildGeometryInfoKHR info,
        AccelerationStructureBuildRangeInfoKHR range)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (scratch == null) throw new InvalidOperationException("The one-time acceleration build workspace was retired.");
        info.DstAccelerationStructure = Handle;
        info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratchAddress };
        AccelerationStructureBuildRangeInfoKHR* ranges = &range;
        api.CmdBuildAccelerationStructures(commands, 1, &info, &ranges);
    }

    internal void RetireBuildWorkspace(FrameRing frames)
    {
        if (scratch is not { } workspace) return;
        scratch = null;
        frames.DeferDeletion(workspace);
    }

    internal static AccelerationStructureBuildSizesInfoKHR Sizes(VulkanContext context,
        KhrAccelerationStructure api, AccelerationStructureBuildGeometryInfoKHR info, uint primitives)
    {
        var sizes = new AccelerationStructureBuildSizesInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildSizesInfoKhr,
        };
        api.GetAccelerationStructureBuildSizes(context.Device, AccelerationStructureBuildTypeKHR.DeviceKhr,
            &info, &primitives, &sizes);
        if (sizes.AccelerationStructureSize == 0)
            throw new InvalidOperationException("Acceleration build returned an empty storage requirement.");
        return sizes;
    }

    internal static ulong BufferAddress(VulkanContext context, VulkanBuffer buffer)
    {
        var info = new BufferDeviceAddressInfo
        {
            SType = StructureType.BufferDeviceAddressInfo,
            Buffer = buffer.Handle,
        };
        ulong address = context.Api.GetBufferDeviceAddress(context.Device, &info);
        if (address == 0) throw new InvalidOperationException("Ray geometry buffer has no device address.");
        return address;
    }

    internal static void Barrier(VulkanContext context, CommandBuffer commands,
        PipelineStageFlags2 sourceStage, AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage, AccessFlags2 destinationAccess)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = sourceStage, SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage, DstAccessMask = destinationAccess,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1, PMemoryBarriers = &barrier,
        };
        context.Api.CmdPipelineBarrier2(commands, &dependency);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Handle.Handle != 0) api.DestroyAccelerationStructure(context.Device, Handle, null);
        Handle = default; Address = 0;
        scratch?.Dispose(); scratch = null;
        storage?.Dispose(); storage = null;
    }
}
