using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// A D3D12 shared fence permanently imported as a Vulkan timeline semaphore.
/// Queue submissions use monotonically increasing values to transfer ownership
/// of XeSS-FG's shared images without a per-frame CPU wait.
/// </summary>
internal sealed unsafe class XessSharedFence : IDisposable
{
    private const ExternalSemaphoreHandleTypeFlags FenceHandle =
        ExternalSemaphoreHandleTypeFlags.D3D12FenceBit;
    private readonly VulkanContext _context;
    private readonly KhrExternalSemaphoreWin32 _external;
    private bool _disposed;

    private XessSharedFence(VulkanContext context, KhrExternalSemaphoreWin32 external,
        Semaphore semaphore)
    {
        _context = context;
        _external = external;
        Semaphore = semaphore;
    }

    public Semaphore Semaphore { get; }

    public static bool TryCreate(VulkanContext context, XessFgRuntime runtime,
        out XessSharedFence? result, out string reason)
    {
        result = null;
        if (!OperatingSystem.IsWindows() ||
            !context.Api.TryGetDeviceExtension(context.Instance, context.Device,
                out KhrExternalSemaphoreWin32 external))
        {
            reason = "VK_KHR_external_semaphore_win32 is unavailable";
            return false;
        }

        Semaphore semaphore = default;
        nint handle = 0;
        try
        {
            var timeline = new SemaphoreTypeCreateInfo
            {
                SType = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = SemaphoreType.Timeline,
            };
            var query = new PhysicalDeviceExternalSemaphoreInfo
            {
                SType = StructureType.PhysicalDeviceExternalSemaphoreInfo,
                PNext = &timeline,
                HandleType = FenceHandle,
            };
            var properties = new ExternalSemaphoreProperties
            {
                SType = StructureType.ExternalSemaphoreProperties,
            };
            context.Api.GetPhysicalDeviceExternalSemaphoreProperties(context.PhysicalDevice,
                &query, &properties);
            if ((properties.ExternalSemaphoreFeatures &
                 ExternalSemaphoreFeatureFlags.ImportableBit) == 0)
                throw new InvalidOperationException("D3D12 fence timeline import is unsupported");

            int create = runtime.CreateSharedFence(out handle);
            if (create != 0 || handle == 0)
                throw new InvalidOperationException("D3D12 shared fence creation failed (" +
                    create.ToString("X8") + ")");

            var createInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
                PNext = &timeline,
            };
            VulkanResult.Check(context.Api.CreateSemaphore(context.Device, &createInfo,
                null, out semaphore), "vkCreateSemaphore for XeSS-FG shared fence");
            var import = new ImportSemaphoreWin32HandleInfoKHR
            {
                SType = StructureType.ImportSemaphoreWin32HandleInfoKhr,
                Semaphore = semaphore,
                HandleType = FenceHandle,
                Handle = handle,
            };
            VulkanResult.Check(external.ImportSemaphoreWin32Handle(context.Device, &import),
                "vkImportSemaphoreWin32HandleKHR for XeSS-FG shared fence");
            CloseHandle(handle);
            handle = 0;
            result = new XessSharedFence(context, external, semaphore);
            reason = "ready";
            return true;
        }
        catch (Exception error)
        {
            if (handle != 0) CloseHandle(handle);
            if (semaphore.Handle != 0)
                context.Api.DestroySemaphore(context.Device, semaphore, null);
            external.Dispose();
            reason = "Vulkan/DX12 shared fence failed: " + error.Message;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Api.DestroySemaphore(_context.Device, Semaphore, null);
        _external.Dispose();
    }
}
