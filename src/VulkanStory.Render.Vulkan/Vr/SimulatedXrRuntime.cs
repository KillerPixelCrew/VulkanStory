using System.Diagnostics;
using System.Numerics;
using VulkanStory.Render.Vulkan.Core;
using V = Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Vr;

/// <summary>Fixed stereo views backed by renderer-owned images, without an OpenXR loader.</summary>
/// <remarks>
/// Eyes sit at (-0.032, 0, 0) and (+0.032, 0, 0) metres with identity orientation (64 mm IPD).
/// Left/right FOVs in radians are (-0.907, 0.785) and (-0.785, 0.907); both use up 0.8, down -0.85.
/// The default eye extent is 2064 x 2208 and the fixed reference revision is 1.
/// </remarks>
internal sealed class SimulatedXrRuntime : XrRuntime
{
    private readonly OpenXrEyeView[] eyes = [
        new(new Vector3(-0.032f, 0, 0), Quaternion.Identity, -0.907f, 0.785f, 0.8f, -0.85f),
        new(new Vector3(0.032f, 0, 0), Quaternion.Identity, -0.785f, 0.907f, 0.8f, -0.85f) ];
    private readonly VulkanImage?[] images = new VulkanImage?[2];
    private readonly bool[] copied = new bool[2];
    private long? previousBegin;
    private bool frameOpen, projectionFrame, disposed;
    internal override int ReferenceRevision => 1;

    private SimulatedXrRuntime(int width, int height)
    {
        Width = width; Height = height;
        ColorFormat = V.Format.B8G8R8A8Srgb;
        Running = true;
        RuntimeName = "simulated";
        Status = "simulated: waiting for the Vulkan device.";
        FrameStatus = "Waiting for the first simulated frame.";
    }

    internal static SimulatedXrRuntime Create((int Width, int Height) eyeSize = default)
    {
        if (eyeSize == (0, 0)) eyeSize = (2064, 2208);
        if (eyeSize.Width <= 0 || eyeSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(eyeSize), "Simulated eye dimensions must both be positive.");
        return new SimulatedXrRuntime(eyeSize.Width, eyeSize.Height);
    }

    internal override void Attach(VulkanContext device)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (images[0] != null) throw new InvalidOperationException("The simulated XR runtime is already attached.");
        if ((uint)Width > device.Capabilities.MaxImageDimension2D || (uint)Height > device.Capabilities.MaxImageDimension2D)
            throw new NotSupportedException("The simulated eye extent exceeds the Vulkan device's image limit.");
        const V.ImageUsageFlags usage = V.ImageUsageFlags.TransferDstBit | V.ImageUsageFlags.TransferSrcBit |
            V.ImageUsageFlags.ColorAttachmentBit;
        try
        {
            for (int eye = 0; eye < 2; eye++)
                images[eye] = new VulkanImage(device, (uint)Width, (uint)Height, ColorFormat, usage, V.ImageAspectFlags.ColorBit);
        }
        catch { Dispose(); throw; }
        Status = "simulated: fixed stereo views ready.";
    }

    internal override void PollEvents() { }

    internal override void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frameOpen) throw new InvalidOperationException("The previous simulated XR frame has not ended.");
        if (images[0] == null) throw new InvalidOperationException("The simulated XR runtime is not attached.");
        long now = Stopwatch.GetTimestamp();
        FrameDeltaSeconds = previousBegin is { } previous
            ? (float)Math.Clamp((now - previous) / (double)Stopwatch.Frequency, 0, .1) : null;
        previousBegin = now;
        frameOpen = projectionFrame = ShouldRender = true;
        Array.Clear(copied);
        FrameStatus = "Simulated eye images ready.";
    }

    internal override OpenXrEyeView Eye(int eye) => eyes[eye];
    internal override V.Image Image(int eye) => images[eye]?.Handle ??
        throw new InvalidOperationException("The simulated XR eye image is not attached.");
    internal override void MarkCopied(int eye) => copied[eye] = true;
    internal override void UseHeadLockedMenu() => projectionFrame = false;
    internal override void SuppressFrame()
    {
        ShouldRender = false;
        FrameStatus = "Scene transition/loading hold; simulated frame suppressed.";
    }

    internal override void EndFrame()
    {
        if (!frameOpen) return;
        if (ShouldRender)
            FrameStatus = copied[0] && (!projectionFrame || copied[1])
                ? projectionFrame ? "Simulated stereo scene copied." : "Simulated head-locked menu copied."
                : "Simulated frame ended without complete eye copies.";
        frameOpen = projectionFrame = ShouldRender = false;
        Array.Clear(copied);
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        EndFrame();
        Running = ShouldRender = false;
        FrameDeltaSeconds = null;
        // The Vulkan device owner drains GPU work before releasing these images.
        for (int eye = 0; eye < 2; eye++)
        {
            images[eye]?.Dispose();
            images[eye] = null;
        }
        Status = "simulated: disposed.";
        FrameStatus = "Simulated stereo stopped.";
    }
}
