using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>Resource and fence operations shared by the DX12 vendor bridges.</summary>
internal interface IDx12SharedRuntime
{
    int CreateSharedImage(uint width, uint height, Format format, bool writable,
        out nint sharedHandle, out nint resource);
    void ReleaseImage(nint resource);
    int CreateSharedFence(out nint sharedHandle);
}
