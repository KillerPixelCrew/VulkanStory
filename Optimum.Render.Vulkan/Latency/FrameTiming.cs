using System;
using System.Diagnostics;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// One frame's latency breakdown, in microseconds: the eight intervals every
/// vendor tool reports (NV's <c>VkLatencyTimingsFrameReportNV</c> is the widest
/// of them, and this is its shape).
///
/// Backends with a driver report (NV) fill every field from
/// <c>vkGetLatencyTimingsNV</c>. Backends without one (None, Native, AMD) fill
/// the CPU-observable intervals from their own marker timestamps through
/// <see cref="FromCpuTimestamps" /> and leave <see cref="DriverUs" />,
/// <see cref="OsRenderQueueUs" /> and <see cref="GpuUs" /> at zero; the stats
/// line (seam S7) prints what is there.
/// </summary>
/// <param name="FrameId">The latency frame id allocated at the sleep (seam S2).</param>
/// <param name="PresentId">The present id chained as <c>VkPresentIdKHR</c>, or 0 when the frame never presented.</param>
/// <param name="InputUs">Input sample to simulation start.</param>
/// <param name="SimulationUs">Simulation start to simulation end.</param>
/// <param name="RenderSubmitUs">Render submit start to render submit end.</param>
/// <param name="PresentUs">Present start to present end (the <c>vkQueuePresentKHR</c> call itself).</param>
/// <param name="DriverUs">Driver start to driver end; 0 without a driver report.</param>
/// <param name="OsRenderQueueUs">OS render queue start to end; 0 without a driver report.</param>
/// <param name="GpuUs">GPU render start to end; 0 without a driver report.</param>
/// <param name="TotalUs">Input sample to present end: the whole frame as the player feels it.</param>
internal readonly record struct LatencyFrameReport(
    ulong FrameId,
    ulong PresentId,
    ulong InputUs,
    ulong SimulationUs,
    ulong RenderSubmitUs,
    ulong PresentUs,
    ulong DriverUs,
    ulong OsRenderQueueUs,
    ulong GpuUs,
    ulong TotalUs)
{
    /// <summary>
    /// Builds a report from the CPU timestamps a backend without a driver report
    /// collected, all on one monotonic clock in microseconds (see
    /// <see cref="LatencyClock" />). A missing marker is passed as 0 and makes the
    /// intervals that need it 0; intervals never go negative.
    /// </summary>
    public static LatencyFrameReport FromCpuTimestamps(
        ulong frameId,
        ulong presentId,
        long inputSampleUs,
        long simulationStartUs,
        long simulationEndUs,
        long renderSubmitStartUs,
        long renderSubmitEndUs,
        long presentStartUs,
        long presentEndUs)
    {
        long start = inputSampleUs != 0 ? inputSampleUs : simulationStartUs;
        return new LatencyFrameReport(
            frameId,
            presentId,
            Span(inputSampleUs, simulationStartUs),
            Span(simulationStartUs, simulationEndUs),
            Span(renderSubmitStartUs, renderSubmitEndUs),
            Span(presentStartUs, presentEndUs),
            0,
            0,
            0,
            Span(start, presentEndUs));
    }

    /// <summary>0 when either end is missing or the pair is out of order; the difference otherwise.</summary>
    private static ulong Span(long fromUs, long toUs) =>
        fromUs <= 0 || toUs <= 0 || toUs <= fromUs ? 0UL : (ulong)(toUs - fromUs);
}

/// <summary>
/// The frame phases every vendor latency tool knows about.
///
/// The values are the values of <c>VkLatencyMarkerNV</c> (Silk.NET's
/// <c>LatencyMarkerNV</c>), so the NV backend can cast this straight into
/// <c>vkSetLatencyMarkerNV</c> without a translation table; a unit test pins
/// every one of them against Silk.NET. The other backends use the same set:
/// AMD's anti-lag has only INPUT and PRESENT, and the None backend records all
/// of them as CPU timestamps.
///
/// Where each one is stamped in an Optimum frame (plan section "Latency seams",
/// seams S3 and S4; the renderer owns all of them, never double-stamped):
/// <list type="bullet">
/// <item><description><see cref="InputSample" /> and <see cref="SimulationStart" />:
/// in <c>VulkanClientPlatform.LatencySleep</c>, right after the sleep returns and
/// immediately before the client gathers the mouse delta.</description></item>
/// <item><description><see cref="SimulationEnd" /> and <see cref="RenderSubmitStart" />:
/// on the first <c>BeginRenderStage(Before)</c> of the frame.</description></item>
/// <item><description><see cref="RenderSubmitEnd" />: after Submit A in
/// <c>VulkanDevice.Present</c>.</description></item>
/// <item><description><see cref="PresentStart" /> / <see cref="PresentEnd" />:
/// around <c>Swapchain.Present</c>.</description></item>
/// <item><description>The OutOfBand markers: reserved for submissions outside the
/// frame loop (standalone uploads, async present paths). Nothing stamps them yet.</description></item>
/// </list>
/// </summary>
internal enum LatencyMarker
{
    /// <summary>The client's simulation tick starts (after the sleep and the input sample).</summary>
    SimulationStart = 0,

