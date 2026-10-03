using System;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Render.Vulkan.Core;

internal interface IVulkanWindowSurface
{
    IntPtr NativeHandle { get; }
    nint Win32Handle { get; }
    string[] RequiredInstanceExtensions();
    bool TryCreate(VulkanContext context, out SurfaceKHR surface, out string? failureReason);
}

internal sealed class SdlVulkanWindowSurface(SdlWindowHost window) : IVulkanWindowSurface
{
    public IntPtr NativeHandle => window.NativeHandle;
    public nint Win32Handle => window.Win32Handle;
    public string[] RequiredInstanceExtensions() => window.RequiredInstanceExtensions();

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
