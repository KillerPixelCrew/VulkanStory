using System;

namespace VulkanStory.Game.Input;

/// <summary>One-shot remapping capture that waits for all held buttons to release before accepting a new press.</summary>
internal sealed class ControllerButtonCapture
{
    /// <summary>Whether both the fixed Back/View and Start/Menu button bits are held.</summary>
    public static bool CancelChordPressed(uint buttons) =>
        (buttons & ((1u << 4) | (1u << 6))) == ((1u << 4) | (1u << 6));

    /// <summary>Action being remapped, or null when capture is idle.</summary>
    public string? Action { get; private set; }
    /// <summary>Whether capture is still ignoring the initial held buttons.</summary>
    public bool WaitingForRelease { get; private set; }
    private uint previousButtons;

    /// <summary>Begins a new action capture, requiring release before the next press can bind.</summary>
    public void Begin(string action, uint pressedButtons)
    {
        Action = action;
        WaitingForRelease = true;
        previousButtons = pressedButtons;
    }

    /// <summary>Clears the pending action and button-edge state.</summary>
    public void Cancel()
    {
        Action = null;
        WaitingForRelease = false;
        previousButtons = 0;
    }

    /// <summary>Consumes button edges and completes capture on the lowest newly pressed bit.</summary>
    /// <param name="pressedButtons">Current held state for up to 32 buttons.</param>
    /// <returns>The completed action/button pair, or null while idle or waiting; completion clears capture.</returns>
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
