using System;
using System.Collections.Generic;
using System.Reflection;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Core;
using Optimum;
using Xunit;
using GlfwKeyModifiers = OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers;

namespace Optimum.Render.Vulkan.Tests;

public class ControllerInputArbitrationTests
{
    private sealed class Probe : KeyEventHandler, MouseEventHandler
    {
        public readonly List<string> Events = new();
        public KeyEvent? LastKeyDown;
        public MouseEvent? LastMouseMove;
        public Vintagestory.API.Client.MouseWheelEventArgs? LastWheel;
        public void OnKeyDown(KeyEvent e) { LastKeyDown = e; Events.Add("key-down:" + e.KeyCode); }
        public void OnKeyUp(KeyEvent e) => Events.Add("key-up:" + e.KeyCode);
        public void OnKeyPress(KeyEvent e) => Events.Add("text:" + e.KeyChar);
        public void OnMouseDown(MouseEvent e) => Events.Add("mouse-down:" + e.Button);
        public void OnMouseUp(MouseEvent e) => Events.Add("mouse-up:" + e.Button);
        public void OnMouseMove(MouseEvent e) => LastMouseMove = e;
        public void OnMouseWheel(Vintagestory.API.Client.MouseWheelEventArgs e) => LastWheel = e;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReleasingEitherKeySourceKeepsTheOtherHeld(bool controllerFirst)
    {
        var platform = new ClientPlatformWindows(null!);
        var probe = new Probe();
        platform.keyEventHandlers.Add(probe);
        var key = new KeyEvent { KeyCode = (int)GlKeys.W };

        if (controllerFirst)
        {
            platform.InjectControllerKey(key, true);
            PhysicalKey(platform, true);
            platform.InjectControllerKey(key, false);
            PhysicalKey(platform, false);
        }
        else
        {
            PhysicalKey(platform, true);
            platform.InjectControllerKey(key, true);
            PhysicalKey(platform, false);
            platform.InjectControllerKey(key, false);
        }

        Assert.Equal(new[] { "key-down:" + (int)GlKeys.W, "key-up:" + (int)GlKeys.W }, probe.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SdlPhysicalKeyAndControllerShareTheSameHoldArbitration(bool controllerFirst)
    {
        var platform = new ClientPlatformWindows(null!);
        var probe = new Probe();
        platform.keyEventHandlers.Add(probe);
        var key = new KeyEvent { KeyCode = (int)GlKeys.W };
        if (controllerFirst)
        {
            platform.InjectControllerKey(key, true);
            platform.InjectPhysicalKey(key, true);
            platform.InjectControllerKey(key, false);
            platform.InjectPhysicalKey(key, false);
        }
        else
        {
            platform.InjectPhysicalKey(key, true);
            platform.InjectControllerKey(key, true);
            platform.InjectPhysicalKey(key, false);
            platform.InjectControllerKey(key, false);
        }
        Assert.Equal(new[] { "key-down:" + (int)GlKeys.W, "key-up:" + (int)GlKeys.W }, probe.Events);
    }

    [Fact]
    public void SdlKeyAndLayoutTextReachTheGameThroughSeparateEvents()
    {
        var platform = new VulkanClientPlatform(null!);
        var probe = new Probe();
        platform.keyEventHandlers.Add(probe);
        platform.DispatchSdlKeyboard(new SdlInputEvent(SdlEventPump.KeyDown, 0, 1,
            Key: GlKeys.W, Modifiers: 0x0141)); // Shift, Ctrl, Alt
        platform.DispatchSdlKeyboard(new SdlInputEvent(SdlEventPump.TextInput, 0, 1, Text: "é"));
        platform.DispatchSdlKeyboard(new SdlInputEvent(SdlEventPump.KeyUp, 0, 1, Key: GlKeys.W));
        Assert.Equal(new[] { "key-down:" + (int)GlKeys.W, "text:é", "key-up:" + (int)GlKeys.W }, probe.Events);
        Assert.True(probe.LastKeyDown!.ShiftPressed);
        Assert.True(probe.LastKeyDown.CtrlPressed);
        Assert.True(probe.LastKeyDown.AltPressed);
    }

    [Fact]
    public void ControllerActionsSharingAKeyReleaseAfterTheLastAction()
    {
        var platform = new ClientPlatformWindows(null!);
        var probe = new Probe();
        platform.keyEventHandlers.Add(probe);
        var key = new KeyEvent { KeyCode = (int)GlKeys.W };
        platform.InjectControllerKey(key, true);
        platform.InjectControllerKey(key, true);
        platform.InjectControllerKey(key, false);
        Assert.Single(probe.Events);
        platform.InjectControllerKey(key, false);
        Assert.Equal(new[] { "key-down:" + (int)GlKeys.W, "key-up:" + (int)GlKeys.W }, probe.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReleasingEitherMouseSourceKeepsTheOtherHeld(bool controllerFirst)
    {
        var platform = new ClientPlatformWindows(null!);
        var probe = new Probe();
        platform.mouseEventHandlers.Add(probe);

        if (controllerFirst)
        {
            platform.InjectControllerMouseButton(EnumMouseButton.Left, true);
            PhysicalMouse(platform, true);
            platform.InjectControllerMouseButton(EnumMouseButton.Left, false);
            PhysicalMouse(platform, false);
        }
        else
        {
            PhysicalMouse(platform, true);
            platform.InjectControllerMouseButton(EnumMouseButton.Left, true);
            PhysicalMouse(platform, false);
            platform.InjectControllerMouseButton(EnumMouseButton.Left, false);
        }

        Assert.Equal(new[] { "mouse-down:Left", "mouse-up:Left" }, probe.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SdlPhysicalMouseAndControllerShareTheSameHoldArbitration(bool controllerFirst)
    {
        var platform = new ClientPlatformWindows(null!);
        var probe = new Probe();
        platform.mouseEventHandlers.Add(probe);
        if (controllerFirst)
        {
            platform.InjectControllerMouseButton(EnumMouseButton.Left, true);
            platform.InjectPhysicalMouseButton(EnumMouseButton.Left, true, 18, 25);
            platform.InjectControllerMouseButton(EnumMouseButton.Left, false);
            platform.InjectPhysicalMouseButton(EnumMouseButton.Left, false, 18, 25);
        }
        else
        {
            platform.InjectPhysicalMouseButton(EnumMouseButton.Left, true, 18, 25);
            platform.InjectControllerMouseButton(EnumMouseButton.Left, true);
            platform.InjectPhysicalMouseButton(EnumMouseButton.Left, false, 18, 25);
            platform.InjectControllerMouseButton(EnumMouseButton.Left, false);
        }
        Assert.Equal(new[] { "mouse-down:Left", "mouse-up:Left" }, probe.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TouchDragDoesNotReleasePhysicalMouseOrControllerHold(bool touchFirst)
    {
        var platform = new ClientPlatformWindows(null!);
        var probe = new Probe();
        platform.mouseEventHandlers.Add(probe);
        if (touchFirst) platform.InjectTouchMouseButton(EnumMouseButton.Left, true, 18, 25);
        platform.InjectPhysicalMouseButton(EnumMouseButton.Left, true, 18, 25);
        platform.InjectControllerMouseButton(EnumMouseButton.Left, true);
        if (!touchFirst) platform.InjectTouchMouseButton(EnumMouseButton.Left, true, 18, 25);
        platform.InjectTouchMouseButton(EnumMouseButton.Left, false, 18, 25);
        platform.InjectPhysicalMouseButton(EnumMouseButton.Left, false, 18, 25);
        Assert.Equal(new[] { "mouse-down:Left" }, probe.Events);
        platform.InjectControllerMouseButton(EnumMouseButton.Left, false);
        Assert.Equal(new[] { "mouse-down:Left", "mouse-up:Left" }, probe.Events);
    }

    [Fact]
    public void PhysicalMouseMotionSwitchesPromptsButControllerCursorWarpDoesNot()
    {
        var platform = new ClientPlatformWindows(null!);
        OptimumControllerHints.Publish(new Dictionary<string, string> { ["jump"] = "A" });
        try
        {
            OptimumControllerHints.SetControllerActive(true);
            platform.InjectControllerMouseMotion(20, 20, 2, 2);
            Assert.Equal("A", OptimumControllerHints.GlyphFor("jump"));
            platform.InjectPhysicalMouseMotion(22, 22, 2, 2);
            Assert.Null(OptimumControllerHints.GlyphFor("jump"));
        }
        finally { OptimumControllerHints.Publish(null); }
    }

    [Fact]
    public void SdlMouseAndFocusEventsReachOnlyTheirWindow()
    {
        var platform = new VulkanClientPlatform(null!) { SdlWindowId = 42 };
        var probe = new Probe();
        platform.keyEventHandlers.Add(probe);
        platform.mouseEventHandlers.Add(probe);
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.MouseButtonDown, 0, 99,
            X: 15, Y: 20, Button: 1));
        Assert.Empty(probe.Events);
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.MouseButtonDown, 0, 42,
            X: 15, Y: 20, Button: 1, MouseId: SdlEventPump.TouchMouseId));
        Assert.Empty(probe.Events);
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.MouseMotion, 0, 42,
            X: 15, Y: 20, DeltaX: 3, DeltaY: -2));
        Assert.Equal((15, 20, 3, -2),
            (probe.LastMouseMove!.X, probe.LastMouseMove.Y, probe.LastMouseMove.DeltaX, probe.LastMouseMove.DeltaY));
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.MouseButtonDown, 0, 42,
            X: 15, Y: 20, Button: 1));
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.KeyDown, 0, 42, Key: GlKeys.W));
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.MouseWheel, 0, 42,
            X: 15, Y: 20, DeltaY: 2, Data1: 1));
        Assert.Equal(-2 * ClientSettings.MouseWheelSensivity, probe.LastWheel!.deltaPrecise);
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.WindowFocusLost, 0, 42));
        Assert.Equal(new[] { "mouse-down:Left", "key-down:" + (int)GlKeys.W,
            "key-up:" + (int)GlKeys.W, "mouse-up:Left" }, probe.Events);
    }

    [SkippableFact]
    public void FingerTapAndSdlSyntheticMouseProduceOnlyOneClick()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SDL touch routing probe runs on Windows.");
        using var host = SdlVulkanWindowHost.Create("SDL touch routing probe", 128, 96, hidden: true);
        var platform = new VulkanClientPlatform(null!) { SdlWindowId = host.WindowId };
        typeof(VulkanClientPlatform).GetField("sdlWindowHost", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(platform, host);
        var probe = new Probe();
        platform.mouseEventHandlers.Add(probe);
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.FingerDown, 1, host.WindowId,
            X: 0.5f, Y: 0.5f, TouchId: 1, FingerId: 2));
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.MouseButtonDown, 2, host.WindowId,
            X: 64, Y: 48, Button: 1, MouseId: SdlEventPump.TouchMouseId));
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.FingerUp, 3, host.WindowId,
            X: 0.5f, Y: 0.5f, TouchId: 1, FingerId: 2));
        platform.DispatchSdlInput(new SdlInputEvent(SdlEventPump.MouseButtonUp, 4, host.WindowId,
            X: 64, Y: 48, Button: 1, MouseId: SdlEventPump.TouchMouseId));
        Assert.Equal(new[] { "mouse-down:Left", "mouse-up:Left" }, probe.Events);
    }

    [Theory]
    [InlineData(1, EnumMouseButton.Left)]
    [InlineData(2, EnumMouseButton.Middle)]
    [InlineData(3, EnumMouseButton.Right)]
    [InlineData(4, EnumMouseButton.Button4)]
    [InlineData(8, EnumMouseButton.Button8)]
    [InlineData(9, EnumMouseButton.None)]
    public void SdlButtonsFollowSdlIndexOrder(byte button, EnumMouseButton expected) =>
        Assert.Equal(expected, VulkanClientPlatform.MouseButton(button));

    private static void PhysicalKey(ClientPlatformWindows platform, bool down)
    {
        string name = down ? "game_KeyDown" : "game_KeyUp";
        var args = new KeyboardKeyEventArgs(Keys.W, 0, (GlfwKeyModifiers)0, false);
        typeof(ClientPlatformWindows).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(platform, new object[] { args });
    }

    private static void PhysicalMouse(ClientPlatformWindows platform, bool down)
    {
        string name = down ? "Mouse_ButtonDown" : "Mouse_ButtonUp";
        var args = new MouseButtonEventArgs(MouseButton.Button1, down ? (InputAction)1 : (InputAction)0, (GlfwKeyModifiers)0);
        typeof(ClientPlatformWindows).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(platform, new object[] { args });
    }
}
