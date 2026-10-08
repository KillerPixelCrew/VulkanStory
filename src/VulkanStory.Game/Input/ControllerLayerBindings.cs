using System.Collections.Generic;

namespace VulkanStory.Game.Input;

/// <summary>Modifier-layer overrides with exclusive physical-button ownership among explicit shifted actions.</summary>
internal static class ControllerLayerBindings
{
    /// <summary>Resolves an explicit shifted binding or inherits the main button unless another shifted action owns it.</summary>
    /// <returns>Effective physical button, or -1 when inheritance is suppressed.</returns>
    internal static int Resolve(IReadOnlyDictionary<string, int> shifted, string action, int mainButton)
    {
        if (shifted.TryGetValue(action, out int button)) return button;
        // An explicit shifted action owns its button; inherited main actions
        // must not also run when that physical button is pressed.
        foreach (var pair in shifted)
            if (pair.Value == mainButton) return -1;
        return mainButton;
    }

    /// <summary>Assigns a shifted button, removing the first conflict; a negative button removes the override.</summary>
    internal static void Assign(Dictionary<string, int> shifted, string action, int button)
    {
        string? collision = null;
        foreach (var pair in shifted)
            if (pair.Key != action && pair.Value == button) { collision = pair.Key; break; }
        if (collision != null) shifted.Remove(collision);
        if (button < 0) shifted.Remove(action);
        else shifted[action] = button;
    }
}
