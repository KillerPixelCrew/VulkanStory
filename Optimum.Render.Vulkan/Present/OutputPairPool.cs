using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Optimum.Render.Vulkan.Core;

namespace Optimum.Render.Vulkan.Present;

/// <summary>Where one output pair is in its cycle between the render thread and the present thread.</summary>
internal enum OutputPairState
{
    /// <summary>Nobody holds it; the render thread may take it.</summary>
    Free,

    /// <summary>The render thread is recording the pair's two images.</summary>
    Rendering,

    /// <summary>Handed off, waiting for the present thread.</summary>
    Queued,

    /// <summary>The present thread is presenting it.</summary>
    Presenting,
}

/// <summary>What <see cref="OutputPairPool.WaitForWork" /> tells the present thread to do.</summary>
internal enum PresentWork
{
    /// <summary>Present the pair it returned.</summary>
    Pair,

    /// <summary>The render thread asked for a quiesce (a pool rebuild): drop, go idle, acknowledge.</summary>
    Quiesce,

    /// <summary>Shut down: drop, go idle, exit.</summary>
    Stop,
}

/// <summary>The two textures of one pair, as the device created them.</summary>
internal readonly record struct OutputPairImages(
    int InterpolatedTextureId, VulkanTexture? Interpolated, int RealTextureId, VulkanTexture? Real);

/// <summary>
/// One (interpolated, real) display-resolution image pair.
///
/// Threading: the images are immutable for the pair's life. Every other member is
/// written only inside <see cref="OutputPairPool" /> under its handoff lock. The
/// handoff values (<see cref="FrameValue" />, the present ids, the frame id) are
/// written at <see cref="OutputPairPool.MarkQueued" /> and are immutable while the
/// pair is Queued or Presenting - which is the only time the present thread reads
/// them. <see cref="LastPresentValue" /> is written by the present thread's release
/// and read by the render thread's acquire, both under the lock.
/// </summary>
internal sealed class OutputPair
{
    internal OutputPair(int index, int generation, OutputPairImages images)
    {
        Index = index;
        Generation = generation;
        Images = images;
    }

    public int Index { get; }

    /// <summary>The pool build this pair belongs to; a rebuild starts a new generation.</summary>
    public int Generation { get; }

    public OutputPairImages Images { get; }

    public VulkanTexture? Interpolated => Images.Interpolated;

    public VulkanTexture? Real => Images.Real;

    /// <summary>Guarded by the pool's handoff lock.</summary>
    internal OutputPairState State { get; set; }

    /// <summary>The Frame timeline value whose completion means both images are written (the evaluate's submission).</summary>
    public ulong FrameValue { get; internal set; }

    /// <summary>
    /// The present timeline value of the newest present submission that read this
    /// pair, 0 before the first. The next write into the pair waits on it inside its
    /// own submission (a GPU dependency, never a CPU wait).
    /// </summary>
    public ulong LastPresentValue { get; internal set; }

    /// <summary>The present id the render thread reserved at handoff for the generated present.</summary>
    public ulong GeneratedPresentId { get; internal set; }

    /// <summary>The present id the render thread reserved at handoff for the real present.</summary>
    public ulong RealPresentId { get; internal set; }

    /// <summary>The latency frame id of the frame the pair carries.</summary>
    public ulong LatencyFrameId { get; internal set; }

    /// <summary>Every state the pair was in, in order, when the pool tracks history. Guarded by the lock.</summary>
    internal List<OutputPairState>? History { get; set; }
}

/// <summary>
/// The pool of <c>FramesInFlight</c> output pairs and the one rendezvous between the
/// render thread and the present thread (ROADMAP, "The paced present: the design").
///
/// Every mutable field below is guarded by <see cref="_lock" /> - the "handoff lock"
/// - and every wake-up is a <see cref="Monitor.PulseAll" /> on it, so a state change,
/// a quiesce request and a stop can never be lost between a check and a wait.
///
/// Why a pool rather than one image: the render thread must never overwrite a pair
/// the present thread has not read yet, and a lagging present thread would otherwise
/// see the next frame's pixels (or poison) in the frame it is presenting - the
/// ROADMAP's hazard 1, invisible to a single-frame readback. When no pair is free
/// the render thread blocks, which is the back-pressure; the present thread never
/// waits on the render thread's CPU progress, so the two cannot wait on each other.
/// </summary>
internal sealed class OutputPairPool
{
    /// <summary>A back-pressure wait longer than this is logged; every wait is counted.</summary>
    public static readonly TimeSpan LoggedWait = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private readonly Action<string>? _log;

