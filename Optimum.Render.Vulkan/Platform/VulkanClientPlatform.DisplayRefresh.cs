using OpenTK.Windowing.Desktop;

namespace Optimum.Render.Vulkan.Platform;

// Paced present: the display refresh rate, read from the window's monitor at device
// bring-up and on every resize (a resize is also what a move to another monitor or a
// fullscreen switch produces). The pacer seeds its interval from it and the paced frame
// cap under FIFO is two of its intervals.
public partial class VulkanClientPlatform
{
    /// <summary>Test seam: the refresh source. Null reads the client window's monitor.</summary>
    internal IDisplayRefreshSource? DisplayRefreshSourceOverride;

    /// <summary>
    /// The last resolved rate, 0 before the first read. Written on the main thread (bring-up,
    /// resize) and read by the present-thread wiring: volatile, a single int, so a reader
    /// sees either the old or the new rate and never a torn one.
    /// </summary>
    private volatile int displayRefreshHz;

    /// <summary>Main thread only: the unknown-rate line has been logged.</summary>
    private bool displayRefreshFallbackLogged;

    /// <summary>The display refresh rate in Hz; <see cref="DisplayRefresh.FallbackHz" /> before the first read.</summary>
    internal int DisplayRefreshHz
    {
        get
        {
            int hz = displayRefreshHz;
            return hz != 0 ? hz : DisplayRefresh.FallbackHz;
        }
    }

    /// <summary>Reads and resolves the refresh rate; unknown resolves to 60 Hz with one log line.</summary>
    internal void ReadDisplayRefreshRate()
    {
        IDisplayRefreshSource? source = DisplayRefreshSourceOverride;
        int reported = source != null ? source.CurrentRefreshHz() : WindowMonitorRefreshSource.Read(window as NativeWindow);
        displayRefreshHz = DisplayRefresh.Resolve(reported, ref displayRefreshFallbackLogged, LogDisplayRefresh);
    }

    private void LogDisplayRefresh(string line) => Logger?.Notification(line);
}
