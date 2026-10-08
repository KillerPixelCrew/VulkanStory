using System.Numerics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Game;

// The session supplies pacing/markers, renderer resizing/teardown, GUI text
// targeting, and controller mapping. No renderer or game singleton lives in SDL.
/// <summary>Session-supplied boundaries for SDL input, latency, controls and frame dispatch; callbacks execute from the owned event loop.</summary>
internal sealed record GamePlatformCallbacks(
    Action BeforeInput, Action InputPumped, Action UpdateControllers, Action ControllersPumped,
    Action<int> ControllerActivity, Action RefreshControllers, Action ControllerFocusLost,
    Action PhysicalInput, System.Func<bool> TouchEnabled, System.Func<bool> HasTextTarget, Action SyncTextInput, System.Func<long> TextTargetRevision,
    Action StopTextInput, Action<int, int> ResizeGraphics, Action RecomposeGui,
    Action<ulong, ulong> RecordInputAge, Action StopAndDrainGraphics)
{
    internal ControllerPerformanceDiagnostics? ControllerPerformance { get; init; }
    internal Func<bool>? ControllerDiagnosticsEnabled { get; init; }
    internal Func<bool>? ControllerInputActive { get; init; }
}

/// <summary>SDL state alongside the unchanged official platform object.</summary>
internal sealed class GamePlatformAdapter : IDisposable
{
    private static readonly ConditionalWeakTable<ClientPlatformWindows, GamePlatformAdapter> Adapters = new();
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private ClientPlatformWindows? platform;
    private SdlWindowHost? window;
    private GamePlatformBindings? bindings;
    private GameInputBridge? input;
    private SdlTouchMouse? touch;
    private GameCursorController? cursor;
    private SdlXPlatformInterface? xPlatform;
    private GamePlatformCallbacks? callbacks;
    private readonly StartupRoutingTransaction routing;
    private string composition = "";
    private long textTargetRevision;
    private Vector2? controllerWarp;
    private long controllerWarpUntil;
    private (int Width, int Height)? pendingPixels;
    private bool recompose, closeRequested, pumping, stopping;
    private int externalCloseRequested;
    /// <summary>Queues an exit request for the next owned SDL pump without destroying the window.</summary>
    /// <remarks>Uses an atomic request flag; ordinary game close cancellation still applies.</remarks>
    internal void RequestWindowExit() => Interlocked.Exchange(ref externalCloseRequested, 1);
    internal void RefreshWindowLayout() { RequireActive(); QueueLayout(); }
    private EnumWindowBorder windowBorder = EnumWindowBorder.Resizable;

    private GamePlatformAdapter(ClientPlatformWindows platform, SdlWindowHost window,
        StartupRoutingTransaction routing, GamePlatformCallbacks callbacks)
    {
        this.platform = platform;
        this.window = window;
        this.routing = routing;
        this.callbacks = callbacks;
        textTargetRevision = callbacks.TextTargetRevision();
        bindings = new GamePlatformBindings(platform);
        input = new GameInputBridge(platform, bindings, callbacks.PhysicalInput, CursorPixels,
            () => ClientSettings.MouseWheelSensivity);
        cursor = new GameCursorController(platform, bindings, window, () => ClientSettings.GUIScale);
        xPlatform = new SdlXPlatformInterface(platform.XPlatInterface ??
            throw new InvalidOperationException("Original OS services must exist before SDL attachment."),
            window, () => routing.RoutingEnabled, RequireActive, writer =>
            {
                if (!GameGraphicsAdapter.TryGet(platform, out var graphics) || graphics is null)
                    throw new InvalidOperationException("Active video capture has no graphics adapter.");
                graphics.AssociateVideoWriter(writer);
            });
        touch = new SdlTouchMouse(input.InjectPhysicalMouseMotion, (button, down, x, y) =>
        {
            input!.InjectTouchMouseButton(button, down, x, y);
            if (down) SyncText();
        });
        QueueLayout();
    }

