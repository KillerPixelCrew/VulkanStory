using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal sealed class GlTimestampQuery(VulkanContext context, QueryPool pool, bool elapsed, double period, ulong mask) : IDisposable
    {
        internal readonly QueryPool Pool = pool;
        internal readonly bool Elapsed = elapsed;
        internal readonly double Period = period;
        internal readonly ulong Mask = mask;
        internal ulong FrameValue, TransferValue, Value;
        internal bool Ended, Resolved;
        public void Dispose() => context.Api.DestroyQueryPool(context.Device, Pool, null);
    }

    internal GlTimestampQuery BeginGlTimestamp(bool elapsed)
    {
        RequireFrameLifetime();
        uint count = 0;
        _context.Api.GetPhysicalDeviceQueueFamilyProperties(_context.PhysicalDevice, ref count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* properties = families) _context.Api.GetPhysicalDeviceQueueFamilyProperties(_context.PhysicalDevice, ref count, properties);
        uint bits = families[_context.GraphicsQueueFamily].TimestampValidBits;
        double period = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits.TimestampPeriod;
        if (bits == 0 || period <= 0) throw new NotSupportedException("The graphics queue has no GPU timestamp counter.");
        var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = 2 };
        VulkanResult.Check(_context.Api.CreateQueryPool(_context.Device, &info, null, out QueryPool pool), "creating a GL timer query");
        var query = new GlTimestampQuery(_context, pool, elapsed, period, bits >= 64 ? ulong.MaxValue : (1UL << (int)bits) - 1);
        try { WriteGlTimestamp(query, 0, reset: true); query.Ended = !elapsed; return query; }
        catch { _frames.DeferDeletion(query); throw; }
    }

    private void WriteGlTimestamp(GlTimestampQuery query, uint index, bool reset)
    {
        EndNativePass();
        if (_frameActive) { _targets.EndPass(Commands); _targets.EndRendering(Commands); }
        CommandBuffer command = _frameActive ? Commands : _uploads.BeginRecording(inlineInFrame: false);
        try
        {
            if (reset) _context.Api.CmdResetQueryPool(command, query.Pool, 0, 2);
            _context.Api.CmdWriteTimestamp(command, PipelineStageFlags.BottomOfPipeBit, query.Pool, index);
            if (_frameActive) query.FrameValue = _frames.Current.FrameValue;
        }
        finally { if (!_frameActive) _uploads.EndRecording(); }
        if (!_frameActive) query.TransferValue = _uploads.SubmitStandalone();
    }
    internal void EndGlTimestamp(GlTimestampQuery query)
    {
        if (!query.Elapsed || query.Ended) throw new InvalidOperationException("Timer query is not active.");
        WriteGlTimestamp(query, 1, reset: false);
        query.Ended = true;
    }
    internal void RetireGlTimestamp(GlTimestampQuery query) => _frames.DeferDeletion(query);

    internal bool ReadGlTimestamp(GlTimestampQuery query, bool wait, out ulong nanoseconds)
    {
        nanoseconds = query.Value;
        if (query.Resolved) return true;
        if (!query.Ended) return false;
        if (wait)
        {
            RequireFrameLifetime();
            if (query.FrameValue > _frames.Timeline.FrameSignalled)
            {
                if (!_frameActive) throw new InvalidOperationException("The timer query has no submitted recording owner.");
                SubmitPartial();
            }
            _frames.Timeline.WaitForFrame(query.FrameValue, WaitSite.Readback);
            _frames.Timeline.WaitForTransfer(query.TransferValue, WaitSite.Readback);
        }
        if (_frames.Timeline.FrameCompleted < query.FrameValue || _frames.Timeline.TransferCompleted < query.TransferValue) return false;
        ulong* data = stackalloc ulong[4];
        new Span<ulong>(data, 4).Clear();
        uint queryCount = query.Elapsed ? 2u : 1u;
        QueryResultFlags flags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit;
        if (wait) flags |= QueryResultFlags.ResultWaitBit;
        Result status = _context.Api.GetQueryPoolResults(_context.Device, query.Pool, 0, queryCount,
            (nuint)(queryCount * 2 * sizeof(ulong)), data, 2 * sizeof(ulong), flags);
        if (status == Result.NotReady) return false;
        VulkanResult.Check(status, "reading a GL timer query");
        if (data[1] == 0 || query.Elapsed && data[3] == 0) return false;
        ulong ticks = query.Elapsed ? (data[2] - data[0]) & query.Mask : data[0] & query.Mask;
        double scaled = ticks * query.Period;
        query.Value = scaled >= ulong.MaxValue ? ulong.MaxValue : (ulong)Math.Max(0, Math.Round(scaled));
        query.Resolved = true;
        nanoseconds = query.Value;
        return true;
    }

    internal void FinishGlWork()
    {
        RequireFrameLifetime();
        EndNativePass();
        if (_frameActive) SubmitPartial(); else _uploads.SubmitStandalone();
        _frames.Timeline.WaitForFrame(_frames.Timeline.FrameSignalled, WaitSite.Readback);
        _frames.Timeline.WaitForTransfer(_frames.Timeline.TransferSignalled, WaitSite.Readback);
    }
    internal bool CanBeginGlOcclusion(int query) => _frameActive && _queryRing.CanBegin(query);
    internal ulong GetGlOcclusionResult(int query, bool wait)
    {
        if (wait && !_queryRing.IsResultAvailable(query)) FinishGlWork();
        return _queryRing.GetResult64(query);
    }
}
