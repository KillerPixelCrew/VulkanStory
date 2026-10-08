using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

// All added movement/window state belongs to the mod owner, never injected fields.
/// <summary>Session-owned bridge from controller processing to the original game's input and GUI handlers.</summary>
internal interface IControllerPlatformHost
{
    /// <summary>Original game platform whose handlers receive synthetic controller events.</summary>
    ClientPlatformWindows Original { get; }
    /// <summary>Whether the session can still accept controller window operations.</summary>
    bool HasControllerWindow { get; }
    /// <summary>Whether the SDL window currently owns input focus.</summary>
    bool IsFocused { get; }
    /// <summary>Whether the game uses relative mouse capture rather than GUI cursor movement.</summary>
    bool MouseGrabbed { get; }
    /// <summary>Controller navigation bounds in drawable pixels.</summary>
    (int Width, int Height) ControllerWindowSize { get; }
    /// <summary>GUI cursor position in drawable pixels; setting it routes through the session input bridge.</summary>
    Vector2 ControllerCursorPosition { get; set; }
    /// <summary>Current normalized controller movement direction retained by the session.</summary>
    Vector2 ControllerMoveAxes { get; set; }
    /// <summary>Analog movement-speed factor retained by the session for protocol transmission.</summary>
    float ControllerMoveFactor { get; set; }
    // True only after the optional server companion's negotiation succeeds.
    /// <summary>Whether the current world/player pair acknowledged the optional analog companion.</summary>
    bool AnalogServerReady { get; }
    /// <summary>Returns the current game screen, when available.</summary>
    GuiScreen? ControllerCurrentScreen();
    /// <summary>Returns the active GUI text target for controller keyboard entry, when one is focused.</summary>
    GuiElementEditableTextBase? ControllerFocusedEditableText();
    /// <summary>Routes a synthetic key transition while preserving physical-key ownership.</summary>
    void InjectControllerKey(KeyEvent key, bool down);
    /// <summary>Routes a synthetic mouse transition while preserving physical-button ownership.</summary>
    void InjectControllerMouseButton(EnumMouseButton button, bool down);
    /// <summary>Routes one controller-generated scroll step to the game.</summary>
    void InjectControllerMouseWheel(int direction);
}
