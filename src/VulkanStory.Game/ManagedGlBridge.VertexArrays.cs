using System.Reflection;
using System.Runtime.InteropServices;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private object? VertexAttributeQuery(MethodInfo method, object?[] a)
    {
        int index = I(a[0]), token = I(a[1]);
        if ((uint)index >= 16) Error(0x0501);
        Array.Attributes.TryGetValue(index, out Attribute? attribute);
        int value = token switch
        {
            0x8622 => Array.Enabled.Contains(index) ? 1 : 0,
            0x8623 => attribute?.Components ?? 4,
            0x8624 => attribute?.Stride ?? 0,
            0x8625 => attribute?.Type ?? 0x1406,
            0x886A => attribute?.Normalized == true ? 1 : 0,
            0x889F => attribute?.Buffer?.Name ?? 0,
            0x88FD => attribute?.Integer == true ? 1 : 0,
            0x8626 => 0, // Current generic values are context state, initially (0,0,0,1).
            _ => throw new GlError(0x0500)
        };
        int[] values = token == 0x8626 ? [0, 0, 0, 1] : [value];
        Type scalar = method.GetParameters()[2].ParameterType.GetElementType()!;
        if (scalar == typeof(float)) WriteFloats(method, a, 2, values.Select(component => (float)component).ToArray());
        else if (scalar == typeof(double)) WriteDoubles(method, a, 2, values.Select(component => (double)component).ToArray());
        else WriteOutput(method, a, 2, values, false);
        return null;
    }

    private void VertexAttributePointerQuery(MethodInfo method, object?[] a)
    {
        int index = I(a[0]);
        if ((uint)index >= 16) Error(0x0501);
        if (I(a[1]) != 0x8645) Error(0x0500);
        nint value = Array.Attributes.TryGetValue(index, out Attribute? attribute) ? attribute.Offset : 0;
        if (method.GetParameters()[2].ParameterType.IsByRef) { a[2] = value; return; }
        if (a[2] is nint[] array)
        { if (array.Length == 0) Error(0x0501); array[0] = value; return; }
        nint destination = Pointer(a[2]); if (destination == 0) Error(0x0501);
        Marshal.WriteIntPtr(destination, value);
    }
}
