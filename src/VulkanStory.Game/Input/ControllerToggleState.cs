namespace VulkanStory.Game.Input;

/// <summary>Edge-triggered latch for controller hold/toggle actions with context deactivation reset.</summary>
internal sealed class ControllerToggleState
{
    private bool wasPressed;
    private bool latched;

    /// <summary>Samples a button and returns the effective hold or toggle state.</summary>
    /// <param name="pressed">Current physical button state.</param>
    /// <param name="toggleEnabled">Whether a new press flips a latch instead of following the held state.</param>
    /// <param name="active">Whether this action's context is active; false clears the latch.</param>
    public bool Update(bool pressed, bool toggleEnabled, bool active)
    {
        if (!active)
        {
            latched = false;
            wasPressed = pressed;
            return false;
        }
        if (!toggleEnabled)
        {
            latched = false;
            wasPressed = pressed;
            return pressed;
        }
        if (pressed && !wasPressed) latched = !latched;
        wasPressed = pressed;
        return latched;
    }

    /// <summary>Clears the latch and seeds the previous button state to avoid a false press edge.</summary>
    public void Reset(bool pressed = false)
    {
        latched = false;
        wasPressed = pressed;
    }
}
