using System.Text.Json.Serialization;

namespace VulkanStory.Game.Input;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ControllerGestureMode { Hold, Press, Release, Tap, LongPress, Toggle, DoublePress, DoubleTap, DoubleHold, DoubleToggle }

internal sealed class ControllerGesture
{
    private bool wasDown, longFired, secondBeat, latched;
    private long started, lastTap = long.MinValue;
    internal bool Output { get; private set; }

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
