using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Game.Input;

// Retained bitmap conversion and cursor ownership from VulkanClientPlatform.SdlInput.
internal sealed class GameCursorController(ClientPlatformWindows platform,
    GamePlatformBindings bindings, SdlWindowHost window, System.Func<float> guiScale)
{
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

    internal void Restore()
    {
        if (window.KeepsHidden) return;
        window.RestoreCursor();
        bindings.SetCurrentCursor(null);
    }
}
