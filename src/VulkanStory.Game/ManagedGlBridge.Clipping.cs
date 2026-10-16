using System.Runtime.InteropServices;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private static int ClipIndex(int plane)
    {
        int index = plane - 0x3000;
        if ((uint)index >= VulkanStory.Render.Vulkan.VulkanDevice.MaxGlClipPlanes) Error(0x0500);
        return index;
    }

    private void ClipPlane(object?[] a)
    {
        int index = ClipIndex(I(a[0]));
        byte[] bytes = CopyBytes(a[1], 4 * sizeof(double), allowNull: false);
        // Legacy matrix entry points are not admitted by this bridge, so their
        // modelview remains the GL initial identity. Game shader matrices are
        // independent uniforms and must not transform the stored equation.
        System.Buffer.BlockCopy(bytes, 0, C.ClipPlanes, index * 4 * sizeof(double), bytes.Length);
        SyncClipping();
    }

    private void GetClipPlane(object?[] a)
    {
        int index = ClipIndex(I(a[0]));
        double[] equation = C.ClipPlanes.AsSpan(index * 4, 4).ToArray();
        if (a[1] is double[] output)
        {
            if (output.Length < 4) Error(0x0501);
            equation.CopyTo(output, 0);
        }
        else
        {
            IntPtr pointer = Pointer(a[1]);
            if (pointer == IntPtr.Zero) Error(0x0501);
            Marshal.Copy(equation, 0, pointer, 4);
        }
    }

    private void SyncClipping()
    {
        Span<float> planes = stackalloc float[32];
        for (int plane = 0; plane < 8; plane++)
        {
            // Multiplying a half-space equation by a positive scalar preserves
            // it, and avoids overflowing finite double coefficients on upload.
            double scale = 0;
            for (int i = 0; i < 4; i++) scale = Math.Max(scale, Math.Abs(C.ClipPlanes[plane * 4 + i]));
            if (scale == 0) scale = 1;
            for (int i = 0; i < 4; i++) planes[plane * 4 + i] = (float)(C.ClipPlanes[plane * 4 + i] / scale);
        }
        Device.SetGlClipPlanes(planes, C.ClipMask);
    }
}