    // Construction failure leaves native resources with the session factory.
    /// <summary>Attaches SDL input/window services to one unchanged original platform and replaces its XPlatInterface with a scoped adapter.</summary>
    /// <param name="platform">Original game platform identity.</param>
    /// <param name="window">Session window, retained through device drain.</param>
    /// <param name="routing">Complete startup transaction used to gate window operations.</param>
    /// <param name="callbacks">Session-owned input, resize, controller and teardown boundaries.</param>
    /// <returns>The new platform sidecar.</returns>
    /// <remarks>Construction failure leaves native resources with the session factory; replacing XPlatInterface failure removes the association.</remarks>
    internal static GamePlatformAdapter Attach(ClientPlatformWindows platform, SdlWindowHost window,
        StartupRoutingTransaction routing, GamePlatformCallbacks callbacks)
    {
        if (Adapters.TryGetValue(platform, out _))
            throw new InvalidOperationException("The original platform already has an SDL adapter.");
        var adapter = new GamePlatformAdapter(platform, window, routing, callbacks);
        Adapters.Add(platform, adapter);
        try { adapter.bindings!.SetXPlatform(adapter.xPlatform!); }
        catch { Adapters.Remove(platform); throw; }
        return adapter;
    }

    internal static bool TryGet(ClientPlatformWindows platform, out GamePlatformAdapter? adapter) =>
        Adapters.TryGetValue(platform, out adapter);

    internal SdlWindowHost Window => window ?? throw new ObjectDisposedException(nameof(GamePlatformAdapter));
    internal GameInputBridge Input => input ?? throw new ObjectDisposedException(nameof(GamePlatformAdapter));
    internal GameCursorController Cursor => cursor ?? throw new ObjectDisposedException(nameof(GamePlatformAdapter));
    internal void RequireWindowRouting() => RequireActive();
    internal EnumWindowBorder WindowBorder
    {
        get { RequireActive(); return windowBorder; }
        set
        {
            RequireActive();
            windowBorder = value;
            if (!Window.IsFullscreen) ApplyWindowBorder();
        }
    }

    private void ApplyWindowBorder()
    {
        Window.SetResizable(windowBorder != EnumWindowBorder.Fixed);
        Window.SetBordered(windowBorder != EnumWindowBorder.Hidden);
    }

    /// <summary>Maps the original window-state request to SDL fullscreen, restore, minimize or maximize and queues layout refresh.</summary>
    /// <param name="value">Original OpenTK state enum; unknown numeric states are rejected.</param>
    internal void SetWindowState(OpenTK.Windowing.Common.WindowState value)
    {
        RequireActive();
        if ((int)value == 3) { Window.SetFullscreen(true); QueueLayout(); return; }
        if (Window.IsFullscreen)
        {
            Window.SetFullscreen(false);
            Window.Sync();
            ApplyWindowBorder();
        }
        switch ((int)value)
        {
            case 0: Window.Restore(); break;
            case 1: Window.Minimize(); break;
            case 2: Window.Maximize(); break;
            default: throw new ArgumentOutOfRangeException(nameof(value));
        }
        QueueLayout();
    }

    /// <summary>Pumps input and original frame callbacks until close is accepted or cancellation is requested.</summary>
    /// <param name="cancellation">Optional loop cancellation token.</param>
    internal void Run(CancellationToken cancellation = default)
    {
        RequireActive();
        closeRequested = false;
        while (!closeRequested && !cancellation.IsCancellationRequested) PumpFrame();
    }

    /// <summary>Runs one pre-input pacing, SDL drain, controller, resize/text and original-frame dispatch cycle.</summary>
    /// <remarks>Owner thread only; recursive pumping is rejected. Rendering remains gated on complete startup routing.</remarks>
    internal void PumpFrame()
    {
        RequireActive();
        if (pumping) throw new InvalidOperationException("Recursive SDL frame dispatch.");
        pumping = true;
        try
        {
            if (Interlocked.Exchange(ref externalCloseRequested, 0) != 0) RequestClose();
            if (closeRequested) return;
            var performance = callbacks!.ControllerPerformance;
            performance?.BeginFrame(callbacks.ControllerDiagnosticsEnabled?.Invoke() == true);
            callbacks!.BeforeInput();
            performance?.EndPacing();
            if (SdlEventPump.Drain(Dispatch, callbacks.InputPumped,
                    callbacks.ControllerActivity, callbacks.UpdateControllers))
                callbacks.RefreshControllers();
            performance?.EndEvents();
            callbacks.ControllersPumped();
            performance?.EndControllers(callbacks.ControllerInputActive?.Invoke() == true, Window.RelativeMouseMode);
            if (callbacks.TouchEnabled()) touch!.Tick(SdlEventPump.TicksNanoseconds());
            else touch!.Cancel();
            if (closeRequested) return;
            ApplyPendingResize();
            SyncText();
            // Active requires all startup, window and graphics groups installed.
            RequireActive();
            bindings!.RenderFrame();
            performance?.EndFrame();
        }
        finally { pumping = false; }
    }

