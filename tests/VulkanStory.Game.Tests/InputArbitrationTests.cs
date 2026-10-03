using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;
using Xunit;

namespace VulkanStory.Game.Tests;

// Retained cases from ControllerInputArbitrationTests at migration baseline
// 386e0d05386d0b228b439d09aeca851428f7bbf3, exercised through the new adapter.
public sealed class InputArbitrationTests
{
    private sealed class Probe : KeyEventHandler, MouseEventHandler
    {
        internal readonly List<string> Events = new();
        internal KeyEvent? LastKey;
        internal MouseWheelEventArgs? Wheel;
        public void OnKeyDown(KeyEvent e) { LastKey = e; Events.Add("key-down:" + e.KeyCode); }
        public void OnKeyUp(KeyEvent e) => Events.Add("key-up:" + e.KeyCode);
        public void OnKeyPress(KeyEvent e) => Events.Add("text:" + e.KeyChar);
        public void OnMouseDown(MouseEvent e) => Events.Add("mouse-down:" + e.Button);
        public void OnMouseUp(MouseEvent e) => Events.Add("mouse-up:" + e.Button);
        public void OnMouseMove(MouseEvent e) { }
        public void OnMouseWheel(MouseWheelEventArgs e) => Wheel = e;
    }

    private sealed class Fixture
    {
        internal readonly ClientPlatformWindows Platform;
        internal readonly GamePlatformBindings Bindings;
        internal readonly GameInputBridge Input;
        internal readonly Probe Probe = new();
        internal int PhysicalActivity;
        internal float WheelSensitivity = 1.25f;
        internal readonly List<bool> Focus = new();

        internal Fixture()
        {
            // Bypass the platform constructor's native screen discovery. Only
            // input state is initialized; frame/close delegates are never called.
            Platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
            Platform.keyEventHandlers = [Probe];
            Platform.mouseEventHandlers = [Probe];
            Set("uptimeStopWatch", new Stopwatch());
            Set("focusChangedDelegates", new List<ClientPlatformAbstract.OnFocusChanged> { Focus.Add });
            Bindings = new GamePlatformBindings(Platform);
            Input = new GameInputBridge(Platform, Bindings, () => PhysicalActivity++, () => (18, 25),
                () => WheelSensitivity);
        }

        private void Set(string name, object value) => typeof(ClientPlatformWindows)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Platform, value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherKeySourceCanReleaseWithoutReleasingTheOther(bool controllerFirst)
    {
        var f = new Fixture();
        var key = new KeyEvent { KeyCode = (int)GlKeys.W };
        if (controllerFirst)
        {
            f.Input.InjectControllerKey(key, true);
            f.Input.InjectPhysicalKey(key, true);
            f.Input.InjectControllerKey(key, false);
            f.Input.InjectPhysicalKey(key, false);
        }
        else
        {
            f.Input.InjectPhysicalKey(key, true);
            f.Input.InjectControllerKey(key, true);
            f.Input.InjectPhysicalKey(key, false);
            f.Input.InjectControllerKey(key, false);
        }
        Assert.Equal(new[] { "key-down:" + (int)GlKeys.W, "key-up:" + (int)GlKeys.W }, f.Probe.Events);
    }

