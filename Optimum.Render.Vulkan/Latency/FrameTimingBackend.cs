using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// One latency implementation behind the seams of the frame (plan section
/// "Latency seams", S2-S8). Exactly one instance is live on a device, reachable
/// as <c>VulkanDevice.Latency</c>; the default is <see cref="NoneLatencyBackend" />.
///
/// The frame, with the call sites of every member:
/// <code>
/// window_RenderFrame                 (lib)
///   if (!LatencyOwnsFrameCap) the client's FPS cap runs   &lt;- OwnsFrameCap
///   LatencySleep()                                        &lt;- Sleep(frameId)
///                                                            Marker(InputSample), Marker(SimulationStart)
///   UpdateMousePosition(); OnNewFrame(dt)
///     first BeginRenderStage(Before)                      &lt;- Marker(SimulationEnd), Marker(RenderSubmitStart)
///   EndFrame() -&gt; VulkanDevice.Present
///     FrameSlot.Submit (A, B, partial)                    &lt;- TagSubmit
///     after Submit A                                      &lt;- Marker(RenderSubmitEnd)
///     around Swapchain.Present                            &lt;- Marker(PresentStart/PresentEnd), OnPresent
///   Swapchain.Build                                       &lt;- OnSwapchainCreated
///   VulkanStats sample                                    &lt;- TakeReports
/// </code>
///
/// Implementations are used from the one client thread for everything except
/// <see cref="TagSubmit" />, which the upload path can reach under the ring's
/// submit lock, and <see cref="TakeReports" />, which the stats sample calls.
/// </summary>
internal interface ILatencyBackend : IDisposable
{
    /// <summary>Which implementation this is; goes into the "device up" log line and the stats.</summary>
    LatencyBackendKind Kind { get; }

    /// <summary>The settings last handed to <see cref="Apply" />.</summary>
    LatencySettings Settings { get; }

    /// <summary>
    /// Sets mode and frame cap. Called once when the device comes up, again
    /// whenever the client's setting changes, and again from
    /// <see cref="OnSwapchainCreated" />, because a new swapchain drops the
    /// driver's sleep mode.
    /// </summary>
    void Apply(in LatencySettings settings);

    /// <summary>
    /// True when this backend paces the frame itself, so the client's own FPS
    /// limiter must stand down (lib seam S3: the cap block in
    /// <c>window_RenderFrame</c> runs only when this is false). False for
    /// <see cref="NoneLatencyBackend" /> and for any backend whose mode is Off.
    /// </summary>
    bool OwnsFrameCap { get; }

    /// <summary>
    /// The frame's one sleep, called immediately before input is sampled, exactly
    /// once per frame. <paramref name="frameId" /> is the latency frame id
    /// allocated for this frame (seam S2).
    /// </summary>
    /// <returns>Microseconds actually waited; 0 when the backend did not sleep.</returns>
    ulong Sleep(ulong frameId);

    /// <summary>
    /// Stamps one phase marker of the frame. Called from the sites listed on this
    /// interface and nowhere else: every marker has exactly one owner, so no
    /// phase is ever stamped twice.
    /// </summary>
    void Marker(ulong frameId, LatencyMarker marker);

    /// <summary>
    /// A new swapchain exists (resize, vsync toggle, OUT_OF_DATE, present-mode
    /// promotion). Called from <c>Swapchain.Build</c> after the slot is live, so
    /// the backend can re-apply its sleep mode to the new handle.
    /// </summary>
    void OnSwapchainCreated(SwapchainKHR swapchain);

    /// <summary>
    /// The swapchain the backend was last told about has been retired (a rebuild
    /// passed it as oldSwapchain) or destroyed, and nothing must be called
    /// against that handle any more. Called from <c>Swapchain.Build</c> the
    /// moment the old slot is handed to the retirement queue - which happens even
    /// when the creation that replaces it fails, so the frames that keep running
    /// on a chain that could not be rebuilt make no vendor call at all - and from
    /// <c>Swapchain.Dispose</c>.
    ///
    /// A backend with no per-swapchain state does nothing; NV drops the handle,
    /// so its sleep, its markers and its timing query all stand down until the
    /// next <see cref="OnSwapchainCreated" />.
    /// </summary>
    void OnSwapchainRetired();