    /// <summary>Converts controller framebuffer-pixel coordinates to SDL logical coordinates and records the short-lived synthetic warp.</summary>
    /// <param name="pixelX">Horizontal framebuffer-pixel position.</param>
    /// <param name="pixelY">Vertical framebuffer-pixel position.</param>
    internal void WarpControllerCursor(float pixelX, float pixelY)
    {
        RequireActive();
        Vector2 pixels = new(pixelX, pixelY);
        Vector2 logical = SdlWindowCoordinates.ToLogical(pixels, Window.WindowSize, Window.PixelSize);
        controllerWarp = pixels;
        controllerWarpUntil = Environment.TickCount64 + 100;
        Window.WarpMouse(logical.X, logical.Y);
    }

    private (float X, float Y) CursorPixels()
    {
        var cursor = Window.MousePosition;
        Vector2 pixels = Pixels(cursor.X, cursor.Y);
        return (pixels.X, pixels.Y);
    }

    private Vector2 Pixels(float x, float y) =>
        SdlWindowCoordinates.ToPixels(new(x, y), Window.WindowSize, Window.PixelSize);

    /// <summary>Dispatches one neutral SDL event to the matching original platform input/window handlers.</summary>
    /// <param name="e">Collected event; unrelated window events are filtered by identity.</param>
    /// <remarks>Runs on the owner thread inside the active event pump.</remarks>
    internal void Dispatch(SdlInputEvent e)
    {
        RequireActive();
        if (e.Type == SdlEventPump.Quit) { RequestClose(); return; }
        if (e.DisplayId != 0)
        {
            if (e.Type is SdlEventPump.DisplayAdded or SdlEventPump.DisplayRemoved ||
                e.DisplayId == Window.DisplayId) QueueLayout();
            return;
        }
        if (e.WindowId != Window.WindowId &&
            !(e.Type == SdlEventPump.DropFile && e.WindowId == 0 && Window.IsFocused)) return;
        if (e.MouseId == SdlEventPump.TouchMouseId && e.Type is
            SdlEventPump.MouseMotion or SdlEventPump.MouseButtonDown or
            SdlEventPump.MouseButtonUp or SdlEventPump.MouseWheel) return;

        if (e.TimestampNanoseconds != 0 && e.Type is
            SdlEventPump.KeyDown or SdlEventPump.KeyUp or SdlEventPump.TextEditing or
            SdlEventPump.TextInput or SdlEventPump.MouseMotion or SdlEventPump.MouseButtonDown or
            SdlEventPump.MouseButtonUp or SdlEventPump.MouseWheel or SdlEventPump.FingerDown or
            SdlEventPump.FingerMotion or SdlEventPump.FingerUp or SdlEventPump.FingerCanceled)
            callbacks!.RecordInputAge(e.TimestampNanoseconds, SdlEventPump.TicksNanoseconds());

        GlKeys key = SdlKeyMap.ToGlKey(e.Scancode);
        if (e.Type == SdlEventPump.TextEditing)
        {
            if (!callbacks!.HasTextTarget()) { SyncText(); return; }
            composition = e.Text ?? "";
            return;
        }
        if (e.Type == SdlEventPump.KeyDown && composition.Length != 0 && !callbacks!.HasTextTarget())
            SyncText();
        if (e.Type == SdlEventPump.KeyDown && key == GlKeys.Escape && composition.Length != 0)
        {
            Window.ClearComposition();
            composition = "";
            return;
        }
        if (e.Type == SdlEventPump.TextInput)
        {
            if (!callbacks!.HasTextTarget()) { SyncText(); return; }
            composition = "";
            Input.InjectPhysicalText(e.Text ?? "");
            return;
        }
        if (e.Type is SdlEventPump.KeyDown or SdlEventPump.KeyUp)
        {
            if (key != GlKeys.Unknown)
                Input.InjectPhysicalKey(new KeyEvent
                {
                    KeyCode = (int)key,
                    ShiftPressed = (e.Modifiers & 0x0003) != 0,
                    CtrlPressed = (e.Modifiers & 0x00C0) != 0,
                    AltPressed = (e.Modifiers & 0x0300) != 0,
                    CommandPressed = (e.Modifiers & 0x0C00) != 0,
                }, e.Type == SdlEventPump.KeyDown);
            if (e.Type == SdlEventPump.KeyDown) SyncText();
            return;
        }
        switch (e.Type)
        {
            case SdlEventPump.FingerDown:
            case SdlEventPump.FingerMotion:
            case SdlEventPump.FingerUp:
            case SdlEventPump.FingerCanceled:
                if (!callbacks!.TouchEnabled()) { touch!.Cancel(); break; }
                touch!.Handle(e, Window.WindowSize, Window.PixelSize, SdlEventPump.TicksNanoseconds());
                break;
            case SdlEventPump.DropFile:
                if (!string.IsNullOrEmpty(e.Text)) platform!.fileDropEventHandler?.Invoke(e.Text);
                break;
            case SdlEventPump.MouseMotion:
                Vector2 pos = Pixels(e.X, e.Y), delta = Pixels(e.DeltaX, e.DeltaY);
                bool warped = controllerWarp is { } expected &&
                    Environment.TickCount64 <= controllerWarpUntil && Vector2.DistanceSquared(pos, expected) < 4f;
                if (warped || Environment.TickCount64 > controllerWarpUntil) controllerWarp = null;
                if (warped) Input.InjectControllerMouseMotion(pos.X, pos.Y, delta.X, delta.Y);
                else Input.InjectPhysicalMouseMotion(pos.X, pos.Y, delta.X, delta.Y);
                break;
            case SdlEventPump.MouseButtonDown:
            case SdlEventPump.MouseButtonUp:
                Vector2 buttonPos = Pixels(e.X, e.Y);
                Input.InjectPhysicalMouseButton(MouseButton(e.Button), e.Type == SdlEventPump.MouseButtonDown,
                    buttonPos.X, buttonPos.Y);
                if (e.Type == SdlEventPump.MouseButtonDown) SyncText();
                break;
            case SdlEventPump.MouseWheel:
                Vector2 wheelPos = Pixels(e.X, e.Y);
                Input.InjectPhysicalMouseWheel(e.Data1 == 1 ? -e.DeltaY : e.DeltaY, wheelPos.X, wheelPos.Y);
                break;
            case SdlEventPump.WindowFocusLost:
                StopText();
                controllerWarp = null;
                touch!.Cancel();
                Input.InjectPhysicalFocusChanged(false);
                callbacks!.ControllerFocusLost();
                Input.ReleaseControllers();
                break;
            case SdlEventPump.WindowFocusGained:
                Input.InjectPhysicalFocusChanged(true);
                SyncText();
                break;
            case SdlEventPump.WindowCloseRequested: RequestClose(); break;
            case SdlEventPump.WindowResized:
                if (e.Data1 > 0 && e.Data2 > 0 && !Window.IsFullscreen)
                    (ClientSettings.ScreenWidth, ClientSettings.ScreenHeight) = (e.Data1, e.Data2);
                QueueLayout();
                break;
            case SdlEventPump.WindowPixelSizeChanged:
                if (e.Data1 > 0 && e.Data2 > 0) pendingPixels = (e.Data1, e.Data2);
                break;
            case SdlEventPump.WindowMaximized:
            case SdlEventPump.WindowRestored:
            case SdlEventPump.WindowDisplayChanged:
            case SdlEventPump.WindowDisplayScaleChanged:
            case SdlEventPump.WindowSafeAreaChanged:
            case SdlEventPump.WindowEnterFullscreen:
            case SdlEventPump.WindowLeaveFullscreen: QueueLayout(); break;
        }
    }

