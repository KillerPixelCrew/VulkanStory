using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;

// The Frame/ folder follows the plan's layout; the namespace stays Core until the
// renderer is reorganised.
namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// GPU time per labelled section of a frame, for attributing a GPU-bound frame
/// rather than guessing at it.
///
/// Each frame slot owns one timestamp pool. The slot's first commands reset it and
/// write the "frame" mark; every later <see cref="Mark" /> closes the previous
/// section and opens a new one, and every command-buffer submission of the slot
/// writes a "submit" mark, so GPU idle between a frame's command buffers shows up
/// as its own section rather than inflating the one before it. The next frame
/// command buffer reopens the interrupted section; a present buffer opens "present". A section's time
/// is the gap between its mark and the next; the frame's last mark has no end and
/// is dropped. Marks are written at BOTTOM_OF_PIPE, so a section is the work that
/// finished between the two marks, which is close to exclusive time on a GPU
/// that is kept busy.
///
/// Results are read without the wait bit when the slot is recycled, after the
/// ring's wait for that slot's last submission, so nothing ever blocks on them.
/// Only the graphics queue is measured: work an SDK records on its own queues
/// (DLSS-G, FidelityFX, and XeSS-FG interpolation, and the DX12 side of any
/// interop path) is not included.
///
/// Off unless the stats log is on; <c>OPTIMUM_VULKAN_GPU_TIMESTAMPS=0</c> turns it
/// off even then, since each mark can drain the pipeline.
/// </summary>
internal sealed unsafe class GpuTimestamps : IDisposable
{
    public const uint MarksPerFrame = 256;
    public const string FrameLabel = "frame";
    public const string SubmitLabel = "submit";
    public const string PresentLabel = "present";

    private readonly VulkanContext _context;
    private readonly SlotMarks[] _slots;
    private readonly double _nanosecondsPerTick;
    private readonly ulong _validMask;
    private readonly Dictionary<string, int> _labelIds = new(StringComparer.Ordinal);
    private readonly List<string> _labels = new();
    private readonly ulong[] _results = new ulong[MarksPerFrame * 2];
    private double[] _sums = new double[16];
    private long[] _indices = new long[16];
    private long _recordedFrames;
    private int _current = -1;
    private string _openLabel = FrameLabel;
    private long _frames;
    private double _frameSum;
    private long _dropped;
    private bool _disposed;

    private sealed class SlotMarks
    {
        public QueryPool Pool;
        public readonly int[] Labels = new int[MarksPerFrame];
        public uint Used;
    }

