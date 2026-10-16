using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    public const int MaxGlClipPlanes = 8;

    /// <summary>Snapshots the selected GL context's clipping state for subsequent draws.</summary>
    public void SetGlClipPlanes(ReadOnlySpan<float> planes, int mask)
    {
        if (planes.Length != MaxGlClipPlanes * 4) throw new ArgumentException("Eight four-component planes are required.", nameof(planes));
        FrameGlobals.TryGetMember(GlClipShader.Planes, out UniformMember member);
        WriteFrameGlobal(member.Offset, MemoryMarshal.AsBytes(planes));
        SetGlClipMask(mask);
    }

    private void SetGlClipMask(int mask)
    {
        FrameGlobals.TryGetMember(GlClipShader.Mask, out UniformMember member);
        WriteFrameGlobal(member.Offset, new ReadOnlySpan<byte>(&mask, sizeof(int)));
    }

    // GL clears and image blits are not subject to vertex clipping. Their internal
    // raster implementation must bind its own frame snapshot, then restore GL state.
    private int DisableGlClipping()
    {
        FrameGlobals.TryGetMember(GlClipShader.Mask, out UniformMember member);
        int mask = MemoryMarshal.Read<int>(_frameGlobals.AsSpan(member.Offset, sizeof(int)));
        SetGlClipMask(0);
        return mask;
    }
}