    // --- guarded by _lock ---------------------------------------------------
    private OutputPair[] _pairs = Array.Empty<OutputPair>();
    private readonly Queue<OutputPair> _queued = new();
    private int _generation;
    private bool _stop;
    private bool _quiesceRequested;
    private bool _quiesceAcked;
    private bool _presenterFailed;
    private long _backPressureWaits;
    private long _backPressureTimeouts;
    private long _dropped;
    private bool _trackHistory;

    public OutputPairPool(int capacity, Action<string>? log = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _log = log;
    }

    /// <summary>How many pairs the pool holds (the ring depth). Immutable.</summary>
    public int Capacity { get; }

    /// <summary>The images' size and format at the last build. Render thread only (it builds).</summary>
    public uint Width { get; private set; }

    /// <summary>See <see cref="Width" />.</summary>
    public uint Height { get; private set; }

    /// <summary>See <see cref="Width" />.</summary>
    public Silk.NET.Vulkan.Format Format { get; private set; }

    /// <summary>Records every pair's state history (tests). Set before the first build.</summary>
    public bool TrackHistory
    {
        get { lock (_lock) return _trackHistory; }
        set { lock (_lock) _trackHistory = value; }
    }

    public long BackPressureWaits
    {
        get { lock (_lock) return _backPressureWaits; }
    }

    public long BackPressureTimeouts
    {
        get { lock (_lock) return _backPressureTimeouts; }
    }

    /// <summary>Queued pairs a quiesce or a stop dropped without presenting.</summary>
    public long Dropped
    {
        get { lock (_lock) return _dropped; }
    }

    public int QueuedCount
    {
        get { lock (_lock) return _queued.Count; }
    }

    /// <summary>
    /// For the pacer: another pair is waiting - or the pool is stopping or quiescing,
    /// in which case the held real frame should go out now rather than after a hold.
    /// </summary>
    public bool NextPairQueuedOrLeaving
    {
        get { lock (_lock) return _queued.Count > 0 || _stop || _quiesceRequested; }
    }

    /// <summary>A snapshot of every pair's state, in index order.</summary>
    public OutputPairState[] States()
    {
        lock (_lock)
        {
            var states = new OutputPairState[_pairs.Length];
            for (int i = 0; i < _pairs.Length; i++) states[i] = _pairs[i].State;
            return states;
        }
    }

    /// <summary>A copy of every pair's history, in index order (empty lists when untracked).</summary>
    public List<OutputPairState>[] Histories()
    {
        lock (_lock)
        {
            var histories = new List<OutputPairState>[_pairs.Length];
            for (int i = 0; i < _pairs.Length; i++)
            {
                histories[i] = _pairs[i].History == null
                    ? new List<OutputPairState>()
                    : new List<OutputPairState>(_pairs[i].History!);
            }
            return histories;
        }
    }

