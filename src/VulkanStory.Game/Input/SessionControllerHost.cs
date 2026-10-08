using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Game.Input;

/// <summary>Session movement sample and analog negotiation identity, reset across world/connection changes.</summary>
internal sealed class ControllerMovementState
{
    internal Vector2 Axes;
    internal float Factor = 1f;
    private object? acknowledgedWorld;
    private long acknowledgedPlayer;
    private object? probeWorld;
    private long probePlayer;
    /// <summary>Whether this world object owns the accepted negotiation response.</summary>
    internal bool IsAcknowledged(object? world) => world != null && ReferenceEquals(world, acknowledgedWorld);
    /// <summary>Whether both the world object and player entity match the accepted negotiation response.</summary>
    internal bool IsAcknowledged(object world, long player) => IsAcknowledged(world) && acknowledgedPlayer == player;
    /// <summary>Records the world/player identity for the next acceptable protocol acknowledgement.</summary>
    internal void BeginProbe(object world, long player) { probeWorld = world; probePlayer = player; }
    /// <summary>Accepts acknowledgement only for the most recently probed world/player pair.</summary>
    internal void Acknowledge(object world, long player)
    {
        if (!ReferenceEquals(world, probeWorld) || player != probePlayer) return;
        acknowledgedWorld = world; acknowledgedPlayer = player;
    }
    /// <summary>Clears negotiation and restores idle movement with full digital speed.</summary>
    internal void Reset()
    { acknowledgedWorld = probeWorld = null; acknowledgedPlayer = probePlayer = 0; Axes = Vector2.Zero; Factor = 1f; }
}

/// <summary>Adapts one render session's SDL window, text target, and movement state for controller processing.</summary>
/// <param name="session">Render session owning input dispatch and stopping state.</param>
/// <param name="platform">Original platform receiving game-compatible input.</param>
/// <param name="window">SDL window used for focus, capture, and drawable coordinates.</param>
/// <param name="text">Text-input bridge used to locate focused editable targets.</param>
/// <param name="movement">Session-owned analog input and negotiation state.</param>
internal sealed class SessionControllerHost(GameRenderSession session, ClientPlatformWindows platform,
    SdlWindowHost window, SdlGuiTextInput text, ControllerMovementState movement) : IControllerPlatformHost
{
    private readonly GameGuiBindings gui = new();
    /// <inheritdoc />
    public ClientPlatformWindows Original => platform;
    /// <inheritdoc />
    public bool HasControllerWindow => !session.Stopping;
    /// <inheritdoc />
    public bool IsFocused => window.IsFocused;
    /// <inheritdoc />
    public bool MouseGrabbed => window.RelativeMouseMode;
    /// <inheritdoc />
    public (int Width, int Height) ControllerWindowSize => window.PixelSize;
    /// <inheritdoc />
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
    /// <inheritdoc />
    public Vector2 ControllerMoveAxes { get => movement.Axes; set => movement.Axes = value; }
    /// <inheritdoc />
    public float ControllerMoveFactor { get => movement.Factor; set => movement.Factor = value; }
    /// <inheritdoc />
    public bool AnalogServerReady => gui.CurrentScreen is GuiScreenRunningGame running &&
        gui.RunningGame(running) is { EntityPlayer: { } player } client && movement.IsAcknowledged(client, player.EntityId);
    /// <inheritdoc />
    public GuiScreen? ControllerCurrentScreen() => gui.CurrentScreen;
    /// <inheritdoc />
    public GuiElementEditableTextBase? ControllerFocusedEditableText() => text.FocusedEditableText();
    /// <inheritdoc />
    public void InjectControllerKey(KeyEvent key, bool down) => session.Input.Input.InjectControllerKey(key, down);
    /// <inheritdoc />
    public void InjectControllerMouseButton(EnumMouseButton button, bool down) => session.Input.Input.InjectControllerMouseButton(button, down);
    /// <inheritdoc />
    public void InjectControllerMouseWheel(int direction) => session.Input.Input.InjectControllerMouseWheel(direction);
}
