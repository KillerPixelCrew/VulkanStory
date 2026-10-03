using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// Physical SDL scancodes to the game's layout-independent hotkey codes.
/// Text entry must use SDL_EVENT_TEXT_INPUT instead of this map so keyboard
/// layouts, dead keys, and IME composition remain intact.
/// </summary>
internal static class SdlKeyMap
{
    internal static GlKeys ToGlKey(int scancode) => scancode switch
    {
        >= 4 and <= 29 => GlKeys.A + (scancode - 4),
        >= 30 and <= 38 => GlKeys.Number1 + (scancode - 30),
        39 => GlKeys.Number0,
        40 => GlKeys.Enter,
        41 => GlKeys.Escape,
        42 => GlKeys.BackSpace,
        43 => GlKeys.Tab,
        44 => GlKeys.Space,
        45 => GlKeys.Minus,
        46 => GlKeys.Plus,
        47 => GlKeys.BracketLeft,
        48 => GlKeys.BracketRight,
        49 or 50 or 100 => GlKeys.BackSlash,
        51 => GlKeys.Semicolon,
        52 => GlKeys.Quote,
        53 => GlKeys.Tilde,
        54 => GlKeys.Comma,
        55 => GlKeys.Period,
        56 => GlKeys.Slash,
        57 => GlKeys.CapsLock,
        >= 58 and <= 69 => GlKeys.F1 + (scancode - 58),
        70 => GlKeys.PrintScreen,
        71 => GlKeys.ScrollLock,
        72 => GlKeys.Pause,
        73 => GlKeys.Insert,
        74 => GlKeys.Home,
        75 => GlKeys.PageUp,
        76 => GlKeys.Delete,
        77 => GlKeys.End,
        78 => GlKeys.PageDown,
        79 => GlKeys.Right,
        80 => GlKeys.Left,
        81 => GlKeys.Down,
        82 => GlKeys.Up,
        83 => GlKeys.NumLock,
        84 => GlKeys.KeypadDivide,
        85 => GlKeys.KeypadMultiply,
        86 => GlKeys.KeypadSubtract,
        87 => GlKeys.KeypadAdd,
        88 => GlKeys.KeypadEnter,
        >= 89 and <= 97 => GlKeys.Keypad1 + (scancode - 89),
        98 => GlKeys.Keypad0,
        99 => GlKeys.KeypadDecimal,
        101 or 118 => GlKeys.Menu,
        >= 104 and <= 115 => GlKeys.F13 + (scancode - 104),
        156 => GlKeys.Clear,
        224 => GlKeys.ControlLeft,
        225 => GlKeys.ShiftLeft,
        226 => GlKeys.AltLeft,
        227 => GlKeys.WinLeft,
        228 => GlKeys.ControlRight,
        229 => GlKeys.ShiftRight,
        230 => GlKeys.AltRight,
        231 => GlKeys.WinRight,
        258 => GlKeys.Sleep,
        _ => GlKeys.Unknown,
    };
}
