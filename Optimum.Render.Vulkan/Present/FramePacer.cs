using System;
using Optimum.Render.Vulkan.Core;

namespace Optimum.Render.Vulkan.Present;

/// <summary>
/// The spacing pacer of the paced present (ROADMAP, "The paced present: the design").
/// NVIDIA's DLSS-FG guide (v310.7.0, section 7) leaves timing to the application:
/// present the generated frame as soon as the evaluate completes, and the retained
/// real frame "at the correct time to achieve equal time intervals between frames".
/// This class decides that time; it never waits and never touches Vulkan, so it is
/// deterministic under an injected clock and tested without a device (PacerTests).
///
/// The rendered-frame interval is measured from successive <see cref="PacedPresentKind.Generated" />
/// present returns, because the generated present is issued the moment a pair
/// arrives: its cadence is the render thread's cadence as the present thread sees
/// it, which is the quantity the real frame has to split in half. The real present's
/// own return is not used for smoothing - it lands where this pacer put it, and
/// feeding it back would make the pacer measure itself.
///
/// Threading: the present thread calls both interface methods; any thread may read
/// <see cref="SmoothedIntervalUs" /> (the frame-cap wiring may). Every mutable field
/// below is guarded by <see cref="_gate" />, the one lock of this class.
/// </summary>
internal sealed class FramePacer : IFramePacer
{
    /// <summary>
    /// EMA weight of one new interval (1/8). The time constant is about eight
    /// rendered frames - 133 ms at 60 fps: short enough to follow a real frame-rate
    /// change within a quarter second, long enough that +-2 ms of render jitter
    /// (uniform, stddev 1.15 ms) leaves the half-interval hold with a stddev of
    /// sqrt(w / (2 - w)) / 2 = 0.13x the input's, which PacerTests measures.
    /// </summary>
    public const double SmoothingWeight = 0.125;

    /// <summary>
    /// An interval is clamped to [1/2, 2] x the smoothed one before it enters the
    /// average. 2x is the largest legitimate single-frame jump - one missed vsync
    /// under FIFO doubles an interval - so anything beyond it is an outlier that
    /// may move the average only as far as a missed vsync would. With the clamp a
    /// sustained slow-down grows the average by at most w x 1 = 12.5% per frame and
    /// a speed-up shrinks it by at most w x 1/2 = 6.25% per frame.
    /// </summary>
    public const double OutlierLowFactor = 0.5;

    /// <inheritdoc cref="OutlierLowFactor" />
    public const double OutlierHighFactor = 2.0;

    /// <summary>
    /// An interval above 3x the smoothed one is a stall (a loading hitch, a GC pause,
    /// a chunk rebuild spike) and does not enter the average at all: a single 100 ms
    /// stall at 60 fps would otherwise hold the next several real frames about 1 ms
    /// late each while the average decays back. The next pair is spaced as if the
    /// stall never happened.
    /// </summary>
    public const double StallFactor = 3.0;

    /// <summary>
    /// Three stall-sized intervals in a row are not a stall but a new frame rate (a
    /// 60 -> 15 fps drop entering a heavy scene): the average is reseeded to the
    /// latest interval instead of creeping up 12.5% per frame for a dozen frames.
    /// </summary>
    public const int StallsBeforeReseed = 3;

    /// <summary>
    /// Bounds of the smoothed interval: 1 ms (1000 fps, below which the hold is
    /// shorter than the SpinTail resolution of the hold primitive anyway) to 100 ms,
    /// the same stale-anchor bound as <see cref="NativeLatencyBackend.MaxHoldUs" />:
    /// a real frame is never held more than 50 ms behind its generated frame.
    /// </summary>
    public const long MinIntervalUs = 1_000;

    /// <inheritdoc cref="MinIntervalUs" />
    public const long MaxIntervalUs = 100_000;

    /// <summary>
    /// The interval assumed before any pair has been measured: one 60 Hz refresh,
    /// the same fallback the display refresh source uses when the monitor does not
    /// report a rate. The wiring replaces it with the measured refresh interval.
    /// </summary>
    public const long DefaultSeedIntervalUs = 16_667;

    /// <summary>
    /// While pairs keep queueing, the first real frame goes out immediately (holding
    /// it only grows the queue and the latency) and every further one is held a
    /// quarter interval. A quarter because a queue drains as long as a pair takes less
    /// than one rendered interval to consume - a quarter drains 3/4 of an interval per
    /// pair - while still never putting a generated and its real frame back to back,
    /// which is what a backlog released all at once would do (a present-thread stall
    /// of 100 ms at 60 fps would otherwise release six pairs as twelve presents with
    /// no gaps).
    /// </summary>
    public const int BacklogHoldDivisor = 4;

    private readonly Func<long> _nowUs;
    private readonly PresentIntervalRing? _intervals;
    private readonly object _gate = new();

