using System.Text.Json.Serialization;

namespace VulkanStory.Game.Input;

/// <summary>Serialized gesture modes supported by controller action bindings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ControllerGestureMode { Hold, Press, Release, Tap, LongPress, Toggle, DoublePress, DoubleTap, DoubleHold, DoubleToggle }

/// <summary>Per-action press/release timing and latch state for one controller gesture binding.</summary>
internal sealed class ControllerGesture
{
    private bool wasDown, longFired, secondBeat, latched;
    private long started, lastTap = long.MinValue;
    /// <summary>Effective output from the most recent sample.</summary>
    internal bool Output { get; private set; }

    /// <summary>Advances gesture timing and computes this sample's pulse, hold, or latched output.</summary>
    /// <param name="down">Current button state.</param>
    /// <param name="mode">Gesture interpretation selected by the profile.</param>
    /// <param name="nowMs">Monotonic sample time in milliseconds.</param>
    /// <param name="tapMs">Tap duration and second-press window in milliseconds.</param>
    /// <param name="longMs">Held duration required for the one-shot long-press event.</param>
    internal bool Update(bool down, ControllerGestureMode mode, long nowMs, int tapMs, int longMs)
    {
        bool press = down && !wasDown, release = !down && wasDown;
        bool tap = release && !longFired && nowMs - started <= tapMs;
        if (press)
        {
            started = nowMs;
            longFired = false;
            secondBeat = lastTap != long.MinValue && nowMs - lastTap <= tapMs;
        }
        bool longPress = down && !longFired && nowMs - started >= longMs;
        if (longPress) longFired = true;
        bool doubleTap = tap && secondBeat;
        if (tap) lastTap = secondBeat ? long.MinValue : nowMs;
        if ((mode == ControllerGestureMode.Toggle && press) ||
            (mode == ControllerGestureMode.DoubleToggle && press && secondBeat)) latched = !latched;
        wasDown = down;
        return Output = mode switch
        {
            ControllerGestureMode.Hold => down,
            ControllerGestureMode.Press => press,
            ControllerGestureMode.Release => release,
            ControllerGestureMode.Tap => tap,
            ControllerGestureMode.LongPress => longPress,
            ControllerGestureMode.Toggle or ControllerGestureMode.DoubleToggle => latched,
            ControllerGestureMode.DoublePress => press && secondBeat,
            ControllerGestureMode.DoubleTap => doubleTap,
            ControllerGestureMode.DoubleHold => down && secondBeat,
            _ => false
        };
    }
}
