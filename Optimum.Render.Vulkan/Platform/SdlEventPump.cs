using System;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// One SDL event queue reader for both gamepads and the SDL-owned client
/// window. SDL_PollEvent removes events, so separate readers would steal events
/// from each other. Input text is copied while SDL still owns its event buffer.
/// </summary>
internal static unsafe class SdlEventPump
{
    private const int GamepadHandoffAxisThreshold = 16384; // half travel; ignore stick drift
    internal static ulong TicksNanoseconds() => SDL_GetTicksNS();

    internal const uint Quit = 0x100;
    internal const uint GamepadAdded = 0x653;
    internal const uint GamepadRemoved = 0x654;
    internal const uint GamepadRemapped = 0x655;
    internal const uint GamepadAxisMotion = 0x650;
    internal const uint GamepadButtonDown = 0x651;
    internal const uint KeyDown = 0x300;
    internal const uint KeyUp = 0x301;
    internal const uint TextEditing = 0x302;
    internal const uint TextInput = 0x303;
    internal const uint MouseMotion = 0x400;
    internal const uint MouseButtonDown = 0x401;
    internal const uint MouseButtonUp = 0x402;
    internal const uint MouseWheel = 0x403;
    internal const uint FingerDown = 0x700;
    internal const uint FingerUp = 0x701;
    internal const uint FingerMotion = 0x702;
    internal const uint FingerCanceled = 0x703;
    internal const uint TouchMouseId = uint.MaxValue;
    internal const uint DropFile = 0x1000;
    internal const uint DisplayFirst = 0x151;
    internal const uint DisplayLast = 0x158;
    internal const uint DisplayAdded = 0x152;
    internal const uint DisplayRemoved = 0x153;
    internal const uint WindowFirst = 0x202;
    internal const uint WindowLast = 0x220;
    internal const uint WindowFocusGained = 0x20E;
    internal const uint WindowFocusLost = 0x20F;
    internal const uint WindowCloseRequested = 0x210;
    internal const uint WindowResized = 0x206;
    internal const uint WindowPixelSizeChanged = 0x207;
    internal const uint WindowMaximized = 0x20A;
    internal const uint WindowRestored = 0x20B;
    internal const uint WindowDisplayChanged = 0x213;
    internal const uint WindowDisplayScaleChanged = 0x214;
    internal const uint WindowSafeAreaChanged = 0x215;
    internal const uint WindowEnterFullscreen = 0x217;
    internal const uint WindowLeaveFullscreen = 0x218;

    internal static bool Drain(Action<SdlInputEvent>? onWindowEvent = null,
        Action? onInputPumped = null, Action<int>? onGamepadActivity = null,
        Action? updateGamepads = null)
    {
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

    internal static SdlInputEvent? Decode(nint eventPointer)
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
            return new(type, timestamp, windowId, Key: SdlKeyMap.ToGlKey(scancode), Modifiers: modifiers,
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

internal readonly record struct SdlInputEvent(
    uint Type, ulong TimestampNanoseconds, uint WindowId,
    GlKeys Key = GlKeys.Unknown, ushort Modifiers = 0, bool Repeat = false,
    string? Text = null, float X = 0, float Y = 0,
    float DeltaX = 0, float DeltaY = 0, byte Button = 0,
    int Data1 = 0, int Data2 = 0, uint MouseId = 0,
    ulong TouchId = 0, ulong FingerId = 0,
    int EditStart = -1, int EditLength = -1, uint DisplayId = 0);