    private void RequestClose() { if (bindings!.RequestClose()) closeRequested = true; }
    private void SyncText()
    {
        callbacks!.SyncTextInput();
        long revision = callbacks.TextTargetRevision();
        if (revision != textTargetRevision)
        {
            composition = "";
            textTargetRevision = revision;
        }
    }
    private void StopText()
    {
        callbacks!.StopTextInput();
        Window.SetTextInputActive(false);
        composition = "";
        textTargetRevision = callbacks.TextTargetRevision();
    }

    private void QueueLayout()
    {
        recompose = true;
        var pixels = Window.PixelSize;
        if (pixels.Width > 0 && pixels.Height > 0 &&
            (pixels.Width != platform!.WindowSize.Width || pixels.Height != platform.WindowSize.Height))
            pendingPixels = pixels;
    }

    private void ApplyPendingResize()
    {
        bool changed = false, refresh = recompose;
        recompose = false;
        if (pendingPixels is { } size)
        {
            pendingPixels = null;
            if (platform!.WindowSize.Width != size.Width || platform.WindowSize.Height != size.Height)
            {
                callbacks!.ResizeGraphics(size.Width, size.Height);
                platform.WindowSize.Width = size.Width;
                platform.WindowSize.Height = size.Height;
                if (!Window.IsFullscreen)
                    (ClientSettings.ScreenWidth, ClientSettings.ScreenHeight) = Window.WindowSize;
                platform.TriggerWindowResized(size.Width, size.Height);
                changed = true;
            }
        }
        if (refresh) callbacks!.RecomposeGui();
        if (refresh && !changed && platform!.WindowSize.Width > 0 && platform.WindowSize.Height > 0)
            platform.TriggerWindowResized(platform.WindowSize.Width, platform.WindowSize.Height);
    }

