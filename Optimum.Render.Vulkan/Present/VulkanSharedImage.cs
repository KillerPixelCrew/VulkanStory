using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// A D3D12 committed resource imported as a dedicated Vulkan image. Both APIs
/// refer to the same allocation. The caller must synchronize GPU ownership
/// before either API reads or writes it.
/// </summary>
internal sealed unsafe class VulkanSharedImage : IDisposable
{
    private const ExternalMemoryHandleTypeFlags SharedResource =
        ExternalMemoryHandleTypeFlags.D3D12ResourceBit;
    private const uint ExternalQueueFamily = uint.MaxValue - 1;
    private readonly VulkanContext _context;
    private readonly XessFgRuntime _runtime;
    private readonly KhrExternalMemoryWin32 _external;
    private bool _disposed;
    private bool _releasedToDx12;

    private VulkanSharedImage(VulkanContext context, XessFgRuntime runtime,
        KhrExternalMemoryWin32 external, Image image, DeviceMemory memory,
        nint handle, nint resource, uint width, uint height, Format format)
    {
        _context = context;
        _runtime = runtime;
        _external = external;
        Image = image;
        Memory = memory;
        SharedHandle = handle;
        D3D12Resource = resource;
        Width = width;
        Height = height;
        Format = format;
    }

    public Image Image { get; }
    public DeviceMemory Memory { get; }
    public nint SharedHandle { get; }
    public nint D3D12Resource { get; }
    public uint Width { get; }
    public uint Height { get; }
    public Format Format { get; }

