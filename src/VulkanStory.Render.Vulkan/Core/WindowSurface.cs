using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Window-owned native handles and surface-creation operations consumed by the Vulkan backend.</summary>
internal interface IVulkanWindowSurface
{
    /// <summary>Borrowed native window pointer used by the platform host.</summary>
    IntPtr NativeHandle { get; }
    /// <summary>Borrowed Win32 HWND, or zero when the window has no Win32 handle.</summary>
    nint Win32Handle { get; }
    /// <summary>Returns the instance extensions required to create this window's Vulkan surface.</summary>
    string[] RequiredInstanceExtensions();
    /// <summary>Creates a Vulkan surface for the borrowed window using the selected native/proxy dispatch.</summary>
    /// <param name="context">Borrowed instance owner.</param>
    /// <param name="surface">Created surface on success; its lifetime belongs to the presentation owner.</param>
    /// <param name="failureReason">Failure detail, or null on success.</param>
    /// <returns>Whether surface creation succeeded.</returns>
    bool TryCreate(VulkanContext context, out SurfaceKHR surface, out string? failureReason);
}

/// <summary>Borrows an SDL window and adapts its required extensions and surface creation to the Vulkan context.</summary>
internal sealed class SdlVulkanWindowSurface(SdlWindowHost window) : IVulkanWindowSurface
{
    /// <inheritdoc/>
    public IntPtr NativeHandle => window.NativeHandle;
    /// <inheritdoc/>
    public nint Win32Handle => window.Win32Handle;
    /// <inheritdoc/>
    public string[] RequiredInstanceExtensions() => window.RequiredInstanceExtensions();

    /// <inheritdoc/>
    public bool TryCreate(VulkanContext context, out SurfaceKHR surface, out string? failureReason)
    {
        surface = default;
        if (context.Streamline != null && OperatingSystem.IsWindows())
            return WindowSurface.TryCreateStreamlineWin32(context, window.Win32Handle,
                out surface, out failureReason);
        if (!window.TryCreateVulkanSurface((nint)context.Instance.Handle, out ulong handle,
                out failureReason)) return false;
        surface = new SurfaceKHR(handle);
        return true;
    }
}

/// <summary>
/// Surface cleanup and the optional Streamline Win32 surface path. The normal
/// window source is SDL3; no GLFW window or OpenGL context is created here.
/// </summary>
internal static unsafe class WindowSurface
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    /// <summary>
    /// Destroys a surface that never reached a <see cref="Swapchain"/>. The
    /// swapchain owns the surface once it exists, so this is only for the
    /// failure paths between creation and hand-over; the instance must not be
    /// destroyed with a surface still alive under it.
    /// </summary>
    public static void Destroy(VulkanContext context, SurfaceKHR surface)
    {
        if (surface.Handle == 0) return;
        if (context.Streamline != null)
        {
            context.Streamline.DestroySurface(context.Instance, surface);
            return;
        }
        if (!context.Api.TryGetInstanceExtension(context.Instance, out KhrSurface surfaceApi)) return;
        surfaceApi.DestroySurface(context.Instance, surface, null);
        surfaceApi.Dispose();
    }

    /// <summary>Creates a Win32 Vulkan surface through the Streamline proxy when available.</summary>
    internal static bool TryCreateStreamlineWin32(
        VulkanContext context, nint hwnd, out SurfaceKHR surface, out string? failureReason)
    {
        surface = default;
        failureReason = null;
        if (hwnd == 0 || context.Streamline == null || !OperatingSystem.IsWindows())
        {
            failureReason = "no Win32 window or Streamline surface provider";
            return false;
        }
        var info = new Win32SurfaceCreateInfoKHR
        {
            SType = StructureType.Win32SurfaceCreateInfoKhr,
            Hinstance = GetModuleHandleW(null),
            Hwnd = hwnd,
        };
        Result result = context.Streamline.CreateSurface(context.Instance, &info, out surface);
        if (result == Result.Success) return true;
        failureReason = "Streamline vkCreateWin32SurfaceKHR failed: " + result;
        return false;
    }
}
