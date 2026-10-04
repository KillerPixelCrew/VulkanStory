using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

// All added movement/window state belongs to the mod owner, never injected fields.
internal interface IControllerPlatformHost
{
    ClientPlatformWindows Original { get; }
    bool HasControllerWindow { get; }
    bool IsFocused { get; }
    bool MouseGrabbed { get; }
    (int Width, int Height) ControllerWindowSize { get; }
    Vector2 ControllerCursorPosition { get; set; }
    Vector2 ControllerMoveAxes { get; set; }
    float ControllerMoveFactor { get; set; }
    // True only after the optional server companion's negotiation succeeds.
    bool AnalogServerReady { get; }
    GuiScreen? ControllerCurrentScreen();
    GuiElementEditableTextBase? ControllerFocusedEditableText();
    void InjectControllerKey(KeyEvent key, bool down);
    void InjectControllerMouseButton(EnumMouseButton button, bool down);
    void InjectControllerMouseWheel(int direction);
}
