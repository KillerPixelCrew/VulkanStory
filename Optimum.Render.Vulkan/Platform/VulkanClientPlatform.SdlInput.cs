using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

public partial class VulkanClientPlatform
{
    private SdlVulkanWindowHost? sdlWindowHost;
    private bool ownsSdlWindow;
    private EnumWindowBorder sdlWindowBorder = EnumWindowBorder.Resizable;
    internal uint SdlWindowId;
    private bool sdlCloseRequested;
    internal float ControllerMoveFactor { get; set; } = 1f;
    internal Vector2 ControllerMoveAxes { get; set; }
    public override float OptimumControllerMoveFactor() => ControllerMoveFactor;
    public override Vector2 OptimumControllerMoveAxes() => ControllerMoveAxes;
    private (int Width, int Height)? pendingSdlPixelSize;
    private bool pendingSdlGuiRecompose;
    internal bool SdlGuiRecomposePendingForTests => pendingSdlGuiRecompose;
    private Vector2? pendingSdlControllerWarpPixels;
    private long pendingSdlControllerWarpUntil;
    private bool sdlTextInputActive;
    private GuiElementEditableTextBase? sdlTextInputTarget;
    private string sdlCompositionText = "";
    internal string SdlCompositionTextForTests => sdlCompositionText;
    private SdlTouchMouse? sdlTouchMouse;
    private (int X, int Y, int Width, int Height, int Cursor)? sdlTextInputArea;
    private static readonly FieldInfo? CurrentScreenField = typeof(ScreenManager).GetField(
        "CurrentScreen", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? RunningGameField = typeof(GuiScreenRunningGame).GetField(
        "runningGame", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? LoadedGuisField = typeof(ClientMain).GetField(
        "LoadedGuis", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TextCaretXField = typeof(GuiElementEditableTextBase).GetField(
        "caretX", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TextRenderLeftOffsetField = typeof(GuiElementEditableTextBase).GetField(
        "renderLeftOffset", BindingFlags.Instance | BindingFlags.NonPublic);
    internal bool HasControllerWindow => sdlWindowHost != null || window != null;
    internal (int Width, int Height) ControllerWindowSize => sdlWindowHost?.PixelSize ??
        (window?.ClientSize.X ?? 0, window?.ClientSize.Y ?? 0);
    internal Vector2 ControllerCursorPosition
    {
        get
        {
            if (sdlWindowHost is { } host)
            {
                (float x, float y) = host.MousePosition;
                return SdlWindowCoordinates.ToPixels(new Vector2(x, y), host.WindowSize, host.PixelSize);
            }
            return window?.MousePosition ?? default;
        }
        set
        {
            if (sdlWindowHost is { } host)
            {
                Vector2 current = ControllerCursorPosition;
                Vector2 distance = value - current;
                if (distance.X * distance.X + distance.Y * distance.Y < 0.25f) return;
                Vector2 logical = SdlWindowCoordinates.ToLogical(value, host.WindowSize, host.PixelSize);
                pendingSdlControllerWarpPixels = value;
                pendingSdlControllerWarpUntil = Environment.TickCount64 + 100;
                host.WarpMouse(logical.X, logical.Y);
            }
            else if (window != null) window.MousePosition = value;
        }
    }

    public override Vector2 OptimumWindowMousePosition() =>
        sdlWindowHost != null ? ControllerCursorPosition : base.OptimumWindowMousePosition();

    private Vector2 SdlMousePixels(float x, float y) => sdlWindowHost is { } host
        ? SdlWindowCoordinates.ToPixels(new Vector2(x, y), host.WindowSize, host.PixelSize)
        : new Vector2(x, y);

    public override bool IsFocused => sdlWindowHost?.IsFocused ?? base.IsFocused;
    public override Vintagestory.API.MathTools.Size2i ScreenSize
    {
        get
        {
            if (sdlWindowHost is not { } host) return base.ScreenSize;
            (int width, int height) = host.DisplaySize;
            return new Vintagestory.API.MathTools.Size2i(width, height);
        }
    }
    public override Vintagestory.API.MathTools.Size2i OptimumWindowClientSize()
    {
        if (sdlWindowHost is { } host)
        {
            (int width, int height) = host.PixelSize;
            return new Vintagestory.API.MathTools.Size2i(width, height);
        }
        return base.OptimumWindowClientSize();
    }

    public override void SetDirectMouseMode(bool enabled)
    {
        if (sdlWindowHost == null) base.SetDirectMouseMode(enabled);
    }

    public override void SetTitle(string title)
    {
        if (sdlWindowHost is { } host) host.SetTitle(title);
        if (window != null) base.SetTitle(title);
    }

    public override void SetWindowSize(int width, int height)
    {
        if (sdlWindowHost is { } host) host.SetSize(width, height);
        else base.SetWindowSize(width, height);
    }

    public override EnumWindowBorder WindowBorder
    {
        get => sdlWindowHost != null ? sdlWindowBorder : base.WindowBorder;
        set
        {
            if (sdlWindowHost is not { } host) { base.WindowBorder = value; return; }
            sdlWindowBorder = value;
            if (!host.IsFullscreen) ApplySdlWindowBorder(host);
        }
    }

    private void ApplySdlWindowBorder(SdlVulkanWindowHost host)
    {
        // Borderless-maximized mode still needs a resizable window for SDL_MaximizeWindow.
        host.SetResizable(sdlWindowBorder != EnumWindowBorder.Fixed);
        host.SetBordered(sdlWindowBorder != EnumWindowBorder.Hidden);
    }

    public override WindowState GetWindowState()
    {
        if (sdlWindowHost is not { } host) return base.GetWindowState();
        if (host.IsFullscreen) return (WindowState)3;
        if (host.IsMinimized) return (WindowState)1;
        if (host.IsMaximized) return (WindowState)2;
        return (WindowState)0;
    }

    public override void WindowFocus()
    {
        if (sdlWindowHost is not { } host) { base.WindowFocus(); return; }
        host.Focus();
    }

    public override void SetWindowState(WindowState value)
    {
        if (sdlWindowHost is not { } host) { base.SetWindowState(value); return; }
        if ((int)value == 3) { host.SetFullscreen(true); return; }
        if (host.IsFullscreen)
        {
            host.SetFullscreen(false);
            host.Sync();
            ApplySdlWindowBorder(host);
        }
        switch ((int)value)
        {
            case 0: host.Restore(); break;
            case 1: host.Minimize(); break;
            case 2: host.Maximize(); break;
            default: throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    public override void SetWindowAttribute(WindowAttribute attribute, bool value)
    {
        if (sdlWindowHost is not { } host) { base.SetWindowAttribute(attribute, value); return; }
        if ((int)attribute == 131078)
        {
            if (!host.SetMinimizeOnFocusLoss(value))
                Logger.Warning("SDL could not update fullscreen minimize-on-focus-loss behavior");
        }
        else Logger.Warning("No SDL equivalent for window attribute {0}", attribute);
    }

    public override bool LoadMouseCursor(string code, int hotx, int hoty, BitmapRef bmpRef)
    {
        if (sdlWindowHost is not { } host) return base.LoadMouseCursor(code, hotx, hoty, bmpRef);
        try
        {
            SKBitmap source = ((BitmapExternal)bmpRef).bmp;
            // SDL accepts larger color cursors; limiting the source to 32 px
            // drops otherwise valid game cursors on scaled desktop displays.
            float scale = ClientSettings.GUIScale;
            SKBitmap bitmap = scale == 1f ? source : source.Resize(
                new SKImageInfo(Math.Max(1, (int)(source.Width * scale)),
                    Math.Max(1, (int)(source.Height * scale))),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (bitmap == null) return false;
            try
            {
                byte[] rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
                int offset = 0;
                for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SKColor pixel = bitmap.GetPixel(x, y);
                    rgba[offset++] = pixel.Red;
                    rgba[offset++] = pixel.Green;
                    rgba[offset++] = pixel.Blue;
                    rgba[offset++] = pixel.Alpha;
                }
                host.LoadCursor(code, Math.Clamp((int)(hotx * scale), 0, bitmap.Width - 1),
                    Math.Clamp((int)(hoty * scale), 0, bitmap.Height - 1),
                    bitmap.Width, bitmap.Height, rgba);
            }
            finally { if (!ReferenceEquals(bitmap, source)) bitmap.Dispose(); }
            return true;
        }
        catch (Exception error)
        {
            Logger.Error("Failed loading SDL mouse cursor {0}: {1}", code, error);
            RestoreWindowCursor();
            return false;
        }
    }

    public override void UseMouseCursor(string code, bool forceUpdate = false)
    {
        if (sdlWindowHost is not { } host) { base.UseMouseCursor(code, forceUpdate); return; }
        if (code == CurrentMouseCursor && !forceUpdate) return;
        try
        {
            if (code == null)
            {
                host.RestoreCursor();
                CurrentMouseCursor = null;
            }
            else CurrentMouseCursor = host.UseCursor(code) ? code : null;
        }
        catch (Exception error)
        {
            Logger.Error("Failed selecting SDL mouse cursor {0}: {1}", code, error);
            RestoreWindowCursor();
        }
    }

    public override void RestoreWindowCursor()
    {
        if (sdlWindowHost is { } host)
        {
            host.RestoreCursor();
            CurrentMouseCursor = null;
        }
        else base.RestoreWindowCursor();
    }

    public override void WindowExit(string reason, EnumExitMode mode)
    {
        if (sdlWindowHost != null) sdlCloseRequested = true;
        base.WindowExit(reason, mode);
    }

    public int SdlPixelWidth => sdlWindowHost?.PixelSize.Width ?? 0;
    public int SdlPixelHeight => sdlWindowHost?.PixelSize.Height ?? 0;

    public bool TryInitializeSdlWindow(string title, int width, int height, bool hidden,
        bool fullscreen, out string reason)
    {
        SdlVulkanWindowHost? host = null;
        try
        {
            host = SdlVulkanWindowHost.Create(title, width, height, hidden);
            if (!hidden) SetSdlWindowIcon(host);
            sdlWindowBorder = (EnumWindowBorder)Math.Clamp(ClientSettings.WindowBorder, 0, 2);
            ApplySdlWindowBorder(host);
            if (fullscreen) host.SetFullscreen(true);
            if (!InitializeSdlGraphics(host, out reason))
            {
                host.Dispose();
                return false;
            }
            uint pclMessage = device.PclWindowMessage;
            if (pclMessage != 0 && !host.InstallPclPingHook(pclMessage, device.MarkPclLatencyPing))
                Logger.Warning("SDL could not install the Streamline PCL Windows message hook");
            else if (pclMessage != 0 &&
                Environment.GetEnvironmentVariable("OPTIMUM_LATENCY_TRACE") == "1")
                Logger.Notification("[Optimum] SDL PCL ping hook: message={0}, hwnd={1}",
                    pclMessage, host.Win32Handle);
            ownsSdlWindow = true;
            return true;
        }
        catch (Exception error)
        {
            host?.Dispose();
            reason = error.Message;
            return false;
        }
    }

    public void RunSdlWindow() => RunSdlClientLoop();

    private void SetSdlWindowIcon(SdlVulkanWindowHost host)
    {
        string? assets = Vintagestory.API.Config.GamePaths.AssetsPath;
        if (string.IsNullOrEmpty(assets)) return;
        string path = System.IO.Path.Combine(assets, "gameicon.png");
        if (!System.IO.File.Exists(path)) return;
        try
        {
            using SKBitmap? bitmap = SKBitmap.Decode(path);
            if (bitmap == null) return;
            byte[] rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            int offset = 0;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                rgba[offset++] = pixel.Red;
                rgba[offset++] = pixel.Green;
                rgba[offset++] = pixel.Blue;
                rgba[offset++] = pixel.Alpha;
            }
            host.SetIcon(bitmap.Width, bitmap.Height, rgba);
        }
        catch (Exception error)
        {
            Logger.Warning("SDL window icon unavailable: {0}", error.Message);
        }
    }

    private void SyncSdlTextInput()
    {
        if (sdlWindowHost is not { } host) return;
        GuiElementEditableTextBase? focused = host.IsFocused ? FocusedEditableText() : null;
        if (focused == null)
        {
            StopSdlTextInput();
            return;
        }
        if (!ReferenceEquals(focused, sdlTextInputTarget))
        {
            // Native IME preedit belongs to one field. Dismiss it before SDL
            // starts positioning the candidate UI over a different field.
            if (sdlTextInputActive) host.ClearComposition();
            sdlCompositionText = "";
            sdlTextInputTarget = focused;
        }

        // Gui element bounds use drawable pixels; SDL's IME area uses logical window
        // coordinates, which can differ on high-density handheld displays.
        (int logicalWidth, int logicalHeight) = host.WindowSize;
        if (logicalWidth <= 0 || logicalHeight <= 0) return;
        (int pixelWidth, int pixelHeight) = host.PixelSize;
        double scaleX = (double)logicalWidth / Math.Max(1, pixelWidth);
        double scaleY = (double)logicalHeight / Math.Max(1, pixelHeight);
        var bounds = focused.Bounds;
        int x = Math.Clamp((int)Math.Round(bounds.absX * scaleX), 0, logicalWidth - 1);
        int y = Math.Clamp((int)Math.Round(bounds.absY * scaleY), 0, logicalHeight - 1);
        int width = Math.Clamp((int)Math.Round(bounds.OuterWidth * scaleX), 1, logicalWidth - x);
        int height = Math.Clamp((int)Math.Round(bounds.OuterHeight * scaleY), 1, logicalHeight - y);
        // The native IME UI is anchored at the caret, not the beginning of the field.
        // These are the same rendered positions the game's editable element uses;
        // the offset matters when a long line has scrolled horizontally.
        double caretX = TextCaretXField?.GetValue(focused) is double renderedCaretX ? renderedCaretX : 0;
        double leftOffset = TextRenderLeftOffsetField?.GetValue(focused) is double renderedLeftOffset
            ? renderedLeftOffset : 0;
        double caretPixelX = bounds.renderX + caretX - leftOffset;
        int cursor = SdlWindowCoordinates.TextInputCursorOffset(
            caretPixelX, bounds.absX, width, logicalWidth, pixelWidth);
        var area = (x, y, width, height, cursor);
        if (sdlTextInputArea != area)
        {
            host.SetTextInputArea(x, y, width, height, cursor);
            sdlTextInputArea = area;
        }
        if (!sdlTextInputActive)
        {
            host.SetTextInputActive(true);
            sdlTextInputActive = true;
        }
    }

    private static GuiElementEditableTextBase? FocusedEditableText()
    {
        if (ClientProgram.screenManager is not { } manager ||
            CurrentScreenField?.GetValue(manager) is not GuiScreen screen) return null;
        if (screen is ControllerKeyboardScreen keyboardScreen) return keyboardScreen.Target;
        if (screen is GuiScreenRunningGame running)
        {
            if (RunningGameField?.GetValue(running) is not ClientMain game ||
                LoadedGuisField?.GetValue(game) is not List<GuiDialog> dialogs) return null;
            foreach (GuiDialog dialog in dialogs)
            {
                if (!dialog.ShouldReceiveKeyboardEvents()) continue;
                if (dialog is ControllerKeyboardDialog keyboardDialog && dialog.IsOpened())
                    return keyboardDialog.Target;
                foreach (GuiComposer composer in dialog.Composers.Values)
                    if (FocusedEditableText(composer) is { } text) return text;
            }
            return null;
        }
        return FocusedEditableText(screen.ElementComposer);
    }

    internal GuiElementEditableTextBase? ControllerFocusedEditableText() => FocusedEditableText();
    internal GuiScreen? ControllerCurrentScreen() =>
        ClientProgram.screenManager is { } manager ? CurrentScreenField?.GetValue(manager) as GuiScreen : null;

    private static GuiElementEditableTextBase? FocusedEditableText(GuiComposer? composer)
    {
        if (composer?.Enabled != true || !composer.Composed) return null;
        GuiElement? focused = composer.CurrentTabIndexElement;
        while (focused is GuiElementContainer container)
            focused = container.CurrentTabIndexElement;
        return focused is GuiElementEditableTextBase text && text.HasFocus ? text : null;
    }

    private void StopSdlTextInput()
    {
        if (sdlTextInputActive && sdlWindowHost is { } host)
        {
            host.ClearComposition();
            host.SetTextInputActive(false);
        }
        sdlTextInputActive = false;
        sdlTextInputTarget = null;
        sdlCompositionText = "";
        sdlTextInputArea = null;
    }

    private bool HasCurrentSdlTextTarget() =>
        sdlTextInputActive && sdlWindowHost?.IsFocused == true &&
        sdlTextInputTarget != null &&
        ReferenceEquals(sdlTextInputTarget, FocusedEditableText());

    public override bool MouseGrabbed
    {
        get => sdlWindowHost?.RelativeMouseMode ?? base.MouseGrabbed;
        set
        {
            if (sdlWindowHost is { } host) host.SetRelativeMouseMode(value);
            else base.MouseGrabbed = value;
        }
    }

    internal void DispatchSdlInput(SdlInputEvent input)
    {
        if (input.Type == SdlEventPump.Quit)
        {
            if (OptimumSdlCloseAllowed()) sdlCloseRequested = true;
            return;
        }
        if (input.DisplayId != 0)
        {
            if (sdlWindowHost is { } host &&
                (input.Type is SdlEventPump.DisplayAdded or SdlEventPump.DisplayRemoved ||
                 input.DisplayId == host.DisplayId))
                QueueSdlWindowLayoutUpdate();
            return;
        }
        if (SdlWindowId != 0 && input.WindowId != SdlWindowId &&
            // SDL can deliver an app-level file-open/drop without a target
            // window. Give it to the focused game window, never a background one.
            !(input.Type == SdlEventPump.DropFile && input.WindowId == 0 &&
              sdlWindowHost?.IsFocused == true)) return;
        if (input.MouseId == SdlEventPump.TouchMouseId &&
            input.Type is SdlEventPump.MouseMotion or SdlEventPump.MouseButtonDown or
                SdlEventPump.MouseButtonUp or SdlEventPump.MouseWheel) return;
        if (sdlWindowHost != null && input.TimestampNanoseconds != 0 &&
            input.Type is SdlEventPump.KeyDown or SdlEventPump.KeyUp or
                SdlEventPump.TextEditing or SdlEventPump.TextInput or
                SdlEventPump.MouseMotion or SdlEventPump.MouseButtonDown or
                SdlEventPump.MouseButtonUp or SdlEventPump.MouseWheel or
                SdlEventPump.FingerDown or SdlEventPump.FingerMotion or
                SdlEventPump.FingerUp or SdlEventPump.FingerCanceled)
            VulkanStats.RecordSdlInputAge(input.TimestampNanoseconds, SdlEventPump.TicksNanoseconds());
        if (input.Type == SdlEventPump.TextEditing)
        {
            // SDL's native IME UI draws preedit/candidates. Never inject preedit
            // into the game's text callbacks: only SDL_TEXT_INPUT commits text.
            if (!HasCurrentSdlTextTarget())
            {
                SyncSdlTextInput();
                return;
            }
            sdlCompositionText = input.Text ?? "";
            return;
        }
        if (input.Type == SdlEventPump.KeyDown && input.Key == GlKeys.Escape &&
            sdlCompositionText.Length != 0)
        {
            // Escape dismisses the active IME before it can close chat or a
            // menu. The native candidate UI owns the preedit text.
            sdlWindowHost?.ClearComposition();
            sdlCompositionText = "";
            return;
        }
        if (input.Type is SdlEventPump.KeyDown or SdlEventPump.KeyUp or SdlEventPump.TextInput)
        {
            // A text commit can already be queued when focus leaves a field.
            // Do not deliver it to whichever screen receives the rest of this frame.
            if (input.Type == SdlEventPump.TextInput && !HasCurrentSdlTextTarget())
            {
                SyncSdlTextInput();
                return;
            }
            if (input.Type == SdlEventPump.TextInput) sdlCompositionText = "";
            DispatchSdlKeyboard(input);
            if (input.Type == SdlEventPump.KeyDown) SyncSdlTextInput();
            return;
        }
        switch (input.Type)
        {
            case SdlEventPump.FingerDown:
            case SdlEventPump.FingerMotion:
            case SdlEventPump.FingerUp:
            case SdlEventPump.FingerCanceled:
                if (sdlWindowHost is { } touchHost)
                {
                    sdlTouchMouse ??= new SdlTouchMouse(InjectPhysicalMouseMotion,
                        (button, down, x, y) =>
                        {
                            InjectTouchMouseButton(button, down, x, y);
                            if (down) SyncSdlTextInput();
                        });
                    sdlTouchMouse.Handle(input, touchHost.WindowSize, touchHost.PixelSize,
                        SdlEventPump.TicksNanoseconds());
                }
                break;
            case SdlEventPump.DropFile:
                if (!string.IsNullOrEmpty(input.Text)) fileDropEventHandler?.Invoke(input.Text);
                break;
            case SdlEventPump.MouseMotion:
            {
                Vector2 position = SdlMousePixels(input.X, input.Y);
                Vector2 delta = SdlMousePixels(input.DeltaX, input.DeltaY);
                bool controllerWarp = pendingSdlControllerWarpPixels is { } expected &&
                    Environment.TickCount64 <= pendingSdlControllerWarpUntil &&
                    (position - expected).LengthSquared < 4f;
                if (controllerWarp || Environment.TickCount64 > pendingSdlControllerWarpUntil)
                    pendingSdlControllerWarpPixels = null;
                if (controllerWarp) InjectControllerMouseMotion(position.X, position.Y, delta.X, delta.Y);
                else InjectPhysicalMouseMotion(position.X, position.Y, delta.X, delta.Y);
                break;
            }
            case SdlEventPump.MouseButtonDown:
            case SdlEventPump.MouseButtonUp:
            {
                Vector2 position = SdlMousePixels(input.X, input.Y);
                InjectPhysicalMouseButton(MouseButton(input.Button),
                    input.Type == SdlEventPump.MouseButtonDown, position.X, position.Y);
                if (input.Type == SdlEventPump.MouseButtonDown) SyncSdlTextInput();
                break;
            }
            case SdlEventPump.MouseWheel:
            {
                // SDL marks natural scrolling as flipped. Restore the game's
                // normal wheel direction before applying its sensitivity.
                Vector2 position = SdlMousePixels(input.X, input.Y);
                InjectPhysicalMouseWheel(input.Data1 == 1 ? -input.DeltaY : input.DeltaY,
                    position.X, position.Y);
                break;
            }
            case SdlEventPump.WindowFocusLost:
                StopSdlTextInput();
                pendingSdlControllerWarpPixels = null;
                sdlTouchMouse?.Cancel();
                InjectPhysicalFocusChanged(false);
                sdlGamepadInput?.OnFocusLost();
                break;
            case SdlEventPump.WindowFocusGained:
                InjectPhysicalFocusChanged(true);
                SyncSdlTextInput();
                break;
            case SdlEventPump.WindowCloseRequested:
                if (OptimumSdlCloseAllowed()) sdlCloseRequested = true;
                break;
            case SdlEventPump.WindowResized:
                if (input.Data1 > 0 && input.Data2 > 0 &&
                    sdlWindowHost is { IsFullscreen: false })
                {
                    ClientSettings.ScreenWidth = input.Data1;
                    ClientSettings.ScreenHeight = input.Data2;
                }
                QueueSdlWindowLayoutUpdate();
                break;
            case SdlEventPump.WindowMaximized:
            case SdlEventPump.WindowRestored:
            case SdlEventPump.WindowDisplayChanged:
            case SdlEventPump.WindowDisplayScaleChanged:
            case SdlEventPump.WindowSafeAreaChanged:
            case SdlEventPump.WindowEnterFullscreen:
            case SdlEventPump.WindowLeaveFullscreen:
                QueueSdlWindowLayoutUpdate();
                break;
            case SdlEventPump.WindowPixelSizeChanged:
                if (input.Data1 > 0 && input.Data2 > 0)
                    pendingSdlPixelSize = (input.Data1, input.Data2);
                break;
        }
    }

    private void QueueSdlWindowLayoutUpdate()
    {
        pendingSdlGuiRecompose = true;
        if (sdlWindowHost is not { } host) return;
        (int width, int height) = host.PixelSize;
        if (width > 0 && height > 0 &&
            (width != WindowSize.Width || height != WindowSize.Height))
            pendingSdlPixelSize = (width, height);
    }

    internal void ApplyPendingSdlResize()
    {
        if (device == null) return;
        bool recompose = pendingSdlGuiRecompose;
        pendingSdlGuiRecompose = false;
        bool resized = false;
        if (pendingSdlPixelSize is { } size)
        {
            pendingSdlPixelSize = null;
            if (WindowSize.Width != size.Width || WindowSize.Height != size.Height)
            {
                if (window != null) window.ClientSize = new Vector2i(size.Width, size.Height);
                OnWindowSizeChanged(size.Width, size.Height);
                WindowSize.Width = size.Width;
                WindowSize.Height = size.Height;
                if (FrameBuffers != null) RebuildFrameBuffers();
                if (sdlWindowHost is { IsFullscreen: false } host)
                    (ClientSettings.ScreenWidth, ClientSettings.ScreenHeight) = host.WindowSize;
                TriggerWindowResized(size.Width, size.Height);
                resized = true;
            }
        }
        if (recompose && ScreenManager.GuiComposers is { } composers)
            composers.MarkAllDialogsForRecompose();
        if (recompose && !resized && WindowSize.Width > 0 && WindowSize.Height > 0)
            TriggerWindowResized(WindowSize.Width, WindowSize.Height);
    }

    internal void RunSdlClientLoop(CancellationToken cancellationToken = default)
    {
        if (sdlWindowHost == null || device == null)
            throw new InvalidOperationException("SDL graphics must be initialized before the client loop");
        sdlCloseRequested = false;
        while (!sdlCloseRequested && !cancellationToken.IsCancellationRequested) OptimumRunSdlFrame();
    }

    internal static EnumMouseButton MouseButton(byte button) => button switch
    {
        1 => EnumMouseButton.Left,
        2 => EnumMouseButton.Middle,
        3 => EnumMouseButton.Right,
        >= 4 and <= 8 => EnumMouseButton.Button4 + (button - 4),
        _ => EnumMouseButton.None,
    };

    /// <summary>
    /// SDL keyboard events enter the same physical-key state as GLFW keyboard
    /// events. Text input remains separate so layout and IME output can be sent
    /// to focused game text fields without changing hotkey identities.
    /// </summary>
    internal void DispatchSdlKeyboard(SdlInputEvent input)
    {
        if (input.Type == SdlEventPump.TextInput)
        {
            InjectPhysicalText(input.Text ?? "");
            return;
        }
        if (input.Type is not (SdlEventPump.KeyDown or SdlEventPump.KeyUp) ||
            input.Key == GlKeys.Unknown) return;
        ushort mods = input.Modifiers;
        InjectPhysicalKey(new KeyEvent
        {
            KeyCode = (int)input.Key,
            ShiftPressed = (mods & 0x0003) != 0,
            CtrlPressed = (mods & 0x00C0) != 0,
            AltPressed = (mods & 0x0300) != 0,
            CommandPressed = (mods & 0x0C00) != 0,
        }, input.Type == SdlEventPump.KeyDown);
    }
}
