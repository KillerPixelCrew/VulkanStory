using System;
using System.Collections.Generic;
using System.Threading;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Resources waiting for the GPU to stop referencing them.
///
/// Each entry records the newest Frame and Transfer values that existed when it
/// was retired: any command that could name the resource carries one of those
/// values or an older one. The entry is destroyed at the first
/// <see cref="Collect" /> that sees both counters at or past its values, never
/// earlier, and ready entries are destroyed in the order they were retired.
///
/// <see cref="Retire" /> is safe from any thread (the game's VAO and UBO
/// finalizers release from the finalizer thread). <see cref="Collect" /> runs on
/// the render thread at the start of a frame, and disposes outside the lock so a
/// resource whose Dispose retires something else cannot deadlock.
/// </summary>
internal sealed class RetireQueue
{
    private readonly record struct Entry(IDisposable Resource, ulong Frame, ulong Transfer);

    private readonly ITimelineClock _clock;
    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();
    private int _count;

    public RetireQueue(ITimelineClock clock) => _clock = clock;

    /// <summary>Entries not yet destroyed.</summary>
    public int PendingCount => Volatile.Read(ref _count);

    /// <summary>Queues <paramref name="resource" /> against the timeline values recorded right now.</summary>
    public void Retire(IDisposable resource)
    {
        lock (_lock)
        {
            _entries.Add(new Entry(resource, _clock.FrameRecorded, _clock.TransferRecorded));
            Volatile.Write(ref _count, _entries.Count);
        }
    }

    /// <summary>
    /// Destroys every entry whose Frame and Transfer values have both completed,
    /// oldest first. An entry that has not passed stays queued without holding back
    /// later entries that have. Returns how many were destroyed.
    /// </summary>
    public int Collect()
    {
        if (PendingCount == 0) return 0;

        // Completion only moves forward, so counters read before taking the lock
        // are still true for every entry inside it.
        ulong frameCompleted = _clock.FrameCompleted;
        ulong transferCompleted = _clock.TransferCompleted;

        List<IDisposable>? ready = null;
        lock (_lock)
        {
            int kept = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (entry.Frame <= frameCompleted && entry.Transfer <= transferCompleted)
                {
                    ready ??= new List<IDisposable>();
                    ready.Add(entry.Resource);
                }
                else
                {
                    _entries[kept++] = entry;
                }
            }
            _entries.RemoveRange(kept, _entries.Count - kept);
            Volatile.Write(ref _count, _entries.Count);
        }

        if (ready == null) return 0;
        foreach (IDisposable resource in ready) resource.Dispose();
        return ready.Count;
    }

    /// <summary>
    /// Destroys everything regardless of the timelines, oldest first. Teardown
    /// only, after the GPU has finished all submitted work.
    /// </summary>
    public void DisposeAll()
    {
        while (true)
        {
            Entry[] all;
            lock (_lock)
            {
                if (_entries.Count == 0) return;
                all = _entries.ToArray();
                _entries.Clear();
                Volatile.Write(ref _count, 0);
            }
            foreach (Entry entry in all) entry.Resource.Dispose();
        }
    }
}

/// <summary>
/// Tells a resource created in the last few frames from a long-lived one, with
/// no per-resource storage.
///
/// Resource ids (<see cref="ResourceIds" />) only ever increase, so the highest id
/// issued when a frame began is a watermark: every id above the watermark of the
/// frame <c>N - 1</c> frames back was created within the last N frames. The class
/// keeps one watermark per frame in a ring.
///
/// The descriptor layer uses it to route sets naming short-lived resources (GUI
/// text, atlas tasks, fresh chunk meshes, overflow uniform copies) to the per-slot
/// arena that is reset every frame, instead of caching them in
/// <see cref="DescriptorCache" /> only to evict them moments later.
/// </summary>
internal sealed class ResourceAge
{
    public const int DefaultShortLivedFrames = 60;

    private readonly ulong[] _watermarks;
    private long _frames;
    private int _shortLivedFrames;

    public ResourceAge(int capacity = DefaultShortLivedFrames)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _watermarks = new ulong[capacity];
        _shortLivedFrames = capacity;
    }

    /// <summary>
    /// How many frames a resource counts as short-lived for, at most the ring's
    /// capacity. Zero makes every resource long-lived (the arena is never used).
    /// </summary>
    public int ShortLivedFrames
    {
        get => _shortLivedFrames;
        set
        {
            if (value < 0 || value > _watermarks.Length) throw new ArgumentOutOfRangeException(nameof(value));
            _shortLivedFrames = value;
        }
    }

    /// <summary>Frames noted so far.</summary>
    public long Frames => _frames;

    /// <summary>Records the watermark at the start of a frame: the highest resource id issued so far.</summary>
    public void NoteFrame(ulong highestIssuedId)
    {
        _watermarks[_frames % _watermarks.Length] = highestIssuedId;
        _frames++;
    }

    /// <summary>
    /// Whether <paramref name="resource" /> was created within the last
    /// <see cref="ShortLivedFrames" /> frames, the current one included. Id 0 (a
    /// permanent resource) never is. Before that many frames have been noted,
    /// every resource is: none can be older.
    /// </summary>
    public bool IsShortLived(ulong resource)
    {
        if (resource == 0 || _shortLivedFrames == 0) return false;
        if (_frames < _shortLivedFrames) return true;

        ulong watermark = _watermarks[(_frames - _shortLivedFrames) % _watermarks.Length];
        return resource > watermark;
    }

    /// <summary>Whether any resource a set names is short-lived.</summary>
    public bool NamesShortLived(DescriptorSetContents contents)
    {
        foreach (SamplerBindingValue sampler in contents.Samplers)
        {
            if (IsShortLived(sampler.Resource)) return true;
        }
        foreach (BufferBindingValue buffer in contents.Buffers)
        {
            if (IsShortLived(buffer.Resource)) return true;
        }
        return false;
    }
}
