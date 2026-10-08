using System;
using System.Runtime.InteropServices;

namespace VulkanStory.Platform.Sdl;

/// <summary>
/// One SDL event queue reader for both gamepads and the SDL-owned client
/// window. SDL_PollEvent removes events, so separate readers would steal events
/// from each other. Input text is copied while SDL still owns its event buffer.
/// </summary>
public static unsafe class SdlEventPump
{
    private const int GamepadHandoffAxisThreshold = 16384; // half travel; ignore stick drift
    /// <summary>Returns SDL's monotonic nanosecond clock after registering the package import resolver.</summary>
    public static ulong TicksNanoseconds()
    {
        SdlNativeLibrary.EnsureRegistered();
        return SDL_GetTicksNS();
    }

    /// <summary>SDL application quit-request event.</summary>
    public const uint Quit = 0x100;
    /// <summary>SDL gamepad-added event used to request device reconciliation.</summary>
    public const uint GamepadAdded = 0x653;
    /// <summary>SDL gamepad-removed event used to request device reconciliation.</summary>
    public const uint GamepadRemoved = 0x654;
    /// <summary>SDL mapping-change event used to refresh device capabilities and prompts.</summary>
    public const uint GamepadRemapped = 0x655;
    /// <summary>SDL gamepad axis event sampled for active-device handoff.</summary>
    public const uint GamepadAxisMotion = 0x650;
    /// <summary>SDL gamepad button press event sampled for active-device handoff.</summary>
    public const uint GamepadButtonDown = 0x651;
    /// <summary>Physical key press event; committed text is carried separately.</summary>
    public const uint KeyDown = 0x300;
    /// <summary>Physical key release event.</summary>
    public const uint KeyUp = 0x301;
    /// <summary>IME preedit/composition event with selection offsets.</summary>
    public const uint TextEditing = 0x302;
    /// <summary>Committed UTF-8 text event.</summary>
    public const uint TextInput = 0x303;
    /// <summary>Mouse position and relative-motion event.</summary>
    public const uint MouseMotion = 0x400;
    /// <summary>Mouse-button press event.</summary>
    public const uint MouseButtonDown = 0x401;
    /// <summary>Mouse-button release event.</summary>
    public const uint MouseButtonUp = 0x402;
    /// <summary>Mouse-wheel event carrying precise amounts and direction.</summary>
    public const uint MouseWheel = 0x403;
    /// <summary>Finger contact-start event with normalized coordinates.</summary>
    public const uint FingerDown = 0x700;
    /// <summary>Finger contact-release event with normalized coordinates.</summary>
    public const uint FingerUp = 0x701;
    /// <summary>Finger motion event with normalized coordinates/deltas.</summary>
    public const uint FingerMotion = 0x702;
    /// <summary>Finger cancellation event used to release synthetic touch input.</summary>
    public const uint FingerCanceled = 0x703;
    /// <summary>SDL mouse identity for synthetic touch-mouse events filtered by the game adapter.</summary>
    public const uint TouchMouseId = uint.MaxValue;
    /// <summary>Dropped-file event whose path is copied to managed text.</summary>
    public const uint DropFile = 0x1000;
    /// <summary>Inclusive lower type boundary for decoded display events.</summary>
    public const uint DisplayFirst = 0x151;
    /// <summary>Inclusive upper type boundary for decoded display events.</summary>
    public const uint DisplayLast = 0x158;
    /// <summary>SDL display-added notification.</summary>
    public const uint DisplayAdded = 0x152;
    /// <summary>SDL display-removed notification.</summary>
    public const uint DisplayRemoved = 0x153;
    /// <summary>Inclusive lower type boundary for decoded window events.</summary>
    public const uint WindowFirst = 0x202;
    /// <summary>Inclusive upper type boundary for decoded window events.</summary>
    public const uint WindowLast = 0x220;
    /// <summary>SDL window input-focus gained notification.</summary>
    public const uint WindowFocusGained = 0x20E;
    /// <summary>SDL window input-focus lost notification.</summary>
    public const uint WindowFocusLost = 0x20F;
    /// <summary>SDL window close request dispatched to the frame owner's close policy.</summary>
    public const uint WindowCloseRequested = 0x210;
    /// <summary>Logical window-size change notification.</summary>
    public const uint WindowResized = 0x206;
    /// <summary>Drawable pixel-size change notification.</summary>
    public const uint WindowPixelSizeChanged = 0x207;
    /// <summary>SDL window maximized notification.</summary>
    public const uint WindowMaximized = 0x20A;
    /// <summary>SDL window restored notification.</summary>
    public const uint WindowRestored = 0x20B;
    /// <summary>Window display-association change notification.</summary>
    public const uint WindowDisplayChanged = 0x213;
    /// <summary>Window display-scale change notification.</summary>
    public const uint WindowDisplayScaleChanged = 0x214;
    /// <summary>Window safe-area change notification.</summary>
    public const uint WindowSafeAreaChanged = 0x215;
    /// <summary>Window entered fullscreen notification.</summary>
    public const uint WindowEnterFullscreen = 0x217;
    /// <summary>Window left fullscreen notification.</summary>
    public const uint WindowLeaveFullscreen = 0x218;