    /// <summary>
    /// Offers a pNext struct for one <c>vkQueueSubmit</c> of the frame (NV's
    /// <c>VkLatencySubmissionPresentIdNV</c> at extension revision 3 and up,
    /// where tagging is all-or-nothing across a frame's submits).
    ///
    /// Called from the shared <c>FrameSlot.Submit</c>, which Submit A, Submit B
    /// and SubmitPartial all pass through, with the chain the caller has already
    /// built; the return value becomes <c>SubmitInfo.PNext</c>. A backend that
    /// has nothing to add returns <paramref name="pNext" /> unchanged - which is
    /// why the caller needs to know nothing about the backend.
    ///
    /// Whatever is returned must stay valid until that submit has been made; a
    /// backend storing the struct keeps it in stable native memory, one slot per
    /// frame in flight.
    /// </summary>
    unsafe void* TagSubmit(ulong frameId, void* pNext);

    /// <summary>
    /// The frame has been presented: <paramref name="presentId" /> is the value
    /// chained as <c>VkPresentIdKHR</c> (0 when present ids are off). Called
    /// right after <c>vkQueuePresentKHR</c> returns, after the PresentEnd marker,
    /// and is where a CPU-timestamp backend closes the frame's report.
    /// </summary>
    void OnPresent(ulong frameId, ulong presentId);

    /// <summary>
    /// The reports finished since the last call, oldest first, and clears them.
    /// Called by the stats sample (seam S7); an empty array when nothing closed.
    /// </summary>
    LatencyFrameReport[] TakeReports();
}

/// <summary>
/// Which latency implementation is active. Exactly one at a time, chosen per
/// present path (plan section "Latency seams").
///
/// This branch carries the frame-marking foundation only (frame identity, markers,
/// present ids, the stats line), so <see cref="None" /> is the one implementation
/// that exists here. The other kinds are the pacing backends of <c>feat/latency</c>;
/// they are named so the stats and log tokens, and the selection that lands with
/// them, keep one vocabulary across both branches.
/// </summary>
internal enum LatencyBackendKind
{
    /// <summary>No sleeping, no vendor calls; CPU timestamps only. The default.</summary>
    None = 0,

    /// <summary>Completion pacing done in the renderer (<c>feat/latency</c>).</summary>
    Native = 1,

    /// <summary>VK_NV_low_latency2 (<c>feat/latency</c>).</summary>
    NvLowLatency2 = 2,

    /// <summary>VK_AMD_anti_lag (<c>feat/latency</c>).</summary>
    AmdAntiLag = 3,
}

/// <summary>The tokens a backend kind is written as, in the "device up" line and the stats sample.</summary>
internal static class LatencyBackends
{
    public static string Token(LatencyBackendKind kind) => kind switch
    {
        LatencyBackendKind.Native => "native",
        LatencyBackendKind.NvLowLatency2 => "nv",
        LatencyBackendKind.AmdAntiLag => "amd",
        _ => "off",
    };
}

/// <summary>
/// How hard the active latency backend works. The persisted setting that drives it
/// (<c>LatencyMode</c>, off|on|boost) arrives with the pacing backends on
/// <c>feat/latency</c>; on this branch every backend stays <see cref="Off" />.
/// </summary>
internal enum LatencyMode
{
    /// <summary>No sleeping and no markers beyond the CPU timestamps the reports need.</summary>
    Off = 0,

    /// <summary>The backend paces the frame (NV: lowLatencyMode; AMD: anti-lag on; Native: completion pacing).</summary>
    On = 1,

    /// <summary>
    /// As <see cref="On" />, plus the vendor's clock boost while the CPU is the
    /// bottleneck (NV: lowLatencyBoost). Backends without a boost treat it as
    /// <see cref="On" />.
    /// </summary>
    Boost = 2,
}

