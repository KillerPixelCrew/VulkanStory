using System;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal bool ResidentEntitiesEnabled { get; set; }

    internal int BeginEntityPose(int floatCount)
    {
        // TODO(E-render): reserve this frame's current-pose ring range.
        return 0;
    }

    internal Span<float> EntityPoseSpan(int offset, int floatCount)
    {
        // TODO(E-render): expose the reserved pose range.
        return Span<float>.Empty;
    }

    internal bool CaptureEntityRow(NativePipeline pipeline, int meshId,
        ReadOnlySpan<NativeTexture> textures, long entityKey, int subMesh,
        double centerX, double centerY, double centerZ, float radius, int poseOffset)
    {
        // TODO(E-render): capture uniforms, push bytes and texture slots without recording a draw.
        return false;
    }

    internal void ReleaseEntityRows(long entityKey)
    {
        // TODO(E-render): release all persistent rows for this entity.
    }

    internal bool DrawEntityPass(int passKind /* 0 opaque, 1 afterOit, 2 shadowNear, 3 shadowFar */,
        in TerrainView view)
    {
        // TODO(E-render): cull and record instanced indirect draws for this frame's captured rows.
        return false;
    }
}