    // All guarded by _gate.
    private double _smoothedUs;
    private long _lastGeneratedUs;
    private int _consecutiveStalls;
    private int _backlogStreak;
    private bool _skipNextInterval;
    private long _immediateRealPresents;
    private long _stallsRejected;

    /// <param name="nowUs">The clock, in <see cref="LatencyClock.NowUs" /> microseconds; tests inject a fake.</param>
    /// <param name="seedIntervalUs">The interval assumed until pairs are measured; clamped to the bounds.</param>
    /// <param name="intervals">
    /// Where every present's return goes for the <c>stats.present</c> line; null records nothing.
    /// The client passes <see cref="VulkanStats.PresentIntervals" />.
    /// </param>
    public FramePacer(Func<long> nowUs, long seedIntervalUs = DefaultSeedIntervalUs, PresentIntervalRing? intervals = null)
    {
        _nowUs = nowUs ?? throw new ArgumentNullException(nameof(nowUs));
        _intervals = intervals;
        _smoothedUs = Clamp(seedIntervalUs);
    }

    /// <summary>The smoothed rendered-frame interval, in microseconds.</summary>
    public long SmoothedIntervalUs
    {
        get { lock (_gate) return (long)Math.Round(_smoothedUs); }
    }

    /// <summary>Real frames this pacer released immediately because a pair was queued.</summary>
    public long ImmediateRealPresents
    {
        get { lock (_gate) return _immediateRealPresents; }
    }

    /// <summary>Intervals rejected as stalls (not reseeds).</summary>
    public long StallsRejected
    {
        get { lock (_gate) return _stallsRejected; }
    }

    /// <summary>
    /// Replaces the interval assumed before a pair has been measured - the measured
    /// display refresh interval, once it is known. Ignored once pairs have been
    /// measured, because a measurement beats an assumption.
    /// </summary>
    public void SeedIntervalUs(long intervalUs)
    {
        lock (_gate)
        {
            if (_lastGeneratedUs == 0) _smoothedUs = Clamp(intervalUs);
        }
    }

    public void NotePresented(PacedPresentKind kind, long presentReturnUs)
    {
        _intervals?.Add(kind, presentReturnUs);
        if (kind != PacedPresentKind.Generated) return;

        lock (_gate)
        {
            long last = _lastGeneratedUs;
            if (last != 0 && presentReturnUs <= last) return; // a clock that went backwards is not an interval
            _lastGeneratedUs = presentReturnUs;
            if (last == 0) return;
            // While a backlog drains, a generated present goes out right after the previous
            // real one, so its interval is the drain rate, not the render rate. Fed in, a
            // 100 ms present-thread stall shrank the average from 16.7 to 11.2 ms over the
            // eight-pair drain and held every real frame ~2.8 ms early for 25 frames after.
            // The first pair after the backlog is the same case one step later: its
            // generated present still followed a backlogged real one, so the interval that
            // ends at the next generated present starts from a late anchor (skipping only
            // the backlog left the real frame 0.52 ms early, back within 0.1 ms only 13 frames later, in PacerTests).
            if (_backlogStreak > 0 || _skipNextInterval)
            {
                _skipNextInterval = false;
                return;
            }
            FeedInterval(presentReturnUs - last);
        }
    }

    public long RealPresentTargetUs(long generatedReturnUs, bool nextPairQueued)
    {
        lock (_gate)
        {
            if (!nextPairQueued)
            {
                if (_backlogStreak > 0) _skipNextInterval = true;
                _backlogStreak = 0;
                return generatedReturnUs + (long)Math.Round(_smoothedUs / 2.0);
            }

            _backlogStreak++;
            if (_backlogStreak == 1)
            {
                _immediateRealPresents++;
                // generatedReturnUs is already in the past for the caller, so it reads as "now".
                long now = _nowUs();
                return generatedReturnUs < now ? generatedReturnUs : now;
            }
            return generatedReturnUs + (long)Math.Round(_smoothedUs / BacklogHoldDivisor);
        }
    }

    // Caller holds _gate.
    private void FeedInterval(long rawUs)
    {
        double smoothed = _smoothedUs;
        if (rawUs > StallFactor * smoothed)
        {
            _consecutiveStalls++;
            if (_consecutiveStalls >= StallsBeforeReseed)
            {
                _smoothedUs = Clamp(rawUs);
                _consecutiveStalls = 0;
            }
            else
            {
                _stallsRejected++;
            }
            return;
        }

        _consecutiveStalls = 0;
        double clamped = Math.Clamp((double)rawUs, smoothed * OutlierLowFactor, smoothed * OutlierHighFactor);
        _smoothedUs = Clamp(smoothed + SmoothingWeight * (clamped - smoothed));
    }

    private static double Clamp(double intervalUs) => Math.Clamp(intervalUs, MinIntervalUs, MaxIntervalUs);
}
