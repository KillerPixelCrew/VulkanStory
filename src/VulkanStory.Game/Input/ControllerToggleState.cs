namespace VulkanStory.Game.Input;

internal sealed class ControllerToggleState
{
    private bool wasPressed;
    private bool latched;

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

    public void Reset(bool pressed = false)
    {
        latched = false;
        wasPressed = pressed;
    }
}
