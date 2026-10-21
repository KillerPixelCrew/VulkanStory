using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

// Retained compute-output pool shared by the particle geometry and visual paths.
public sealed unsafe partial class VulkanDevice
{
    // Cached outputs outlive individual frame slots. Recycle native buffers only
    // through FrameRing's frame/transfer retirement, never at a cache eviction.
    // Active outputs keep their existing feature budgets; idle capacity is capped.
    private const ulong ComputeOutputIdleBudget = 32UL << 20;
    private readonly Dictionary<(ulong, BufferUsageFlags), Stack<VulkanBuffer>> _computeOutputIdle = new();
    private ulong _computeOutputIdleBytes;
    private int _computeOutputIdleCount;
    private bool _computeOutputPoolClosed;

    private VulkanBuffer RentComputeOutput(ulong bytes, BufferUsageFlags usage)
    {
        ulong capacity = 256;
        while (capacity < bytes) capacity <<= 1;
        var key = (capacity, usage);
        if (_computeOutputIdle.TryGetValue(key, out var available) && available.Count != 0)
        {
            var buffer = available.Pop();
            if (available.Count == 0) _computeOutputIdle.Remove(key);
            _computeOutputIdleBytes -= capacity; _computeOutputIdleCount--;
            buffer.ReuseComputeOutput(bytes);
            return buffer;
        }
        var output = new VulkanBuffer(_context, capacity, usage, MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
        output.ReuseComputeOutput(bytes);
        return output;
    }

    private sealed class RetiredComputeOutput(VulkanDevice owner, VulkanBuffer buffer) : IDisposable
    {
        public void Dispose() => owner.ReturnComputeOutputIdle(buffer);
    }

    private void RetireComputeOutput(VulkanBuffer buffer) =>
        _frames.DeferDeletion(new RetiredComputeOutput(this, buffer));

    private void ReturnComputeOutputIdle(VulkanBuffer buffer)
    {
        // Deferred disposals execute on the render thread after both timelines.
        if (_computeOutputPoolClosed || _computeOutputIdleCount >= 1024 ||
            buffer.Capacity > ComputeOutputIdleBudget - _computeOutputIdleBytes)
        { buffer.Dispose(); return; }
        var key = (buffer.Capacity, buffer.Usage);
        if (!_computeOutputIdle.TryGetValue(key, out var available)) _computeOutputIdle.Add(key, available = new());
        available.Push(buffer); _computeOutputIdleBytes += buffer.Capacity; _computeOutputIdleCount++;
    }

    internal void ResetWorldComputeGeometry()
    {
        ResetTerrainOcclusion();
        RetireParticleGeometry(all: true);
        RetireParticleVisuals(all: true);
    }

    private void DisposeComputeOutputsIdle()
    {
        _computeOutputPoolClosed = true;
        foreach (var available in _computeOutputIdle.Values)
            foreach (var buffer in available) buffer.Dispose();
        _computeOutputIdle.Clear(); _computeOutputIdleBytes = 0; _computeOutputIdleCount = 0;
    }
}
