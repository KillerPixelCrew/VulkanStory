using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Game.Input;

internal sealed class ControllerMovementState
{
    internal Vector2 Axes;
    internal float Factor = 1f;
    private object? acknowledgedWorld;
    private long acknowledgedPlayer;
    private object? probeWorld;
    private long probePlayer;
    internal bool IsAcknowledged(object? world) => world != null && ReferenceEquals(world, acknowledgedWorld);
    internal bool IsAcknowledged(object world, long player) => IsAcknowledged(world) && acknowledgedPlayer == player;
    internal void BeginProbe(object world, long player) { probeWorld = world; probePlayer = player; }
    internal void Acknowledge(object world, long player)
    {
        if (!ReferenceEquals(world, probeWorld) || player != probePlayer) return;
        acknowledgedWorld = world; acknowledgedPlayer = player;
    }
    internal void Reset()
    { acknowledgedWorld = probeWorld = null; acknowledgedPlayer = probePlayer = 0; Axes = Vector2.Zero; Factor = 1f; }
}

internal sealed class SessionControllerHost(GameRenderSession session, ClientPlatformWindows platform,
    SdlWindowHost window, SdlGuiTextInput text, ControllerMovementState movement) : IControllerPlatformHost
{
    private readonly GameGuiBindings gui = new();
    public ClientPlatformWindows Original => platform;
    public bool HasControllerWindow => !session.Stopping;
    public bool IsFocused => window.IsFocused;
    public bool MouseGrabbed => window.RelativeMouseMode;
    public (int Width, int Height) ControllerWindowSize => window.PixelSize;
    public Vector2 ControllerCursorPosition
    {
        get
        {
            var logical = window.MousePosition;
            var pixels = SdlWindowCoordinates.ToPixels(new System.Numerics.Vector2(logical.X, logical.Y), window.WindowSize, window.PixelSize);
            return new Vector2(pixels.X, pixels.Y);
        }
        set => session.Input.WarpControllerCursor(value.X, value.Y);
    }
    public Vector2 ControllerMoveAxes { get => movement.Axes; set => movement.Axes = value; }
    public float ControllerMoveFactor { get => movement.Factor; set => movement.Factor = value; }
    public bool AnalogServerReady => gui.CurrentScreen is GuiScreenRunningGame running &&
        gui.RunningGame(running) is { EntityPlayer: { } player } client && movement.IsAcknowledged(client, player.EntityId);
    public GuiScreen? ControllerCurrentScreen() => gui.CurrentScreen;
    public GuiElementEditableTextBase? ControllerFocusedEditableText() => text.FocusedEditableText();
    public void InjectControllerKey(KeyEvent key, bool down) => session.Input.Input.InjectControllerKey(key, down);
    public void InjectControllerMouseButton(EnumMouseButton button, bool down) => session.Input.Input.InjectControllerMouseButton(button, down);
}