    /// <summary>
    /// Builds (or rebuilds) the pairs. Every existing pair must be Free - after a
    /// quiesce the present thread has dropped the queue and gone idle, and the render
    /// thread holds none - and each is handed to <paramref name="retire" /> first.
    /// </summary>
    public void Build(uint width, uint height, Silk.NET.Vulkan.Format format,
        Func<int, OutputPairImages> create, Action<OutputPair>? retire = null)
    {
        lock (_lock)
        {
            foreach (OutputPair pair in _pairs)
            {
                if (pair.State != OutputPairState.Free)
                {
                    throw new InvalidOperationException(
                        $"output pair {pair.Index} is {pair.State}: the pool is rebuilt only after a quiesce");
                }
            }
            foreach (OutputPair pair in _pairs) retire?.Invoke(pair);

            _generation++;
            var pairs = new OutputPair[Capacity];
            for (int i = 0; i < Capacity; i++)
            {
                pairs[i] = new OutputPair(i, _generation, create(i))
                {
                    State = OutputPairState.Free,
                    History = _trackHistory ? new List<OutputPairState> { OutputPairState.Free } : null,
                };
            }
            _pairs = pairs;
            Width = width;
            Height = height;
            Format = format;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Every pair, for teardown; the caller has stopped the present thread.</summary>
    public OutputPair[] Pairs()
    {
        lock (_lock) return (OutputPair[])_pairs.Clone();
    }

    // ------------------------------------------------------------ render thread

    /// <summary>
    /// Takes a Free pair for recording, blocking - the back-pressure - while none is.
    /// Bounded by <paramref name="timeout" />: false on timeout, when the present
    /// thread has failed, or when a stop was requested, and the caller skips the
    /// frame's presentation rather than hanging the client. Prefers the pair released
    /// longest ago, whose present read is the most likely to have completed.
    /// </summary>
    public bool TryAcquireForRendering(TimeSpan timeout, out OutputPair? pair)
    {
        pair = null;
        long start = Stopwatch.GetTimestamp();
        bool waited = false;
        lock (_lock)
        {
            while (true)
            {
                if (_stop || _presenterFailed) return false;

                OutputPair? best = null;
                foreach (OutputPair candidate in _pairs)
                {
                    if (candidate.State != OutputPairState.Free) continue;
                    if (best == null || candidate.LastPresentValue < best.LastPresentValue) best = candidate;
                }
                if (best != null)
                {
                    Transition(best, OutputPairState.Free, OutputPairState.Rendering);
                    pair = best;
                    break;
                }

                if (!waited)
                {
                    waited = true;
                    _backPressureWaits++;
                }
                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_lock, remaining))
                {
                    if (FreeCount() > 0) continue;
                    _backPressureTimeouts++;
                    _log?.Invoke("paced present: no output pair became free within " +
                        (int)timeout.TotalMilliseconds + " ms; the frame is not presented (" +
                        _backPressureTimeouts + " so far)");
                    return false;
                }
            }
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        if (waited && elapsed > LoggedWait)
        {
            _log?.Invoke("paced present: the render thread waited " + (int)elapsed.TotalMilliseconds +
                " ms for a free output pair");
        }
        return true;
    }

    /// <summary>The render thread recorded both images; the pair goes to the present thread.</summary>
    public void MarkQueued(OutputPair pair, ulong frameValue, ulong generatedPresentId, ulong realPresentId,
        ulong latencyFrameId)
    {
        lock (_lock)
        {
            Transition(pair, OutputPairState.Rendering, OutputPairState.Queued);
            pair.FrameValue = frameValue;
            pair.GeneratedPresentId = generatedPresentId;
            pair.RealPresentId = realPresentId;
            pair.LatencyFrameId = latencyFrameId;
            _queued.Enqueue(pair);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>The render thread took a pair and will not hand it off (its frame failed).</summary>
    public void CancelRendering(OutputPair pair)
    {
        lock (_lock)
        {
            Transition(pair, OutputPairState.Rendering, OutputPairState.Free);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Asks the present thread to finish its current present, drop the queue, go idle
    /// and acknowledge. True once acknowledged; false after <paramref name="timeout" />
    /// or when the present thread is gone, in which case the request is withdrawn.
    /// </summary>
    public bool Quiesce(TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _quiesceRequested = true;
            _quiesceAcked = false;
            Monitor.PulseAll(_lock);
            while (!_quiesceAcked)
            {
                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(start);
                if (_presenterFailed || _stop || remaining <= TimeSpan.Zero)
                {
                    _quiesceRequested = false;
                    Monitor.PulseAll(_lock);
                    return false;
                }
                Monitor.Wait(_lock, remaining);
            }
            return true;
        }
    }

    /// <summary>Ends a quiesce; the present thread picks work up again.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            _quiesceRequested = false;
            _quiesceAcked = false;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Asks the present thread to drop the queue, go idle and exit.</summary>
    public void RequestStop()
    {
        lock (_lock)
        {
            _stop = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Tests: blocks until no pair is Queued or Presenting, bounded. True when drained.</summary>
    public bool WaitUntilPresented(TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            while (true)
            {
                bool busy = _queued.Count > 0;
                foreach (OutputPair pair in _pairs)
                {
                    if (pair.State == OutputPairState.Presenting) busy = true;
                }
                if (!busy) return true;
                if (_presenterFailed) return false;
                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero) return false;
                Monitor.Wait(_lock, remaining);
            }
        }
    }

    // ----------------------------------------------------------- present thread

    /// <summary>
    /// Blocks until there is something to do. Stop wins over a quiesce, and a quiesce
    /// over queued pairs, so neither waits behind a backlog. A returned pair is
    /// Presenting.
    /// </summary>
    public PresentWork WaitForWork(out OutputPair? pair)
    {
        pair = null;
        lock (_lock)
        {
            while (true)
            {
                if (_stop) return PresentWork.Stop;
                if (_quiesceRequested && !_quiesceAcked) return PresentWork.Quiesce;
                if (_queued.Count > 0)
                {
                    OutputPair next = _queued.Dequeue();
                    Transition(next, OutputPairState.Queued, OutputPairState.Presenting);
                    pair = next;
                    return PresentWork.Pair;
                }
                Monitor.Wait(_lock);
            }
        }
    }

    /// <summary>
    /// Both presents of <paramref name="pair" /> were submitted (or skipped); the
    /// newest present timeline value that read it is <paramref name="lastPresentValue" />
    /// (0: none this time, the previous one stands).
    /// </summary>
    public void ReleasePresented(OutputPair pair, ulong lastPresentValue)
    {
        lock (_lock)
        {
            Transition(pair, OutputPairState.Presenting, OutputPairState.Free);
            if (lastPresentValue > pair.LastPresentValue) pair.LastPresentValue = lastPresentValue;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Drops every Queued pair back to Free without presenting it. Returns how many.</summary>
    public int DropQueued()
    {
        lock (_lock)
        {
            int count = _queued.Count;
            while (_queued.Count > 0)
            {
                Transition(_queued.Dequeue(), OutputPairState.Queued, OutputPairState.Free);
            }
            _dropped += count;
            Monitor.PulseAll(_lock);
            return count;
        }
    }

    /// <summary>
    /// The present thread is idle after a quiesce: acknowledges it and waits until the
    /// render thread resumes or stops the pool.
    /// </summary>
    public void AcknowledgeQuiesceAndWait()
    {
        lock (_lock)
        {
            if (!_quiesceRequested) return;
            _quiesceAcked = true;
            Monitor.PulseAll(_lock);
            while (_quiesceRequested && !_stop) Monitor.Wait(_lock);
        }
    }

    /// <summary>The present thread died; nothing will consume pairs again, so nobody may wait for one.</summary>
    public void NotePresenterFailed()
    {
        lock (_lock)
        {
            _presenterFailed = true;
            Monitor.PulseAll(_lock);
        }
    }

    // ------------------------------------------------------------------ helpers

    private int FreeCount()
    {
        int free = 0;
        foreach (OutputPair pair in _pairs)
        {
            if (pair.State == OutputPairState.Free) free++;
        }
        return free;
    }

    /// <summary>
    /// The only place a state changes. An unexpected state is a threading bug - the
    /// render thread about to overwrite a pair the present thread still owns, or a
    /// double release - and throws instead of carrying on with a torn image.
    /// </summary>
    private void Transition(OutputPair pair, OutputPairState expected, OutputPairState next)
    {
        if (pair.State != expected)
        {
            throw new InvalidOperationException(
                $"output pair {pair.Index} is {pair.State}, expected {expected} before {next}");
        }
        pair.State = next;
        pair.History?.Add(next);
    }
}
