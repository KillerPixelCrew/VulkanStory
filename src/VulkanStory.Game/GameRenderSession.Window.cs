using SkiaSharp;
using Vintagestory.API.Config;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    // Migrated from VulkanClientPlatform.SdlInput.cs at donor baseline
    // 386e0d05386d0b228b439d09aeca851428f7bbf3. Keep asset lookup and decoding
    // in the game integration; the SDL host receives only RGBA pixels.
    private void SetWindowIcon()
    {
        if (window is null || window.KeepsHidden) return;
        string? assets = GamePaths.AssetsPath;
        if (string.IsNullOrEmpty(assets)) return;
        string path = Path.Combine(assets, "gameicon.png");
        if (!File.Exists(path)) return;
        try
        {
            using SKBitmap? bitmap = SKBitmap.Decode(path);
            if (bitmap is null) return;
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
            window.SetIcon(bitmap.Width, bitmap.Height, rgba);
        }
        catch (Exception error)
        {
            platform.Logger.Warning("SDL window icon unavailable: {0}", error.Message);
        }
    }
}
