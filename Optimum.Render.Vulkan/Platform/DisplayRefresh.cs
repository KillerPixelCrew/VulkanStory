using System;
using OpenTK.Windowing.Desktop;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// Where the display refresh rate comes from. Behind an interface so tests inject a
/// rate; the client reads the window's monitor through OpenTK/GLFW. Nothing in the
/// backend queried a refresh rate before the paced present (the present-thread map
/// found no refresh query anywhere), and the pacer's seed interval and the paced frame
/// cap under FIFO both need it.
/// </summary>
internal interface IDisplayRefreshSource
{
    /// <summary>The current refresh rate in Hz, or 0 when it is unknown.</summary>
    int CurrentRefreshHz();
}

/// <summary>
/// The monitor the client window is on, as OpenTK reports it: the monitor GLFW's
/// window position falls on, and that monitor's current video mode. Must run on the
/// thread that owns GLFW (the client's main thread), which is where device bring-up
/// and the resize notification run.
/// </summary>
internal sealed class WindowMonitorRefreshSource : IDisplayRefreshSource
{
    private readonly NativeWindow? _window;

    public WindowMonitorRefreshSource(NativeWindow? window) => _window = window;

    public int CurrentRefreshHz() => Read(_window);

    /// <summary>The window's monitor refresh rate, 0 when there is no window or GLFW cannot say.</summary>
    public static int Read(NativeWindow? window)
    {
        if (window == null) return 0;
        try
        {
            MonitorInfo monitor = Monitors.GetMonitorFromWindow(window);
            return monitor == null ? 0 : monitor.CurrentVideoMode.RefreshRate;
        }
        catch (Exception)
        {
            // A window not yet realised, a monitor unplugged mid-query or a GLFW
            // without monitor support: unknown, which falls back to 60 Hz.
            return 0;
        }
    }
}

/// <summary>Turns a reported rate into the rate the pacer and the frame cap use.</summary>
internal static class DisplayRefresh
{
    /// <summary>Assumed when the monitor reports nothing plausible.</summary>
    public const int FallbackHz = 60;

    /// <summary>
    /// Above this a reported rate is garbage rather than a panel (the fastest shipping
    /// panels are 540 Hz); treated as unknown.
    /// </summary>
    public const int MaxPlausibleHz = 1000;

    /// <summary>
    /// <paramref name="reportedHz" /> when plausible, else <see cref="FallbackHz" />. The
    /// fallback is logged once per owner (<paramref name="fallbackLogged" />): the read
    /// runs on every resize, and a monitor that never reports would otherwise log on
    /// each one.
    /// </summary>
    public static int Resolve(int reportedHz, ref bool fallbackLogged, Action<string>? log)
    {
        if (reportedHz > 0 && reportedHz <= MaxPlausibleHz) return reportedHz;
        if (!fallbackLogged)
        {
            fallbackLogged = true;
            log?.Invoke("[Optimum] display refresh rate unknown (monitor reported " + reportedHz +
                " Hz); frame pacing assumes " + FallbackHz + " Hz");
        }
        return FallbackHz;
    }
}
