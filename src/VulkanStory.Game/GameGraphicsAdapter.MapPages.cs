using System.Runtime.InteropServices;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal int CreateMapPageArray(int size, int layers)
    {
        var renderer = RequireDevice();
        int texture = renderer.CreateTexture2DArray(size, size, layers, TextureInternalFormat.Rgba8, TexturePixelFormat.Rgba);
        SetupTextureSampler(texture, 9728, 33071);
        renderer.SetTextureParameter(texture, GameGlTextureTokens.TextureMinFilter, 9729);
        return texture;
    }
    internal void UploadMapPage(int texture, int layer, int size, int[] pixels)
    {
        var renderer = RequireDevice();
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { renderer.UploadTexture2DArrayLayer(texture, layer, size, size, pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }
}
