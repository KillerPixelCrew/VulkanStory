using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Optimum.Render.Vulkan.Core;

internal interface IVulkanWindowSurface
{
    IntPtr NativeHandle { get; }
    nint Win32Handle { get; }
    string[] RequiredInstanceExtensions();
    bool TryCreate(VulkanContext context, out SurfaceKHR surface, out string? failureReason);
}

internal sealed class GlfwVulkanWindowSurface(IntPtr window) : IVulkanWindowSurface
{
    public IntPtr NativeHandle => window;
    public nint Win32Handle => WindowSurface.Win32Handle(window);
    public string[] RequiredInstanceExtensions() => WindowSurface.RequiredInstanceExtensions();
    public bool TryCreate(VulkanContext context, out SurfaceKHR surface, out string? failureReason) =>
        WindowSurface.TryCreate(context, window, out surface, out failureReason);
}

/// <summary>
/// GLFW window adapter and shared Win32 surface creation for the Vulkan renderer.
///
/// The client already opens its window through OpenTK's GLFW bindings, so the
/// surface is created from the same window pointer rather than by standing up a
/// second windowing stack. The only change the client needs is to ask for
/// <c>ContextAPI.NoAPI</c> so GLFW does not create an OpenGL context alongside.
/// </summary>
internal static unsafe class WindowSurface
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
    public static nint Win32Handle(IntPtr glfwWindow) =>
        OperatingSystem.IsWindows() && glfwWindow != IntPtr.Zero
            ? GLFW.GetWin32Window((Window*)glfwWindow) : 0;
    /// <summary>Whether a Vulkan loader is reachable at all.</summary>
    public static bool VulkanSupported()
    {
        try
        {
            return GLFW.VulkanSupported();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The instance extensions the platform needs before a surface can be made.
    /// These must be enabled at instance creation, which is why they are asked
    /// for before the context exists.
    /// </summary>
    public static string[] RequiredInstanceExtensions()
    {
        try
        {
            return GLFW.GetRequiredInstanceExtensions() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

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

    /// <summary>Creates a surface from the client's GLFW window.</summary>
    public static bool TryCreate(
        VulkanContext context, IntPtr windowHandle, out SurfaceKHR surface, out string? failureReason)
    {
        surface = default;
        failureReason = null;

        if (windowHandle == IntPtr.Zero)
        {
            failureReason = "no window handle";
            return false;
        }

        try
        {
            if (context.Streamline != null && OperatingSystem.IsWindows())
            {
                return TryCreateStreamlineWin32(context, GLFW.GetWin32Window((Window*)windowHandle),
                    out surface, out failureReason);
            }
            var instanceHandle = new VkHandle(context.Instance.Handle);
            int result = GLFW.CreateWindowSurface(
                instanceHandle, (Window*)windowHandle, null, out VkHandle surfaceHandle);

            if (result != 0)
            {
                failureReason = "glfwCreateWindowSurface failed with " + result;
                return false;
            }

            surface = new SurfaceKHR((ulong)surfaceHandle.Handle);
            return true;
        }
        catch (Exception error)
        {
            failureReason = "surface creation threw: " + error.Message;
            return false;
        }
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