    private GpuTimestamps(VulkanContext context, int framesInFlight, double nanosecondsPerTick, uint validBits)
    {
        _context = context;
        _nanosecondsPerTick = nanosecondsPerTick;
        _validMask = validBits >= 64 ? ulong.MaxValue : (1UL << (int)validBits) - 1;
        _slots = new SlotMarks[framesInFlight];
        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = MarksPerFrame,
        };
        for (int i = 0; i < framesInFlight; i++)
        {
            var slot = new SlotMarks();
            VulkanResult.Check(context.Api.CreateQueryPool(context.Device, &info, null, out slot.Pool),
                "vkCreateQueryPool for GPU timestamps");
            _slots[i] = slot;
        }
    }

    public static bool EnabledByEnvironment(string? statsLogPath) =>
        statsLogPath != null &&
        Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_GPU_TIMESTAMPS") != "0";

    /// <summary>Null when the graphics queue cannot write timestamps.</summary>
    public static GpuTimestamps? TryCreate(VulkanContext context, int framesInFlight)
    {
        Vk api = context.Api;
        api.GetPhysicalDeviceProperties(context.PhysicalDevice, out PhysicalDeviceProperties properties);
        uint count = 0;
        api.GetPhysicalDeviceQueueFamilyProperties(context.PhysicalDevice, ref count, null);
        if (context.GraphicsQueueFamily >= count) return null;
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* familiesPtr = families)
            api.GetPhysicalDeviceQueueFamilyProperties(context.PhysicalDevice, ref count, familiesPtr);
        uint validBits = families[context.GraphicsQueueFamily].TimestampValidBits;
        if (validBits == 0 || properties.Limits.TimestampPeriod <= 0) return null;
        return new GpuTimestamps(context, framesInFlight, properties.Limits.TimestampPeriod, validBits);
    }

    /// <summary>
    /// The slot's previous frame has completed: its marks are harvested, then the
    /// pool is reset on the slot's first command buffer and the frame mark written.
    /// Must run outside any rendering scope.
    /// </summary>
    public void BeginSlot(int slotIndex, CommandBuffer commands)
    {
        SlotMarks slot = _slots[slotIndex];
        Harvest(slot);
        _context.Api.CmdResetQueryPool(commands, slot.Pool, 0, MarksPerFrame);
        slot.Used = 0;
        _current = slotIndex;
        _recordedFrames++;
        Write(commands, FrameLabel);
    }

    /// <summary>The section the next mark closes.</summary>
    public string OpenLabel => _openLabel;

    /// <summary>
    /// A later command buffer of the slot started: a frame continuation reopens
    /// the section a partial submission interrupted, a present buffer opens "present".
    /// </summary>
    public void OnCommandsStarted(int slotIndex, CommandBuffer commands, bool frameCommands)
    {
        if (slotIndex != _current) return;
        Write(commands, frameCommands ? _openLabel : PresentLabel);
    }

    /// <summary>The slot's current command buffer is about to be submitted.</summary>
    public void OnCommandsEnding(int slotIndex, CommandBuffer commands)
    {
        if (slotIndex != _current) return;
        string open = _openLabel;
        Write(commands, SubmitLabel);
        _openLabel = open;
    }

    /// <summary>
    /// Closes the open section and opens <paramref name="label" /> on <paramref name="commands" />.
    /// Marking the section already open does nothing, so a per-draw caller costs one compare.
    /// </summary>
    public void Mark(CommandBuffer commands, string label)
    {
        if (ReferenceEquals(label, _openLabel)) return;
        Write(commands, label);
    }

    private void Write(CommandBuffer commands, string label)
    {
        _openLabel = label;
        if (_current < 0 || commands.Handle == 0) return;
        SlotMarks slot = _slots[_current];
        if (slot.Used >= MarksPerFrame)
        {
            _dropped++;
            return;
        }
        _context.Api.CmdWriteTimestamp(commands, PipelineStageFlags.BottomOfPipeBit, slot.Pool, slot.Used);
        slot.Labels[slot.Used++] = LabelId(label);
    }

    /// <summary>Counts indices drawn in the open section, reported as thousands of triangles per frame.</summary>
    public void AddIndices(long count)
    {
        if (_current >= 0) _indices[LabelId(_openLabel)] += count;
    }

    private int LabelId(string label)
    {
        if (_labelIds.TryGetValue(label, out int id)) return id;
        string token = Token(label);
        id = _labels.IndexOf(token);
        if (id < 0)
        {
            id = _labels.Count;
            _labels.Add(token);
        }
        _labelIds.Add(label, id);
        if (_sums.Length <= id)
        {
            Array.Resize(ref _sums, _sums.Length * 2);
            Array.Resize(ref _indices, _sums.Length);
        }
        return id;
    }

    /// <summary>Lower-case letters, digits and underscores, the stats log's key alphabet.</summary>
    internal static string Token(string label)
    {
        var token = new StringBuilder(label.Length);
        foreach (char c in label)
        {
            char lower = char.ToLowerInvariant(c);
            token.Append(lower is >= 'a' and <= 'z' or >= '0' and <= '9' ? lower : '_');
        }
        return token.Length == 0 ? "unnamed" : token.ToString();
    }

    private void Harvest(SlotMarks slot)
    {
        uint used = slot.Used;
        slot.Used = 0;
        if (used < 2) return;
        fixed (ulong* results = _results)
        {
            Result result = _context.Api.GetQueryPoolResults(_context.Device, slot.Pool, 0, used,
                (nuint)(used * 2 * sizeof(ulong)), results, 2 * sizeof(ulong),
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);
            if (result != Result.Success && result != Result.NotReady) return;
        }
        for (uint i = 0; i < used; i++)
            if (_results[i * 2 + 1] == 0) return;
        for (uint i = 0; i + 1 < used; i++)
        {
            ulong ticks = (_results[(i + 1) * 2] - _results[i * 2]) & _validMask;
            _sums[slot.Labels[i]] += ticks * _nanosecondsPerTick / 1e6;
        }
        _frameSum += ((_results[(used - 1) * 2] - _results[0]) & _validMask) * _nanosecondsPerTick / 1e6;
        _frames++;
    }

    /// <summary>
    /// <c>stats.gpu</c>: mean milliseconds per frame over the frames harvested
    /// since the last call, frame span first, then every section, largest first.
    /// Resets the sums.
    /// </summary>
    public string TakeLine()
    {
        long frames = _frames;
        var line = new StringBuilder("stats.gpu frames=").Append(frames.ToString(CultureInfo.InvariantCulture));
        if (frames > 0)
        {
            line.Append(" span_ms=").Append((_frameSum / frames).ToString("F3", CultureInfo.InvariantCulture));
            var order = new int[_labels.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => _sums[b].CompareTo(_sums[a]));
            foreach (int id in order)
            {
                if (_sums[id] <= 0) continue;
                line.Append(' ').Append(_labels[id]).Append("_ms=")
                    .Append((_sums[id] / frames).ToString("F3", CultureInfo.InvariantCulture));
            }
            if (_recordedFrames > 0)
                foreach (int id in order)
                {
                    if (_indices[id] <= 0) continue;
                    line.Append(' ').Append(_labels[id]).Append("_ktri=")
                        .Append((_indices[id] / 3.0 / 1000.0 / _recordedFrames).ToString("F1", CultureInfo.InvariantCulture));
                }
        }
        if (_dropped > 0) line.Append(" dropped_marks=").Append(_dropped.ToString(CultureInfo.InvariantCulture));
        Array.Clear(_sums);
        Array.Clear(_indices);
        _recordedFrames = 0;
        _frameSum = 0;
        _frames = 0;
        _dropped = 0;
        return line.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (SlotMarks slot in _slots)
            _context.Api.DestroyQueryPool(_context.Device, slot.Pool, null);
    }
}