/// <summary>
/// What the client asks of the latency backend: one mode and one frame cap.
///
/// The cap is named after <c>VkLatencySleepModeInfoNV.minimumIntervalUs</c> - the
/// minimum interval between two frame starts - because every backend expresses
/// the same thing: NV passes it through, AMD converts it to <c>maxFPS</c>, the
/// Native backend paces release to release, and XeLL later takes it in
/// <c>xellSetSleepMode</c>. One cap for every backend, so the client's own FPS
/// limiter can stand down whenever <c>ILatencyBackend.OwnsFrameCap</c> is true.
/// </summary>
/// <param name="Mode">Off, On or Boost.</param>
/// <param name="MinimumIntervalUs">
/// Minimum microseconds between two frame starts; 0 is uncapped. 60 fps is 16666.
/// </param>
internal readonly record struct LatencySettings(LatencyMode Mode, ulong MinimumIntervalUs)
{
    /// <summary>The default: no latency work at all.</summary>
    public static LatencySettings Disabled => new(LatencyMode.Off, 0);

    /// <summary>True when the backend should sleep and stamp markers.</summary>
    public bool Enabled => Mode != LatencyMode.Off;

    /// <summary>True when the vendor's clock boost is asked for.</summary>
    public bool Boost => Mode == LatencyMode.Boost;

    /// <summary>0 when uncapped, else the cap expressed as frames per second (rounded down).</summary>
    public uint MaxFps => MinimumIntervalUs == 0 ? 0u : (uint)(1_000_000UL / MinimumIntervalUs);

    /// <summary>The cap that gives at most <paramref name="fps" /> frames a second; 0 is uncapped.</summary>
    public static ulong IntervalUsForFps(double fps) =>
        fps <= 0 ? 0UL : (ulong)(1_000_000.0 / fps);
}

/// <summary>
/// The default backend: no sleeping, no vendor calls, no swapchain pNext. It
/// only records the markers as CPU timestamps, so the stats line has the frame
/// breakdown even with latency reduction off, and so "off is off" is checkable -
/// the OpenGL path and this one add exactly the same amount of pacing, none.
///
/// <see cref="OwnsFrameCap" /> is false, so the client's own FPS limiter keeps
/// running (lib seam S3).
/// </summary>
internal sealed class NoneLatencyBackend : ILatencyBackend
{
    private readonly LatencyPhaseTracker _tracker;

    /// <summary>
    /// The finished reports, in a fixed ring: the stats sample may never run, and
    /// a full buffer must not cost the present path anything (see
    /// <see cref="LatencyReportBuffer" />).
    /// </summary>
    private readonly LatencyReportBuffer _reports = new();

    public NoneLatencyBackend(Action<string>? log = null) => _tracker = new LatencyPhaseTracker(log);

    public LatencyBackendKind Kind => LatencyBackendKind.None;

    public LatencySettings Settings { get; private set; } = LatencySettings.Disabled;

    /// <summary>Remembered only so the stats line can print what was asked for; nothing acts on it.</summary>
    public void Apply(in LatencySettings settings) => Settings = settings;

    public bool OwnsFrameCap => false;

    /// <summary>Never sleeps.</summary>
    public ulong Sleep(ulong frameId) => 0;

    public void Marker(ulong frameId, LatencyMarker marker) =>
        _tracker.Mark(frameId, marker, LatencyClock.NowUs());

    /// <summary>Nothing to re-apply.</summary>
    public void OnSwapchainCreated(SwapchainKHR swapchain)
    {
    }

    /// <summary>Nothing was bound to the swapchain, so nothing is dropped with it.</summary>
    public void OnSwapchainRetired()
    {
    }

    /// <summary>Adds nothing to the submit chain.</summary>
    public unsafe void* TagSubmit(ulong frameId, void* pNext) => pNext;

    public void OnPresent(ulong frameId, ulong presentId)
    {
        if (!_tracker.TryComplete(frameId, presentId, out LatencyFrameReport report)) return;
        _reports.Add(report);
    }

    public LatencyFrameReport[] TakeReports() => _reports.Take();

    public void Dispose()
    {
    }
}