    /// <summary>The simulation tick ends; rendering begins.</summary>
    SimulationEnd = 1,

    /// <summary>The renderer starts recording and submitting the frame's work.</summary>
    RenderSubmitStart = 2,

    /// <summary>The frame's last work submission has been queued (Submit A).</summary>
    RenderSubmitEnd = 3,

    /// <summary>Immediately before <c>vkQueuePresentKHR</c>.</summary>
    PresentStart = 4,

    /// <summary>Immediately after <c>vkQueuePresentKHR</c> returns.</summary>
    PresentEnd = 5,

    /// <summary>The frame's input is sampled; this is the point latency is measured from.</summary>
    InputSample = 6,

    /// <summary>A latency-measurement flash was triggered (tooling only).</summary>
    TriggerFlash = 7,

    /// <summary>A submission outside the frame loop starts.</summary>
    OutOfBandRenderSubmitStart = 8,

    /// <summary>A submission outside the frame loop has been queued.</summary>
    OutOfBandRenderSubmitEnd = 9,

    /// <summary>A present outside the frame loop starts.</summary>
    OutOfBandPresentStart = 10,

    /// <summary>A present outside the frame loop has returned.</summary>
    OutOfBandPresentEnd = 11,
}

/// <summary>One monotonic microsecond clock for every latency timestamp.</summary>
internal static class LatencyClock
{
    private static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    /// <summary>Microseconds since an arbitrary origin; never 0, so 0 stays "no timestamp".</summary>
    public static long NowUs()
    {
        long us = (long)(Stopwatch.GetTimestamp() * TicksToUs);
        return us > 0 ? us : 1;
    }
}

/// <summary>
/// Turns the marker stream of a frame into one <see cref="LatencyFrameReport" />,
/// and applies the self-healing rule from the plan: a phase still open when the
/// next frame starts is closed at the new frame's first marker and logged - once,
/// never every frame, because a dropped marker repeats and would otherwise fill
/// the log.
///
/// Pure logic, no Vulkan: the None backend uses it, the Native and AMD backends
/// will, and it is unit-tested on its own.
/// </summary>
internal sealed class LatencyPhaseTracker
{
    private readonly Action<string>? _log;

    private ulong _frameId;
    private bool _frameOpen;
    private bool _logged;

    private long _inputSample;
    private long _simStart;
    private long _simEnd;
    private long _renderSubmitStart;
    private long _renderSubmitEnd;
    private long _presentStart;
    private long _presentEnd;

    public LatencyPhaseTracker(Action<string>? log = null) => _log = log;

    /// <summary>How often a phase was still open when the next frame started.</summary>
    public int SelfHealCount { get; private set; }

    /// <summary>Whether the self-heal note has already gone to the log.</summary>
    public bool SelfHealLogged => _logged;

    /// <summary>The frame currently collecting markers; 0 before the first one.</summary>
    public ulong CurrentFrameId => _frameId;

    /// <summary>True while some Start has no matching End in the current frame.</summary>
    public bool HasOpenPhase => _frameOpen &&
        ((_simStart != 0 && _simEnd == 0) ||
         (_renderSubmitStart != 0 && _renderSubmitEnd == 0) ||
         (_presentStart != 0 && _presentEnd == 0));

    /// <summary>
    /// Stamps one marker. A marker of a frame id other than the current one
    /// starts a new frame first, closing whatever the old one left open. The
    /// OutOfBand markers belong to submissions outside the frame loop and are
    /// ignored here.
    /// </summary>
    public void Mark(ulong frameId, LatencyMarker marker, long timestampUs)
    {
        if (marker >= LatencyMarker.OutOfBandRenderSubmitStart) return;

        if (!_frameOpen || frameId != _frameId) BeginFrame(frameId, timestampUs);

        switch (marker)
        {
            case LatencyMarker.InputSample: _inputSample = timestampUs; break;
            case LatencyMarker.SimulationStart: _simStart = timestampUs; break;
            case LatencyMarker.SimulationEnd: _simEnd = timestampUs; break;
            case LatencyMarker.RenderSubmitStart: _renderSubmitStart = timestampUs; break;
            case LatencyMarker.RenderSubmitEnd: _renderSubmitEnd = timestampUs; break;
            case LatencyMarker.PresentStart: _presentStart = timestampUs; break;
            case LatencyMarker.PresentEnd: _presentEnd = timestampUs; break;
        }
    }

