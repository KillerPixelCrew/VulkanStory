using System.Runtime.InteropServices;
using VulkanStory.Platform.Sdl;
using Xunit;

namespace VulkanStory.Platform.Sdl.Tests;

// Ported from the source renderer's SdlEventPumpTests. The game adapter now
// maps physical scancodes to GlKeys after the SDL boundary.
/// <summary>Checks SDL event memory offsets and copied text for synthetic keyboard, pointer, display, and touch buffers.</summary>
/// <remarks>Exercises buffer decoding, not native queue pumping or physical input.</remarks>
public sealed unsafe class SdlEventDecodingTests
{
    [Fact]
    public void PhysicalKeyAndUtf8TextRemainSeparate()
    {
        byte* buffer = stackalloc byte[128];
        new Span<byte>(buffer, 128).Clear();
        *(uint*)buffer = SdlEventPump.KeyDown;
        *(ulong*)(buffer + 8) = 12345;
        *(uint*)(buffer + 16) = 9;
        *(int*)(buffer + 24) = 26; // physical W key
        *(ushort*)(buffer + 32) = 3;
        buffer[37] = 1;
        SdlInputEvent key = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal(26, key.Scancode);
        Assert.Equal(12345UL, key.TimestampNanoseconds);
        Assert.Equal(9u, key.WindowId);
        Assert.Equal((ushort)3, key.Modifiers);
        Assert.True(key.Repeat);

        nint utf8 = Marshal.StringToCoTaskMemUTF8("ä猫");
        try
        {
            *(uint*)buffer = SdlEventPump.TextInput;
            *(nint*)(buffer + 24) = utf8;
            SdlInputEvent text = SdlEventPump.Decode((nint)buffer)!.Value;
            Assert.Equal("ä猫", text.Text);
            Assert.Equal(0, text.Scancode);
        }
        finally { Marshal.FreeCoTaskMem(utf8); }

        nint preedit = Marshal.StringToCoTaskMemUTF8("にほん");
        try
        {
            *(uint*)buffer = SdlEventPump.TextEditing;
            *(nint*)(buffer + 24) = preedit;
            *(int*)(buffer + 32) = 2;
            *(int*)(buffer + 36) = 1;
            SdlInputEvent editing = SdlEventPump.Decode((nint)buffer)!.Value;
            Assert.Equal("にほん", editing.Text);
            Assert.Equal(2, editing.EditStart);
            Assert.Equal(1, editing.EditLength);
            Assert.Equal(9u, editing.WindowId);
        }
        finally { Marshal.FreeCoTaskMem(preedit); }
    }

    [Fact]
    public void PointerDisplayAndTouchLayoutsKeepTheirOriginalFields()
    {
        byte* buffer = stackalloc byte[128];
        new Span<byte>(buffer, 128).Clear();
        *(uint*)buffer = SdlEventPump.MouseMotion;
        *(uint*)(buffer + 16) = 7;
        *(float*)(buffer + 28) = 40.5f;
        *(float*)(buffer + 32) = 50.25f;
        *(float*)(buffer + 36) = -2.5f;
        *(float*)(buffer + 40) = 3.5f;
        SdlInputEvent mouse = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal((40.5f, 50.25f, -2.5f, 3.5f),
            (mouse.X, mouse.Y, mouse.DeltaX, mouse.DeltaY));

        *(uint*)buffer = SdlEventPump.WindowResized;
        *(int*)(buffer + 20) = 1280;
        *(int*)(buffer + 24) = 800;
        SdlInputEvent resized = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal((1280, 800, 7u), (resized.Data1, resized.Data2, resized.WindowId));

        *(uint*)buffer = SdlEventPump.DisplayFirst;
        *(uint*)(buffer + 16) = 55;
        *(int*)(buffer + 20) = 2;
        SdlInputEvent display = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal((55u, 0u, 2), (display.DisplayId, display.WindowId, display.Data1));

        *(uint*)buffer = SdlEventPump.FingerMotion;
        *(ulong*)(buffer + 16) = 0x1122334455667788;
        *(ulong*)(buffer + 24) = 0x8877665544332211;
        *(float*)(buffer + 32) = 0.25f;
        *(float*)(buffer + 36) = 0.75f;
        *(float*)(buffer + 40) = 0.05f;
        *(uint*)(buffer + 52) = 42;
        SdlInputEvent finger = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal(42u, finger.WindowId);
        Assert.Equal(0x1122334455667788UL, finger.TouchId);
        Assert.Equal(0x8877665544332211UL, finger.FingerId);
        Assert.Equal((0.25f, 0.75f, 0.05f), (finger.X, finger.Y, finger.DeltaX));
    }
}