    /// <summary>Pumps native events, refreshes optional gamepad state, marks completed collection, and drains the shared queue.</summary>
    /// <param name="onWindowEvent">Receives decoded neutral events with managed copies of text payloads.</param>
    /// <param name="onInputPumped">Runs after PumpEvents and gamepad refresh, before event dispatch.</param>
    /// <param name="onGamepadActivity">Receives a device ID on a button press or axis movement of at least half travel.</param>
    /// <param name="updateGamepads">Optional refresh callback invoked before the completed-pump marker.</param>
    /// <returns>Whether any gamepad added/removed/remapped event was observed.</returns>
    /// <remarks>The frame owner must call this once; independent SDL_PollEvent readers would remove each other's events.</remarks>
    public static bool Drain(Action<SdlInputEvent>? onWindowEvent = null,
        Action? onInputPumped = null, Action<int>? onGamepadActivity = null,
        Action? updateGamepads = null)
    {
        SdlNativeLibrary.EnsureRegistered();
        SDL_PumpEvents();
        // The input marker describes the state consumed by this frame. Refresh
        // the optional gamepad state before stamping it or dispatching events.
        updateGamepads?.Invoke();
        onInputPumped?.Invoke();
        byte* buffer = stackalloc byte[128]; // sizeof(SDL_Event)
        bool deviceChanged = false;
        while (SDL_PollEvent(buffer))
        {
            uint type = *(uint*)buffer;
            deviceChanged |= type is GamepadAdded or GamepadRemoved or GamepadRemapped;
            if (onGamepadActivity != null && type == GamepadButtonDown && buffer[21] != 0)
                onGamepadActivity(*(int*)(buffer + 16));
            if (onGamepadActivity != null && type == GamepadAxisMotion &&
                Math.Abs((int)*(short*)(buffer + 24)) >= GamepadHandoffAxisThreshold)
                onGamepadActivity(*(int*)(buffer + 16));
            if (onWindowEvent != null && Decode((nint)buffer) is { } input)
                onWindowEvent(input);
        }
        return deviceChanged;
    }