    /// <summary>
    /// Starts a frame: closes every phase the previous frame left open (counted,
    /// and logged the first time only) and clears the timestamps.
    /// </summary>
    public void BeginFrame(ulong frameId, long timestampUs)
    {
        if (_frameOpen && HasOpenPhase)
        {
            SelfHealCount++;
            if (!_logged)
            {
                _logged = true;
                _log?.Invoke("latency: a phase of frame " + _frameId +
                    " was still open when frame " + frameId +
                    " started; closing it. Reported once, however often it happens.");
            }
            // Closing means exactly that: the open phases end here, so the frame
            // that follows starts from a clean slate.
            CloseOpenPhases(timestampUs);
        }

        _frameId = frameId;
        _frameOpen = true;
        _inputSample = 0;
        _simStart = 0;
        _simEnd = 0;
        _renderSubmitStart = 0;
        _renderSubmitEnd = 0;
        _presentStart = 0;
        _presentEnd = 0;
    }

    private void CloseOpenPhases(long timestampUs)
    {
        if (_simStart != 0 && _simEnd == 0) _simEnd = timestampUs;
        if (_renderSubmitStart != 0 && _renderSubmitEnd == 0) _renderSubmitEnd = timestampUs;
        if (_presentStart != 0 && _presentEnd == 0) _presentEnd = timestampUs;
    }

    /// <summary>
    /// Closes the frame and builds its report. False when
    /// <paramref name="frameId" /> is not the frame being collected (a present of
    /// a frame whose markers never arrived), leaving the state untouched.
    /// </summary>
    public bool TryComplete(ulong frameId, ulong presentId, out LatencyFrameReport report)
    {
        if (!_frameOpen || frameId != _frameId)
        {
            report = default;
            return false;
        }

        report = LatencyFrameReport.FromCpuTimestamps(
            frameId, presentId,
            _inputSample, _simStart, _simEnd,
            _renderSubmitStart, _renderSubmitEnd,
            _presentStart, _presentEnd);
        _frameOpen = false;
        return true;
    }
}

/// <summary>
/// The finished frame reports a CPU-timestamp backend keeps until the stats
/// sample drains them (seam S7), as a fixed ring.
///
/// A ring rather than a <c>List</c> with <c>RemoveAt(0)</c>, which is what the
/// backends used before the review of 2026-09-12: nothing guarantees the stats
/// sample ever runs (<c>OPTIMUM_VULKAN_STATS</c> is normally unset), so the
/// buffer sits full for the whole session and every present shifted the whole
/// array down by one - about 20 KB of memmove per frame, under a lock, in the
/// present path, with the default None backend. The ring drops the oldest entry
/// by moving one index instead.
///
/// Every member takes the lock: the frame thread adds and amends, the stats
/// sample takes.
/// </summary>
internal sealed class LatencyReportBuffer
{
    /// <summary>A few seconds of frames; a client that never samples must not grow this.</summary>
    public const int DefaultCapacity = 256;

    private readonly LatencyFrameReport[] _reports;
    private readonly object _lock = new();

    /// <summary>Where the next report is written.</summary>
    private int _next;

    /// <summary>How many of the slots hold a report that has not been taken.</summary>
    private int _count;

    public LatencyReportBuffer(int capacity = DefaultCapacity)
    {
        if (capacity < 1) capacity = 1;
        _reports = new LatencyFrameReport[capacity];
    }

    public int Capacity => _reports.Length;

    /// <summary>How many reports are waiting to be taken.</summary>
    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>Adds one report, dropping the oldest when the ring is full.</summary>
    public void Add(in LatencyFrameReport report)
    {
        lock (_lock)
        {
            _reports[_next] = report;
            _next = _next + 1 == _reports.Length ? 0 : _next + 1;
            if (_count < _reports.Length) _count++;
        }
    }

    /// <summary>
    /// Fills in the GPU interval of a report that is still waiting, newest first.
    /// False when that frame's report has already been taken (the Native backend
    /// observes the completion after the fact and never guesses).
    /// </summary>
    public bool AmendGpuUs(ulong frameId, ulong gpuUs)
    {
        lock (_lock)
        {
            for (int i = 1; i <= _count; i++)
            {
                int index = _next - i;
                if (index < 0) index += _reports.Length;
                if (_reports[index].FrameId != frameId) continue;
                _reports[index] = _reports[index] with { GpuUs = gpuUs };
                return true;
            }
            return false;
        }
    }

    /// <summary>The reports since the last call, oldest first, and clears them.</summary>
    public LatencyFrameReport[] Take()
    {
        lock (_lock)
        {
            if (_count == 0) return Array.Empty<LatencyFrameReport>();

            var taken = new LatencyFrameReport[_count];
            int index = _next - _count;
            if (index < 0) index += _reports.Length;
            for (int i = 0; i < _count; i++)
            {
                taken[i] = _reports[index];
                index = index + 1 == _reports.Length ? 0 : index + 1;
            }

            _count = 0;
            _next = 0;
            return taken;
        }
    }
}