    [Fact]
    public void ControllerActionsSharingAKeyReleaseAfterTheLastAction()
    {
        var f = new Fixture();
        var key = new KeyEvent { KeyCode = (int)GlKeys.W };
        f.Input.InjectControllerKey(key, true);
        f.Input.InjectControllerKey(key, true);
        f.Input.InjectControllerKey(key, false);
        Assert.Single(f.Probe.Events);
        f.Input.InjectControllerKey(key, false);
        Assert.Equal(2, f.Probe.Events.Count);
        Assert.Equal("key-up:" + (int)GlKeys.W, f.Probe.Events[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TouchReleasePreservesPhysicalAndControllerMouseHolds(bool touchFirst)
    {
        var f = new Fixture();
        if (touchFirst) f.Input.InjectTouchMouseButton(EnumMouseButton.Left, true, 18, 25);
        f.Input.InjectPhysicalMouseButton(EnumMouseButton.Left, true, 18, 25);
        f.Input.InjectControllerMouseButton(EnumMouseButton.Left, true);
        if (!touchFirst) f.Input.InjectTouchMouseButton(EnumMouseButton.Left, true, 18, 25);
        f.Input.InjectTouchMouseButton(EnumMouseButton.Left, false, 18, 25);
        f.Input.InjectPhysicalMouseButton(EnumMouseButton.Left, false, 18, 25);
        Assert.Equal(new[] { "mouse-down:Left" }, f.Probe.Events);
        f.Input.InjectControllerMouseButton(EnumMouseButton.Left, false);
        Assert.Equal(new[] { "mouse-down:Left", "mouse-up:Left" }, f.Probe.Events);
        Assert.Equal(18, f.Bindings.MouseX);
        Assert.Equal(25, f.Bindings.MouseY);
    }

    [Fact]
    public void FocusLossAndControllerCleanupDeliverOneFinalRelease()
    {
        var f = new Fixture();
        var key = new KeyEvent { KeyCode = (int)GlKeys.W };
        f.Input.InjectPhysicalKey(key, true);
        f.Input.InjectControllerKey(key, true);
        f.Input.InjectControllerKey(key, true);
        f.Input.InjectPhysicalFocusChanged(false);
        Assert.Single(f.Probe.Events);
        Assert.False(f.Input.PhysicalMovementHeld((int)GlKeys.W, 0, 0, 0));
        f.Input.ReleaseControllers();
        f.Input.ReleaseControllers();
        Assert.Equal(new[] { "key-down:" + (int)GlKeys.W, "key-up:" + (int)GlKeys.W }, f.Probe.Events);
        Assert.Equal(new[] { false }, f.Focus);
    }

    [Fact]
    public void PhysicalActivityAndTextRemainIndependentOfControllerCursorMotion()
    {
        var f = new Fixture();
        f.Input.InjectControllerMouseMotion(20, 20, 2, 2);
        Assert.Equal(0, f.PhysicalActivity);
        f.Input.InjectPhysicalMouseMotion(22, 22, 2, 2);
        Assert.Equal(1, f.PhysicalActivity);
        f.Input.InjectPhysicalText("é");
        Assert.Equal(new[] { "text:é" }, f.Probe.Events);
        Assert.Equal(22, f.Bindings.MouseX);
    }

    [Fact]
    public void WheelAccumulatesOnceForMultipleHandlers()
    {
        var f = new Fixture();
        var second = new Probe();
        f.Platform.mouseEventHandlers.Add(second);
        f.Input.InjectPhysicalMouseWheel(2, 18, 25);
        float expected = 2 * f.WheelSensitivity;
        Assert.Equal(expected, f.Bindings.Wheel);
        Assert.Equal(expected, f.Probe.Wheel!.valuePrecise);
        Assert.Equal(expected, second.Wheel!.valuePrecise);
        f.Input.InjectPhysicalMouseWheel(-1, 18, 25);
        Assert.Equal(f.WheelSensitivity, f.Bindings.Wheel);
        // A setting change is observed by the next event without recreating input.
        f.WheelSensitivity = 0.5f;
        f.Input.InjectPhysicalMouseWheel(2, 18, 25);
        Assert.Equal(2.25f, f.Bindings.Wheel);
        Assert.Equal(1f, second.Wheel!.deltaPrecise);
    }

    [Fact]
    public void SecondaryKeyHistoryUsesOriginalFieldsAndEachHandlerGetsItsOwnEvent()
    {
        var f = new Fixture();
        var second = new Probe();
        f.Platform.keyEventHandlers.Add(second);
        f.Bindings.LastKeyUpMs = 0;
        f.Bindings.LastKeyUpKey = (int)GlKeys.A;
        f.Input.InjectPhysicalKey(new KeyEvent { KeyCode = (int)GlKeys.W, ShiftPressed = true }, true);
        Assert.Equal((int)GlKeys.A, f.Probe.LastKey!.KeyCode2);
        Assert.True(f.Probe.LastKey.ShiftPressed);
        Assert.NotSame(f.Probe.LastKey, second.LastKey);
    }
}
