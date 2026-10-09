using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Game.Input;

/// <summary>Converts game Skia bitmaps to the tightly packed, unpremultiplied RGBA8 pixels accepted by the SDL host.</summary>
internal static class SdlBitmapPixels
{
    /// <summary>Copies the bitmap as width × height unpremultiplied RGBA8 texels in row order.</summary>
    /// <remarks>Uses one Skia pixel conversion; falls back to per-texel reads only when the pixels cannot be read directly.</remarks>
    internal static unsafe byte[] ToRgba(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int width = bitmap.Width, height = bitmap.Height;
        byte[] rgba = new byte[checked(width * height * 4)];
        if (rgba.Length == 0) return rgba;
        using (SKPixmap? source = bitmap.PeekPixels())
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            fixed (byte* destination = rgba)
                if (source != null && source.ReadPixels(info, (nint)destination, width * 4, 0, 0)) return rgba;
        }
        // GetPixel also returns unpremultiplied colour, so both paths produce the same bytes.
        int offset = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            SKColor pixel = bitmap.GetPixel(x, y);
            rgba[offset++] = pixel.Red;
            rgba[offset++] = pixel.Green;
            rgba[offset++] = pixel.Blue;
            rgba[offset++] = pixel.Alpha;
        }
        return rgba;
    }
}

// Retained bitmap conversion and cursor ownership from VulkanClientPlatform.SdlInput.
/// <summary>Converts game cursor bitmaps to SDL RGBA cursors and keeps the original platform's cursor identity in sync.</summary>
/// <param name="platform">Original platform supplying cursor state and failure logging.</param>
/// <param name="bindings">Game state accessors used to publish the selected cursor.</param>
/// <param name="window">SDL window owning native cursor lifetimes.</param>
/// <param name="guiScale">Current GUI scale used to resize cursor images and hotspots.</param>
internal sealed class GameCursorController(ClientPlatformWindows platform,
    GamePlatformBindings bindings, SdlWindowHost window, System.Func<float> guiScale)
{
    /// <summary>Uploads a scaled RGBA cursor, disposing only any temporary resized bitmap.</summary>
    /// <returns>True when uploaded or intentionally suppressed by a hidden window; false on conversion/load failure.</returns>
    /// <remarks>Failures are logged and restore the default cursor.</remarks>
    internal bool Load(string code, int hotX, int hotY, BitmapRef bitmapRef)
    {
        if (window.KeepsHidden) return true;
        try
        {
            SKBitmap source = ((BitmapExternal)bitmapRef).bmp;
            float scale = guiScale();
            SKBitmap? bitmap = scale == 1f ? source : source.Resize(
                new SKImageInfo(Math.Max(1, (int)(source.Width * scale)),
                    Math.Max(1, (int)(source.Height * scale))),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (bitmap is null) return false;
            try
            {
                byte[] rgba = SdlBitmapPixels.ToRgba(bitmap);
                window.LoadCursor(code, Math.Clamp((int)(hotX * scale), 0, bitmap.Width - 1),
                    Math.Clamp((int)(hotY * scale), 0, bitmap.Height - 1), bitmap.Width, bitmap.Height, rgba);
            }
            finally { if (!ReferenceEquals(bitmap, source)) bitmap.Dispose(); }
            return true;
        }
        catch (Exception error)
        {
            platform.Logger.Error("Failed loading SDL mouse cursor {0}: {1}", code, error);
            Restore();
            return false;
        }
    }

    /// <summary>Selects a loaded cursor and updates game state; null restores the default, and failures are logged.</summary>
    internal void Use(string? code, bool forceUpdate)
    {
        if (window.KeepsHidden) return;
        if (code == platform.CurrentMouseCursor && !forceUpdate) return;
        try
        {
            if (code is null) Restore();
            else bindings.SetCurrentCursor(window.UseCursor(code) ? code : null);
        }
        catch (Exception error)
        {
            platform.Logger.Error("Failed selecting SDL mouse cursor {0}: {1}", code, error);
            Restore();
        }
    }

    /// <summary>Restores the SDL default cursor and clears the game's custom cursor name unless the window stays hidden.</summary>
    internal void Restore()
    {
        if (window.KeepsHidden) return;
        window.RestoreCursor();
        bindings.SetCurrentCursor(null);
    }
}