    private static EnumMouseButton MouseButton(byte button) => button switch
    {
        1 => EnumMouseButton.Left, 2 => EnumMouseButton.Middle, 3 => EnumMouseButton.Right,
        >= 4 and <= 8 => EnumMouseButton.Button4 + (button - 4), _ => EnumMouseButton.None,
    };

    private void RequireOwner()
    {
        if (ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("SDL platform mutation must stay on its owner thread.");
    }

    private void RequireActive()
    {
        RequireOwner();
        ObjectDisposedException.ThrowIf(platform is null, this);
        if (stopping) throw new InvalidOperationException("The SDL session is stopping.");
        if (!routing.RoutingEnabled) throw new InvalidOperationException("Complete startup routing is not active.");
    }

    /// <summary>Requests close, drains session graphics, then detaches original input/OS bindings and destroys the SDL window.</summary>
    /// <remarks>Rejected during pumping. Drain failure propagates before dependent window resources are released.</remarks>
    public void Dispose()
    {
        RequireOwner();
        if (platform is null) return;
        if (pumping) throw new InvalidOperationException("Stop the SDL frame before disposing its adapter.");
        closeRequested = true;
        stopping = true;
        // If draining fails, retain the window/resources so shutdown can report it.
        callbacks!.StopAndDrainGraphics();
        DetachAfterDrain();
    }

    // Only the session owner calls this after successful device disposal.
    // It is also needed when an earlier non-GPU cleanup action reported errors.
    /// <summary>Restores original OS services and releases SDL/input sidecars after the session confirms device disposal.</summary>
    /// <remarks>The caller must have completed graphics drain. Cleanup actions collect failures while clearing associations; pumping must already have stopped.</remarks>
    internal void DetachAfterDrain()
    {
        RequireOwner();
        if (platform is null) return;
        if (pumping) throw new InvalidOperationException("Stop the SDL frame before detaching its adapter.");
        closeRequested = stopping = true;
        var failures = new List<Exception>();
        void Release(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
        Release(StopText);
        Release(() => touch!.Cancel());
        Release(() => Input.InjectPhysicalFocusChanged(false));
        Release(callbacks!.ControllerFocusLost);
        Release(Input.ReleaseControllers);
        Release(() => { if (ReferenceEquals(platform.XPlatInterface, xPlatform)) bindings!.SetXPlatform(xPlatform!.Inner); });
        Release(() => bindings!.SetCurrentCursor(null));
        Release(() => Window.Dispose());
        Adapters.Remove(platform);
        platform = null;
        window = null;
        bindings = null;
        input = null;
        touch = null;
        cursor = null;
        xPlatform = null;
        callbacks = null;
        if (failures.Count != 0) throw new AggregateException("SDL adapter detachment failed.", failures);
    }
}
