using SkiaSharp;
using Vintagestory.API.Config;
using VulkanStory.Game.Input;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    // Migrated from VulkanClientPlatform.SdlInput.cs at donor baseline
    // 386e0d05386d0b228b439d09aeca851428f7bbf3. Keep asset lookup and decoding
    // in the game integration; the SDL host receives only RGBA pixels.
    /// <summary>Loads the game icon into the owned SDL window when the original assets are available.</summary>
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
            window.SetIcon(bitmap.Width, bitmap.Height, SdlBitmapPixels.ToRgba(bitmap));
        }
        catch (Exception error)
        {
            platform.Logger.Warning("SDL window icon unavailable: {0}", error.Message);
        }
    }
}
