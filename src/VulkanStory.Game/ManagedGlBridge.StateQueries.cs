using System.Reflection;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private static void CheckColorSlot(int index) { if ((uint)index >= StatedRenderState.MaxColorAttachments) Error(0x0501); }
    private bool IndexedEnabled(int token, int index)
    {
        CheckColorSlot(index);
        return token switch { 0x0BE2 => State.SlotBlendEnabled(index), 0x0C11 when index == 0 => State.ScissorEnabled, _ => throw new GlError(0x0500) };
    }
    private void SetIndexedEnabled(int token, int index, bool enabled)
    {
        CheckColorSlot(index);
        if (token == 0x0BE2) State.SetSlotBlendEnabled(index, enabled);
        else if (token == 0x0C11 && index == 0) State.ScissorEnabled = enabled;
        else Error(0x0500);
    }
    private object? QueryIndexed(MethodInfo method, object?[] a, bool boolean)
    {
        int token = I(a[0]), index = I(a[1]);
        int[] values;
        if (token == 0x8919)
        { if ((uint)index >= StatedRenderState.MaxTextureUnits) Error(0x0501); values = [State.SamplerAt(index)]; }
        else
        {
            CheckColorSlot(index);
            var blend = State.RawBlend(index);
            ColorComponentFlags mask = State.SlotColorMask(index);
            values = token switch
            {
                0x0BE2 => [State.SlotBlendEnabled(index) ? 1 : 0],
                0x0C23 => [(mask & ColorComponentFlags.RBit) != 0 ? 1 : 0, (mask & ColorComponentFlags.GBit) != 0 ? 1 : 0,
                    (mask & ColorComponentFlags.BBit) != 0 ? 1 : 0, (mask & ColorComponentFlags.ABit) != 0 ? 1 : 0],
                0x8009 => [BlendEquationToken(blend.ColorOp)], 0x883D => [BlendEquationToken(blend.AlphaOp)],
                0x80C9 => [BlendFactorToken(blend.SrcColor)], 0x80C8 => [BlendFactorToken(blend.DstColor)],
                0x80CB => [BlendFactorToken(blend.SrcAlpha)], 0x80CA => [BlendFactorToken(blend.DstAlpha)],
                0x0C01 => [DrawBufferToken(index)], _ => throw new GlError(0x0500),
            };
        }
        WriteOutput(method, a, 2, values, boolean);
        return null;
    }

    private object? FloatQuery(MethodInfo method, object?[] a)
    {
        float[] values = I(a[0]) switch
        { 0x0C22 => C.ClearColor, 0x0B73 => [C.ClearDepth], 0x8005 => [State.BlendConstants.X, State.BlendConstants.Y, State.BlendConstants.Z, State.BlendConstants.W], 0x8038 => [State.PolygonOffsetFactor], 0x2A00 => [State.PolygonOffsetUnits], _ => IntegerValues(I(a[0])).Select(value => (float)value).ToArray() };
        if (a.Length > 1) WriteFloats(method, a, 1, values);
        return method.ReturnType == typeof(void) ? null : values[0];
    }
    private static void WriteFloats(MethodInfo method, object?[] a, int index, float[] values)
    {
        Type parameter = method.GetParameters()[index].ParameterType;
        if (parameter.IsByRef && a[index] is not IntPtr)
        { if (values.Length != 1) throw new NotSupportedException("Vector queries require an array/pointer destination."); a[index] = values[0]; return; }
        if (a[index] is float[] array)
        { if (array.Length < values.Length) Error(0x0501); values.CopyTo(array, 0); return; }
        nint pointer = Pointer(a[index]); if (pointer == 0) Error(0x0501);
        Marshal.Copy(values, 0, pointer, values.Length);
    }

    // Advertise only implemented extension contracts. All enumeration APIs share
    // this list so native loaders and managed consumers see the same capabilities.
    private static readonly string[] GlExtensions = ["GL_ARB_vertex_array_object"];
    private static string StringQuery(int token) => token switch
    { 0x1F00 => "VulkanStory", 0x1F01 => "VulkanStory Vulkan compatibility renderer", 0x1F02 => "3.3 VulkanStory", 0x1F03 => string.Join(' ', GlExtensions), 0x8B8C => "3.30", _ => throw new GlError(0x0500) };
    private static string IndexedStringQuery(int token, int index)
    {
        if (token != 0x1F03) Error(0x0500);
        if ((uint)index >= GlExtensions.Length) Error(0x0501);
        return GlExtensions[index];
    }

    private object? DoubleQuery(MethodInfo method, object?[] a)
    {
        double[] values = I(a[0]) switch
        {
            0x0C22 => C.ClearColor.Select(value => (double)value).ToArray(),
            0x0B73 => [C.ClearDepth],
            0x8005 => [State.BlendConstants.X, State.BlendConstants.Y, State.BlendConstants.Z, State.BlendConstants.W],
            0x8038 => [State.PolygonOffsetFactor], 0x2A00 => [State.PolygonOffsetUnits],
            _ => IntegerValues(I(a[0])).Select(value => (double)value).ToArray()
        };
        if (a.Length > 1) WriteDoubles(method, a, 1, values);
        return method.ReturnType == typeof(void) ? null : values[0];
    }
    private static void WriteDoubles(MethodInfo method, object?[] a, int index, double[] values)
    {
        Type parameter = method.GetParameters()[index].ParameterType;
        if (parameter.IsByRef && a[index] is not IntPtr)
        { if (values.Length != 1) throw new NotSupportedException("Vector queries require an array/pointer destination."); a[index] = values[0]; return; }
        if (a[index] is double[] array)
        { if (array.Length < values.Length) Error(0x0501); values.CopyTo(array, 0); return; }
        nint pointer = Pointer(a[index]); if (pointer == 0) Error(0x0501);
        Marshal.Copy(values, 0, pointer, values.Length);
    }
    private static string DebugText(object? value, int length)
    {
        string text = (string)value!;
        if (length < -1) Error(0x0501);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        int count = length == -1 ? bytes.Length : Math.Min(length, bytes.Length);
        if (length == -1) { int end = bytes.AsSpan().IndexOf((byte)0); if (end >= 0) count = end; }
        return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
    }
    private void PushDebugGroup(object?[] a)
    {
        if (I(a[0]) is not (0x8249 or 0x824A)) Error(0x0500);
        if (C.DebugGroups.Count >= 63) Error(0x0503);
        C.DebugGroups.Push(DebugText(a[3], I(a[2])));
    }
    private void DebugMessage(object?[] a)
    {
        if (I(a[0]) is not (0x8249 or 0x824A)) Error(0x0500);
        string text = DebugText(a[5], I(a[4]));
        if (drawTracePath is not null) WriteTrace(new { @event = "gl-debug-message", message = text, id = I(a[2]), groups = C.DebugGroups.Reverse().ToArray() });
    }

    private static float[] ReadFloatValues(object? value, int count)
    {
        if (value is float[] array) { if (array.Length < count) Error(0x0501); return array[..count]; }
        if (value is float scalar && count == 1) return [scalar];
        nint pointer = Pointer(value); if (pointer == 0) Error(0x0501);
        var values = new float[count]; Marshal.Copy(pointer, values, 0, count); return values;
    }
    private void ClearTarget(int mask)
    {
        if ((mask & ~0x4500) != 0) Error(0x0501);
        if (GlFramebufferStatus(Target) != 0x8CD5) Error(0x0506);
        if ((mask & 0x4000) != 0) CheckGlColorEncoding();
        session.Graphics.ClearManagedGlTarget(Target, State, mask, C.ClearColor, C.ClearDepth);
        if ((mask & 0x0400) != 0) Device.ClearGlStencil(Target, C.ClearStencil, State.StencilFront.WriteMask, State.ScissorEnabled ? State.Scissor : null);
    }
    private void ClearBuffer(MethodInfo method, object?[] a)
    {
        int buffer = I(a[0]), slot = I(a[1]);
        if (GlFramebufferStatus(Target) != 0x8CD5) Error(0x0506);
        Type input = method.GetParameters()[2].ParameterType;
        bool integer = input.HasElementType && input.GetElementType() is { } element && (element == typeof(int) || element == typeof(uint));
        Rect2D? clip = State.ScissorEnabled ? State.Scissor : null;
        if (buffer == 0x1800)
        {
            CheckGlColorEncoding();
            CheckColorSlot(slot);
            float[] color = integer ? ReadInts(a[2], 4).Select(value => (float)value).ToArray() : ReadFloatValues(a[2], 4);
            if (State.DrawBufferAttachment(Target, slot) >= 0) Device.ClearGlColor(Target, slot, State.SlotColorMask(slot), color, clip);
        }
        else if (buffer == 0x1801)
        {
            if (integer) Error(0x0500); if (slot != 0) Error(0x0501);
            float value = ReadFloatValues(a[2], 1)[0];
            float depth = float.IsNaN(value) ? 0 : Math.Clamp(value, 0f, 1f);
            if (State.DepthWrite) Device.ClearGlDepth(Target, depth, clip);
        }
        else if (buffer == 0x1802)
        { if (!integer) Error(0x0500); if (slot != 0) Error(0x0501); Device.ClearGlStencil(Target, ReadInts(a[2], 1)[0], State.StencilFront.WriteMask, clip); }
        else Error(0x0500);
    }
}
