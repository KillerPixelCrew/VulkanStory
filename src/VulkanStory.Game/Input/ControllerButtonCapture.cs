using System;

namespace VulkanStory.Game.Input;

internal sealed class ControllerButtonCapture
{
    public static bool CancelChordPressed(uint buttons) =>
        (buttons & ((1u << 4) | (1u << 6))) == ((1u << 4) | (1u << 6));

    public string? Action { get; private set; }
    public bool WaitingForRelease { get; private set; }
    private uint previousButtons;

    public void Begin(string action, uint pressedButtons)
    {
        Action = action;
        WaitingForRelease = true;
        previousButtons = pressedButtons;
    }

    public void Cancel()
    {
        Action = null;
        WaitingForRelease = false;
        previousButtons = 0;
    }

    public (string Action, int Button)? Update(uint pressedButtons)
    {
        if (Action == null) return null;
        if (WaitingForRelease)
        {
            previousButtons = pressedButtons;
            if (pressedButtons == 0) WaitingForRelease = false;
            return null;
        }

        uint newlyPressed = pressedButtons & ~previousButtons;
        previousButtons = pressedButtons;
        if (newlyPressed == 0) return null;
        int button = System.Numerics.BitOperations.TrailingZeroCount(newlyPressed);
        string action = Action;
        Cancel();
        return (action, button);
    }
}