    /// <summary>Decodes a live SDL_Event buffer into the supported neutral input/window/display shape.</summary>
    /// <param name="eventPointer">Pointer to a valid SDL_Event buffer; referenced native strings must remain valid during this call.</param>
    /// <returns>A managed value with copied text, or null for event types outside this adapter's supported set.</returns>
    public static SdlInputEvent? Decode(nint eventPointer)
    {
        uint type = unchecked((uint)Marshal.ReadInt32(eventPointer));
        if (type is not (Quit or KeyDown or KeyUp or TextEditing or TextInput or MouseMotion or MouseButtonDown or MouseButtonUp or MouseWheel or
            FingerDown or FingerUp or FingerMotion or FingerCanceled or DropFile) &&
            (type < WindowFirst || type > WindowLast) &&
            (type < DisplayFirst || type > DisplayLast)) return null;
        ulong timestamp = unchecked((ulong)Marshal.ReadInt64(eventPointer, 8));
        if (type >= DisplayFirst && type <= DisplayLast)
            return new(type, timestamp, 0,
                Data1: Marshal.ReadInt32(eventPointer, 20),
                Data2: Marshal.ReadInt32(eventPointer, 24),
                DisplayId: unchecked((uint)Marshal.ReadInt32(eventPointer, 16)));
        if (type is FingerDown or FingerUp or FingerMotion or FingerCanceled)
            return new(type, timestamp, unchecked((uint)Marshal.ReadInt32(eventPointer, 52)),
                X: FloatAt(eventPointer, 32), Y: FloatAt(eventPointer, 36),
                DeltaX: FloatAt(eventPointer, 40), DeltaY: FloatAt(eventPointer, 44),
                TouchId: unchecked((ulong)Marshal.ReadInt64(eventPointer, 16)),
                FingerId: unchecked((ulong)Marshal.ReadInt64(eventPointer, 24)));
        uint windowId = unchecked((uint)Marshal.ReadInt32(eventPointer, 16));
        if (type is KeyDown or KeyUp)
        {
            int scancode = Marshal.ReadInt32(eventPointer, 24);
            ushort modifiers = unchecked((ushort)Marshal.ReadInt16(eventPointer, 32));
            return new(type, timestamp, windowId, Scancode: scancode, Modifiers: modifiers,
                Repeat: Marshal.ReadByte(eventPointer, 37) != 0);
        }
        if (type == TextInput)
            return new(type, timestamp, windowId,
                Text: Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(eventPointer, 24)) ?? "");
        if (type == TextEditing)
            return new(type, timestamp, windowId,
                Text: Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(eventPointer, 24)) ?? "",
                EditStart: Marshal.ReadInt32(eventPointer, 32),
                EditLength: Marshal.ReadInt32(eventPointer, 36));
        if (type == DropFile)
            return new(type, timestamp, windowId,
                Text: Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(eventPointer, 40)) ?? "");
        if (type == MouseMotion)
            return new(type, timestamp, windowId,
                X: FloatAt(eventPointer, 28), Y: FloatAt(eventPointer, 32),
                DeltaX: FloatAt(eventPointer, 36), DeltaY: FloatAt(eventPointer, 40),
                MouseId: unchecked((uint)Marshal.ReadInt32(eventPointer, 20)));
        if (type is MouseButtonDown or MouseButtonUp)
            return new(type, timestamp, windowId,
                X: FloatAt(eventPointer, 28), Y: FloatAt(eventPointer, 32),
                Button: Marshal.ReadByte(eventPointer, 24),
                MouseId: unchecked((uint)Marshal.ReadInt32(eventPointer, 20)));
        if (type == MouseWheel)
            return new(type, timestamp, windowId,
                X: FloatAt(eventPointer, 36), Y: FloatAt(eventPointer, 40),
                DeltaX: FloatAt(eventPointer, 24), DeltaY: FloatAt(eventPointer, 28),
                Data1: Marshal.ReadInt32(eventPointer, 32),
                MouseId: unchecked((uint)Marshal.ReadInt32(eventPointer, 20)));
        return new(type, timestamp, windowId,
            Data1: Marshal.ReadInt32(eventPointer, 20), Data2: Marshal.ReadInt32(eventPointer, 24));
    }

    private static float FloatAt(nint pointer, int offset) =>
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer, offset));

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_PumpEvents();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong SDL_GetTicksNS();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_PollEvent(void* eventBuffer);
}

/// <summary>Managed copy of one supported SDL input/window/display event; only fields relevant to its Type are populated.</summary>
/// <param name="Type">SDL event type token.</param>
/// <param name="TimestampNanoseconds">SDL event timestamp in nanoseconds.</param>
/// <param name="WindowId">Target SDL window identity; display events use zero.</param>
/// <param name="Scancode">Physical keyboard scancode for key transitions.</param>
/// <param name="Modifiers">SDL keyboard modifier bitmask.</param>
/// <param name="Repeat">Whether a keyboard down event is a repeat.</param>
/// <param name="Text">Copied UTF-8 text, composition text, or dropped path.</param>
/// <param name="X">SDL coordinate; finger events use normalized coordinates.</param>
/// <param name="Y">SDL coordinate; finger events use normalized coordinates.</param>
/// <param name="DeltaX">Relative horizontal motion or wheel amount.</param>
/// <param name="DeltaY">Relative vertical motion or wheel amount.</param>
/// <param name="Button">SDL mouse-button index.</param>
/// <param name="Data1">Event-specific integer payload, including wheel direction.</param>
/// <param name="Data2">Second window/display integer payload.</param>
/// <param name="MouseId">Mouse identity, including the synthetic touch-mouse sentinel.</param>
/// <param name="TouchId">Touch device identity for finger events.</param>
/// <param name="FingerId">Finger identity within the touch device.</param>
/// <param name="EditStart">Composition selection start, or -1 outside text-edit events.</param>
/// <param name="EditLength">Composition selection length, or -1 outside text-edit events.</param>
/// <param name="DisplayId">Target display identity for display events.</param>
public readonly record struct SdlInputEvent(
    uint Type, ulong TimestampNanoseconds, uint WindowId,
    int Scancode = 0, ushort Modifiers = 0, bool Repeat = false,
    string? Text = null, float X = 0, float Y = 0,
    float DeltaX = 0, float DeltaY = 0, byte Button = 0,
    int Data1 = 0, int Data2 = 0, uint MouseId = 0,
    ulong TouchId = 0, ulong FingerId = 0,
    int EditStart = -1, int EditLength = -1, uint DisplayId = 0);
