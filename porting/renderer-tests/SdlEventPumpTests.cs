using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

[Collection("Hidden Vulkan Window")]
public sealed unsafe class SdlEventPumpTests
{
    [Fact]
    public void KeyAndTextEventsKeepPhysicalHotkeysSeparateFromUtf8Text()
    {
        byte* buffer = stackalloc byte[128];
        new Span<byte>(buffer, 128).Clear();
        *(uint*)buffer = SdlEventPump.KeyDown;
        *(ulong*)(buffer + 8) = 12345;
        *(uint*)(buffer + 16) = 9;
        *(int*)(buffer + 24) = 26; // physical W key
        *(ushort*)(buffer + 32) = 0x0003; // Shift
        buffer[37] = 1; // repeat
        SdlInputEvent key = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal(GlKeys.W, key.Key);
        Assert.Equal((ulong)12345, key.TimestampNanoseconds);
        Assert.Equal((uint)9, key.WindowId);
        Assert.Equal((ushort)3, key.Modifiers);
        Assert.True(key.Repeat);

        nint utf8 = Marshal.StringToCoTaskMemUTF8("ä猫");
        try
        {
            *(uint*)buffer = SdlEventPump.TextInput;
            *(nint*)(buffer + 24) = utf8;
            SdlInputEvent text = SdlEventPump.Decode((nint)buffer)!.Value;
            Assert.Equal("ä猫", text.Text);
            Assert.Equal(GlKeys.Unknown, text.Key);
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
            Assert.Equal((uint)9, editing.WindowId);
        }
        finally { Marshal.FreeCoTaskMem(preedit); }
    }

    [Fact]
    public void MouseAndWindowEventsKeepCoordinatesAndResizeDimensions()
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
        Assert.Equal(40.5f, mouse.X);
        Assert.Equal(50.25f, mouse.Y);
        Assert.Equal(-2.5f, mouse.DeltaX);
        Assert.Equal(3.5f, mouse.DeltaY);

        *(uint*)buffer = 0x206; // SDL_EVENT_WINDOW_RESIZED
        *(int*)(buffer + 20) = 1280;
        *(int*)(buffer + 24) = 800;
        SdlInputEvent resized = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal(1280, resized.Data1);
        Assert.Equal(800, resized.Data2);
        Assert.Equal((uint)7, resized.WindowId);

        *(uint*)buffer = SdlEventPump.DisplayFirst;
        *(uint*)(buffer + 16) = 55;
        *(int*)(buffer + 20) = 2; // SDL_ORIENTATION_LANDSCAPE_FLIPPED
        SdlInputEvent display = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal(55u, display.DisplayId);
        Assert.Equal(0u, display.WindowId);
        Assert.Equal(2, display.Data1);
    }

    [Fact]
    public void TouchEventsUseTheirOwnWindowFieldAndSyntheticMouseId()
    {
        byte* buffer = stackalloc byte[128];
        new Span<byte>(buffer, 128).Clear();
        *(uint*)buffer = SdlEventPump.FingerMotion;
        *(ulong*)(buffer + 16) = 0x1122334455667788;
        *(ulong*)(buffer + 24) = 0x8877665544332211;
        *(float*)(buffer + 32) = 0.25f;
        *(float*)(buffer + 36) = 0.75f;
        *(float*)(buffer + 40) = 0.05f;
        *(uint*)(buffer + 52) = 42;
        SdlInputEvent finger = SdlEventPump.Decode((nint)buffer)!.Value;
        Assert.Equal((uint)42, finger.WindowId);
        Assert.Equal(0x1122334455667788UL, finger.TouchId);
        Assert.Equal(0x8877665544332211UL, finger.FingerId);
        Assert.Equal((0.25f, 0.75f, 0.05f), (finger.X, finger.Y, finger.DeltaX));

        *(uint*)buffer = SdlEventPump.MouseButtonDown;
        *(uint*)(buffer + 16) = 42;
        *(uint*)(buffer + 20) = SdlEventPump.TouchMouseId;
        buffer[24] = 1;
        Assert.Equal(SdlEventPump.TouchMouseId, SdlEventPump.Decode((nint)buffer)!.Value.MouseId);
    }

    [SkippableFact]
    public void NativeQueueRoutesWindowEventsWithoutLosingGamepadHotplug()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SDL window queue probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL3 event queue probe", 128, 96, hidden: true);
        SdlEventPump.Drain();
        byte* buffer = stackalloc byte[128];
        new Span<byte>(buffer, 128).Clear();
        uint windowId = SDL_GetWindowID(window.NativeHandle);
        Assert.NotEqual(0u, windowId);
        *(uint*)buffer = 0x206; // SDL_EVENT_WINDOW_RESIZED
        *(uint*)(buffer + 16) = windowId;
        *(int*)(buffer + 20) = 320;
        *(int*)(buffer + 24) = 240;
        Assert.True(SDL_PushEvent(buffer));
        *(uint*)buffer = SdlEventPump.GamepadAdded;
        Assert.True(SDL_PushEvent(buffer));
        *(uint*)buffer = SdlEventPump.GamepadButtonDown;
        *(int*)(buffer + 16) = 17;
        buffer[20] = 0;
        buffer[21] = 1;
        Assert.True(SDL_PushEvent(buffer));
        *(uint*)buffer = SdlEventPump.GamepadAxisMotion;
        *(int*)(buffer + 16) = 18;
        *(short*)(buffer + 24) = 12000; // idle/noise threshold
        Assert.True(SDL_PushEvent(buffer));
        *(int*)(buffer + 16) = 19;
        *(short*)(buffer + 24) = 22000;
        Assert.True(SDL_PushEvent(buffer));
        var received = new List<SdlInputEvent>();
        var order = new List<string>();
        var activity = new List<int>();
        Assert.True(SdlEventPump.Drain(input => { order.Add("event"); received.Add(input); },
            () => order.Add("pump"), activity.Add, () => order.Add("gamepads")));
        Assert.Equal(new[] { "gamepads", "pump" }, order.GetRange(0, 2));
        Assert.Single(order.FindAll(item => item == "pump"));
        Assert.Single(order.FindAll(item => item == "gamepads"));
        Assert.Contains(received, item => item.Type == 0x206 && item.WindowId == windowId &&
            item.Data1 == 320 && item.Data2 == 240);
        Assert.DoesNotContain(received, item => item.Type == SdlEventPump.GamepadAdded);
        Assert.Equal(new[] { 17, 19 }, activity);
    }

    [SkippableFact]
    public void WindowEventsContinueWhenGamepadSubsystemIsUnavailable()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SDL window queue probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL input fallback probe", 128, 96, hidden: true);
        SdlEventPump.Drain();
        var platform = new VulkanClientPlatform(null!) { SdlWindowId = window.WindowId };
        using var gamepad = new SdlGamepadInput(platform);
        typeof(SdlGamepadInput).GetField("unavailable", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(gamepad, true);
        byte* buffer = stackalloc byte[128];
        new Span<byte>(buffer, 128).Clear();
        *(uint*)buffer = SdlEventPump.KeyDown;
        *(uint*)(buffer + 16) = window.WindowId;
        *(int*)(buffer + 24) = 26; // W
        Assert.True(SDL_PushEvent(buffer));
        var received = new List<SdlInputEvent>();
        int pumps = 0;
        gamepad.Poll(received.Add, () => pumps++);
        Assert.Equal(1, pumps);
        Assert.Contains(received, input => input.Type == SdlEventPump.KeyDown && input.Key == GlKeys.W);
    }

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetWindowID(nint window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_PushEvent(void* eventData);
}
