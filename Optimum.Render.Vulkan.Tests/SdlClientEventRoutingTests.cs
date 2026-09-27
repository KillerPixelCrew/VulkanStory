using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

[Collection("Hidden Vulkan Window")]
public sealed unsafe class SdlClientEventRoutingTests
{
    private sealed class Probe : KeyEventHandler, MouseEventHandler
    {
        public readonly List<string> Events = new();
        public void OnKeyDown(KeyEvent e) => Events.Add("key-down:" + e.KeyCode);
        public void OnKeyUp(KeyEvent e) => Events.Add("key-up:" + e.KeyCode);
        public void OnKeyPress(KeyEvent e) => Events.Add("text:" + e.KeyChar);
        public void OnMouseDown(MouseEvent e) => Events.Add("mouse-down:" + e.Button);
        public void OnMouseUp(MouseEvent e) => Events.Add("mouse-up:" + e.Button);
        public void OnMouseMove(MouseEvent e) { }
        public void OnMouseWheel(Vintagestory.API.Client.MouseWheelEventArgs e) { }
    }

    [SkippableFact]
    public void NativeSdlQueueDispatchesToTheGamesPhysicalInputHandlers()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SDL window input probe runs on Windows.");
        using var window = SdlVulkanWindowHost.Create("SDL3 client input probe", 128, 96, hidden: true);
        uint windowId = window.WindowId;
        Assert.NotEqual(0u, windowId);
        var platform = new VulkanClientPlatform(null!) { SdlWindowId = windowId };
        var probe = new Probe();
        platform.keyEventHandlers.Add(probe);
        platform.mouseEventHandlers.Add(probe);
        SdlEventPump.Drain();

        byte* input = stackalloc byte[128];
        new Span<byte>(input, 128).Clear();
        *(uint*)input = SdlEventPump.KeyDown;
        *(uint*)(input + 16) = windowId;
        *(int*)(input + 24) = 26; // SDL_SCANCODE_W
        Assert.True(SDL_PushEvent(input));
        *(uint*)input = SdlEventPump.MouseButtonDown;
        input[24] = 3; // SDL_BUTTON_RIGHT, unlike GLFW's button order
        *(float*)(input + 28) = 24;
        *(float*)(input + 32) = 32;
        Assert.True(SDL_PushEvent(input));
        *(uint*)input = SdlEventPump.WindowFocusLost;
        Assert.True(SDL_PushEvent(input));

        SdlEventPump.Drain(platform.DispatchSdlInput);
        Assert.Equal(new[] {
            "key-down:" + (int)GlKeys.W, "mouse-down:Right",
            "key-up:" + (int)GlKeys.W, "mouse-up:Right"
        }, probe.Events);
    }

    [Fact]
    public void ImeEventsWithoutTheOriginalFocusedFieldAreDiscarded()
    {
        var platform = new VulkanClientPlatform(null!) { SdlWindowId = 42 };
        var probe = new Probe();
        platform.keyEventHandlers.Add(probe);
        typeof(VulkanClientPlatform).GetField("sdlTextInputActive",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(platform, true);

        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.TextEditing, 0, 42,
            Text: "にほん", EditStart: 2, EditLength: 1));
        Assert.Equal("", platform.SdlCompositionTextForTests);
        Assert.Empty(probe.Events);

        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.TextInput, 0, 42,
            Text: "日本"));
        Assert.Equal("", platform.SdlCompositionTextForTests);
        Assert.Empty(probe.Events);

        typeof(VulkanClientPlatform).GetField("sdlCompositionText",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(platform, "かな");
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.KeyDown, 0, 42,
            Key: GlKeys.Escape));
        Assert.Equal("", platform.SdlCompositionTextForTests);
        Assert.Empty(probe.Events);
        typeof(VulkanClientPlatform).GetField("sdlCompositionText",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(platform, "かな");
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.WindowFocusLost, 0, 42));
        Assert.Equal("", platform.SdlCompositionTextForTests);
    }

    [Fact]
    public void FileDropCopiesUtf8PathAndRoutesOnlyToItsWindow()
    {
        var platform = new VulkanClientPlatform(null!) { SdlWindowId = 42 };
        var dropped = new List<string>();
        platform.fileDropEventHandler = dropped.Add;
        byte* input = stackalloc byte[128];
        new Span<byte>(input, 128).Clear();
        *(uint*)input = SdlEventPump.DropFile;
        nint path = Marshal.StringToCoTaskMemUTF8(@"C:\mods\Öfen.zip");
        try
        {
            *(nint*)(input + 40) = path;
            *(uint*)(input + 16) = 99;
            SdlInputEvent wrongWindow = SdlEventPump.Decode((nint)input)!.Value;
            platform.DispatchSdlInput(wrongWindow);
            *(uint*)(input + 16) = 42;
            SdlInputEvent correctWindow = SdlEventPump.Decode((nint)input)!.Value;
            Assert.Equal(@"C:\mods\Öfen.zip", correctWindow.Text);
            Marshal.FreeCoTaskMem(path);
            path = 0;
            platform.DispatchSdlInput(correctWindow);
        }
        finally { if (path != 0) Marshal.FreeCoTaskMem(path); }
        Assert.Equal(new[] { @"C:\mods\Öfen.zip" }, dropped);
    }

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_PushEvent(void* eventData);
}