    /// <summary>Reports whether a source can be flipped into this shared image.</summary>
    public bool CanBlitFrom(VulkanTexture source, out string reason)
    {
        if (source.Width != Width || source.Height != Height ||
            (source.Usage & ImageUsageFlags.TransferSrcBit) == 0)
        {
            reason = "source extent or transfer usage does not match XeSS-FG image";
            return false;
        }
        bool depth = Format == Format.D32Sfloat;
        bool colorFormatMatches = source.Format == Format ||
            (source.Format is Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm &&
             Format is Format.R8G8B8A8Unorm or Format.B8G8R8A8Unorm);
        if (depth ? source.Format != Format.D32Sfloat ||
                source.Aspect != ImageAspectFlags.DepthBit :
            source.Aspect != ImageAspectFlags.ColorBit || !colorFormatMatches)
        {
            reason = "source aspect or format is incompatible with XeSS-FG image";
            return false;
        }
        _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice,
            source.Format, out FormatProperties sourceProperties);
        _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice,
            Format, out FormatProperties destinationProperties);
        if ((sourceProperties.OptimalTilingFeatures & FormatFeatureFlags.BlitSrcBit) == 0 ||
            (destinationProperties.OptimalTilingFeatures & FormatFeatureFlags.BlitDstBit) == 0)
        {
            reason = "source or shared format does not support Vulkan image blits";
            return false;
        }
        reason = "ready";
        return true;
    }

    /// <summary>
    /// Source must already be in TRANSFER_SRC_OPTIMAL. The DX12 queue must wait
    /// on the semaphore value signalled by the submission containing this copy.
    /// On reuse, that submission must first wait for DX12 to return ownership.
    /// </summary>
    public void RecordFlippedCopy(CommandBuffer commands, VulkanTexture source)
    {
        if (!CanBlitFrom(source, out string reason))
            throw new InvalidOperationException(reason);
        ImageAspectFlags aspect = Format == Format.D32Sfloat ?
            ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
        var acquire = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.None,
            SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.TransferBit,
            DstAccessMask = AccessFlags2.TransferWriteBit,
            OldLayout = _releasedToDx12 ? ImageLayout.General : ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = ExternalQueueFamily,
            DstQueueFamilyIndex = _context.GraphicsQueueFamily,
            Image = Image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &acquire,
        };
        _context.Api.CmdPipelineBarrier2(commands, &dependency);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(source.Aspect, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
        };
        blit.SrcOffsets.Element0 = new Offset3D(0, (int)Height, 0);
        blit.SrcOffsets.Element1 = new Offset3D((int)Width, 0, 1);
        blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
        blit.DstOffsets.Element1 = new Offset3D((int)Width, (int)Height, 1);
        _context.Api.CmdBlitImage(commands, source.Image, ImageLayout.TransferSrcOptimal,
            Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);

        var release = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.None,
            DstAccessMask = AccessFlags2.None,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = _context.GraphicsQueueFamily,
            DstQueueFamilyIndex = ExternalQueueFamily,
            Image = Image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        dependency.PImageMemoryBarriers = &release;
        _context.Api.CmdPipelineBarrier2(commands, &dependency);
        _releasedToDx12 = true;
    }

    public static bool TryCreate(VulkanContext context, XessFgRuntime runtime,
        uint width, uint height, Format format, out VulkanSharedImage? result,
        out string reason)
    {
        result = null;
        if (!OperatingSystem.IsWindows() || width == 0 || height == 0)
        {
            reason = "external D3D12 images require Windows and a nonzero extent";
            return false;
        }
        if (!context.Api.TryGetDeviceExtension(context.Instance, context.Device,
                out KhrExternalMemoryWin32 external))
        {
            reason = "VK_KHR_external_memory_win32 was not enabled";
            return false;
        }

        Vk api = context.Api;
        Image image = default;
        DeviceMemory memory = default;
        nint handle = 0;
        nint resource = 0;
        try
        {
            var externalFormat = new PhysicalDeviceExternalImageFormatInfo
            {
                SType = StructureType.PhysicalDeviceExternalImageFormatInfo,
                HandleType = SharedResource,
            };
            var formatInfo = new PhysicalDeviceImageFormatInfo2
            {
                SType = StructureType.PhysicalDeviceImageFormatInfo2,
                PNext = &externalFormat,
                Format = format,
                Type = ImageType.Type2D,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            };
            var externalProperties = new ExternalImageFormatProperties
            {
                SType = StructureType.ExternalImageFormatProperties,
            };
            var formatProperties = new ImageFormatProperties2
            {
                SType = StructureType.ImageFormatProperties2,
                PNext = &externalProperties,
            };
            VulkanResult.Check(api.GetPhysicalDeviceImageFormatProperties2(context.PhysicalDevice,
                    &formatInfo, &formatProperties),
                "vkGetPhysicalDeviceImageFormatProperties2 for XeSS-FG sharing");
            if ((externalProperties.ExternalMemoryProperties.ExternalMemoryFeatures &
                 ExternalMemoryFeatureFlags.ImportableBit) == 0 ||
                (externalProperties.ExternalMemoryProperties.CompatibleHandleTypes &
                 SharedResource) == 0)
                throw new InvalidOperationException("D3D12 resource import is unsupported for " + format);

            int createResult = runtime.CreateSharedImage(width, height, format,
                out handle, out resource);
            if (createResult != 0 || handle == 0 || resource == 0)
                throw new InvalidOperationException("D3D12 shared resource creation failed (" +
                    createResult.ToString("X8") + ")");

            var sharing = new ExternalMemoryImageCreateInfo
            {
                SType = StructureType.ExternalMemoryImageCreateInfo,
                HandleTypes = SharedResource,
            };
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                PNext = &sharing,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            VulkanResult.Check(api.CreateImage(context.Device, &imageInfo, null, out image),
                "vkCreateImage for XeSS-FG sharing");
            api.GetImageMemoryRequirements(context.Device, image, out MemoryRequirements requirements);
            var handleProperties = new MemoryWin32HandlePropertiesKHR
            {
                SType = StructureType.MemoryWin32HandlePropertiesKhr,
            };
            VulkanResult.Check(external.GetMemoryWin32HandleProperties(context.Device,
                    SharedResource, handle, &handleProperties),
                "vkGetMemoryWin32HandlePropertiesKHR for XeSS-FG sharing");
            api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice,
                out PhysicalDeviceMemoryProperties memoryProperties);
            uint memoryType = uint.MaxValue;
            for (uint index = 0; index < memoryProperties.MemoryTypeCount; index++)
            {
                if ((requirements.MemoryTypeBits & (1u << (int)index)) != 0 &&
                    (handleProperties.MemoryTypeBits & (1u << (int)index)) != 0 &&
                    (memoryProperties.MemoryTypes[(int)index].PropertyFlags &
                     MemoryPropertyFlags.DeviceLocalBit) != 0)
                {
                    memoryType = index;
                    break;
                }
            }
            if (memoryType == uint.MaxValue)
                throw new InvalidOperationException("no compatible device-local D3D12 memory type");
            var import = new ImportMemoryWin32HandleInfoKHR
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                HandleType = SharedResource,
                Handle = handle,
            };
            var dedicated = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                PNext = &import,
                Image = image,
            };
            var allocation = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &dedicated,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryType,
            };
            VulkanResult.Check(api.AllocateMemory(context.Device, &allocation, null, out memory),
                "vkAllocateMemory for XeSS-FG sharing");
            VulkanResult.Check(api.BindImageMemory(context.Device, image, memory, 0),
                "vkBindImageMemory for XeSS-FG sharing");
            result = new VulkanSharedImage(context, runtime, external, image, memory,
                handle, resource, width, height, format);
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            if (handle != 0) CloseHandle(handle);
            if (image.Handle != 0) api.DestroyImage(context.Device, image, null);
            if (memory.Handle != 0) api.FreeMemory(context.Device, memory, null);
            if (resource != 0) runtime.ReleaseImage(resource);
            external.Dispose();
            reason = "Vulkan/DX12 shared image failed: " + error.Message;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (SharedHandle != 0) CloseHandle(SharedHandle);
        _context.Api.DestroyImage(_context.Device, Image, null);
        _context.Api.FreeMemory(_context.Device, Memory, null);
        _runtime.ReleaseImage(D3D12Resource);
        _external.Dispose();
    }
}
