using Silk.NET.Vulkan;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private void SetStencil(int face, int operation, int a, int b = 0, uint mask = 0)
    {
        if (face is not (0x0404 or 0x0405 or 0x0408)) Error(0x0500);
        StencilOpState Change(StencilOpState value)
        {
            if (operation == 0)
            {
                if (a < 0x0200 || a > 0x0207) Error(0x0500);
                value.CompareOp = (CompareOp)(a - 0x0200); value.Reference = (uint)Math.Clamp(b, 0, 255); value.CompareMask = mask;
            }
            else if (operation == 1) value.WriteMask = mask;
            else { value.FailOp = StencilOperation(a); value.DepthFailOp = StencilOperation(b); value.PassOp = StencilOperation(unchecked((int)mask)); }
            return value;
        }
        // Validate before changing either face so invalid input cannot leave half of the state applied.
        StencilOpState front = Change(State.StencilFront), back = Change(State.StencilBack);
        if (face != 0x0405) State.StencilFront = front;
        if (face != 0x0404) State.StencilBack = back;
    }
    private static StencilOp StencilOperation(int token) => token switch
    {
        0 => StencilOp.Zero, 0x1E00 => StencilOp.Keep, 0x1E01 => StencilOp.Replace,
        0x1E02 => StencilOp.IncrementAndClamp, 0x1E03 => StencilOp.DecrementAndClamp,
        0x150A => StencilOp.Invert, 0x8507 => StencilOp.IncrementAndWrap, 0x8508 => StencilOp.DecrementAndWrap,
        _ => throw new GlError(0x0500),
    };
    private static int StencilOperationToken(StencilOp operation) => operation switch
    {
        StencilOp.Zero => 0, StencilOp.Keep => 0x1E00, StencilOp.Replace => 0x1E01,
        StencilOp.IncrementAndClamp => 0x1E02, StencilOp.DecrementAndClamp => 0x1E03,
        StencilOp.Invert => 0x150A, StencilOp.IncrementAndWrap => 0x8507, StencilOp.DecrementAndWrap => 0x8508,
        _ => throw new InvalidOperationException(),
    };
    private int StencilQuery(int token) => token switch
    {
        0x0B90 => State.StencilTest ? 1 : 0, 0x0B91 => C.ClearStencil,
        0x0B92 => (int)State.StencilFront.CompareOp + 0x0200, 0x0B97 => (int)State.StencilFront.Reference,
        0x0B93 => unchecked((int)State.StencilFront.CompareMask), 0x0B98 => unchecked((int)State.StencilFront.WriteMask),
        0x0B94 => StencilOperationToken(State.StencilFront.FailOp), 0x0B95 => StencilOperationToken(State.StencilFront.DepthFailOp), 0x0B96 => StencilOperationToken(State.StencilFront.PassOp),
        0x8800 => (int)State.StencilBack.CompareOp + 0x0200, 0x8CA3 => (int)State.StencilBack.Reference,
        0x8CA4 => unchecked((int)State.StencilBack.CompareMask), 0x8CA5 => unchecked((int)State.StencilBack.WriteMask),
        0x8801 => StencilOperationToken(State.StencilBack.FailOp), 0x8802 => StencilOperationToken(State.StencilBack.DepthFailOp), 0x8803 => StencilOperationToken(State.StencilBack.PassOp),
        _ => throw new GlError(0x0500),
    };
}
