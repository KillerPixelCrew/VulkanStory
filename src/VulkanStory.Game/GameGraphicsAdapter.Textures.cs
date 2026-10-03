using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.ClientNative;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Retained bitmap/atlas bodies from VulkanClientPlatform.Textures.cs.
internal sealed partial class GameGraphicsAdapter
{
    // Retained VulkanClientPlatform.Leaf.cs RGBA pointer upload; baseline
    // 386e0d05386d0b228b439d09aeca851428f7bbf3. Used by the original SVG rasterizer.
    internal int LoadTextureFromRgbaPointer(int width, int height, IntPtr pixels)
    {
        var renderer = RequireDevice();
        int textureId = renderer.CreateTexture2DRaw(width, height, GameGlTextureTokens.Rgba8, pixels, 4);
        renderer.SetTextureParameter(textureId, GameGlTextureTokens.TextureMinFilter, 9729);
        renderer.SetTextureParameter(textureId, GameGlTextureTokens.TextureMagFilter, 9729);
        return textureId;
    }
    internal unsafe void LoadIntoTexture(IBitmap srcBmp, int targetTextureId, int destX, int destY, bool generateMipmaps = false)
    {
        RequireDevice();
        if (srcBmp is BitmapExternal retainedExternal)
        {
            RequireDevice().UploadTexture2D(targetTextureId, 0, destX, destY,
                srcBmp.Width, srcBmp.Height, VulkanStory.Contracts.TexturePixelFormat.Rgba,
                (IntPtr)retainedExternal.PixelsPtrAndLock);
        }
        else
        {
            // A managed pixel array has to be pinned before the device can
            // read it; the GL body relied on the overload doing that.
            GCHandle retainedPin = GCHandle.Alloc(srcBmp.Pixels, GCHandleType.Pinned);
            try
            {
                RequireDevice().UploadTexture2D(targetTextureId, 0, destX, destY,
                    srcBmp.Width, srcBmp.Height, VulkanStory.Contracts.TexturePixelFormat.Rgba,
                    retainedPin.AddrOfPinnedObject());
            }
            finally
            {
                retainedPin.Free();
            }
        }
        if (mipmapsEnabled() && generateMipmaps)
        {
            BuildMipMaps(targetTextureId);
        }
    }

    /// <summary>
    /// The GL body uploads BGRA bytes into a GL_RGBA image; the device gets a BGRA-ordered
    /// image instead, which samples the same way without a per-pixel swizzle. Anisotropy is
    /// a sampler property the device sets from its own limit, so there is nothing to query.
    /// </summary>
    internal unsafe int LoadTexture(IBitmap bmp, bool linearMag = false, int clampMode = 0, bool generateMipmaps = false)
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException("Graphics routing requires the session owner thread.");
        }
        int retainedTextureId;
        if (bmp is BitmapExternal retainedExternal)
        {
            retainedTextureId = RequireDevice().CreateTexture2DRaw(bmp.Width, bmp.Height,
                GameGlTextureTokens.Bgra, (IntPtr)retainedExternal.PixelsPtrAndLock, 4,
                mipmapsEnabled() && generateMipmaps);
        }
        else
        {
            GCHandle retainedPin = GCHandle.Alloc(bmp.Pixels, GCHandleType.Pinned);
            try
            {
                retainedTextureId = RequireDevice().CreateTexture2DRaw(bmp.Width, bmp.Height,
                    GameGlTextureTokens.Bgra, retainedPin.AddrOfPinnedObject(), 4,
                    mipmapsEnabled() && generateMipmaps);
            }
            finally
            {
                retainedPin.Free();
            }
        }
        switch (clampMode)
        {
        case 1:
            RequireDevice().SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureWrapS, 33071);
            RequireDevice().SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureWrapT, 33071);
            break;
        case 2:
            RequireDevice().SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureWrapS, 10497);
            RequireDevice().SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureWrapT, 10497);
            break;
        }
        RequireDevice().SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureMinFilter, 9729);
        RequireDevice().SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureMagFilter, linearMag ? 9729 : 9728);
        if (mipmapsEnabled() && generateMipmaps)
        {
            BuildMipMaps(retainedTextureId);
        }
        return retainedTextureId;
    }

    internal void LoadOrUpdateTextureFromBgra_DeferMipMap(int[] rgbaPixels, bool linearMag, int clampMode, ref LoadedTexture intoTexture)
    {
        LoadOrUpdateTextureFromPixels(rgbaPixels, linearMag, clampMode, ref intoTexture, bgra: true, makeMipMap: false);
    }

    internal void LoadOrUpdateTextureFromBgra(int[] rgbaPixels, bool linearMag, int clampMode, ref LoadedTexture intoTexture)
    {
        LoadOrUpdateTextureFromPixels(rgbaPixels, linearMag, clampMode, ref intoTexture, bgra: true, makeMipMap: true);
    }

    internal void LoadOrUpdateTextureFromRgba(int[] rgbaPixels, bool linearMag, int clampMode, ref LoadedTexture intoTexture)
    {
        LoadOrUpdateTextureFromPixels(rgbaPixels, linearMag, clampMode, ref intoTexture, bgra: false, makeMipMap: true);
    }

    /// <summary>
    /// The texture atlas upload path: TextureAtlas.Upload reaches it through
    /// LoadOrUpdateTextureFromBgra_DeferMipMap, which is why it only runs once a world
    /// starts loading and never on the menu. The pixels are BGRA when <paramref name="bgra" />
    /// says so (the GL body's PixelFormat 32993) and RGBA otherwise, matching the wrappers.
    /// </summary>
    private void LoadOrUpdateTextureFromPixels(int[] rgbaPixels, bool linearMag, int clampMode, ref LoadedTexture intoTexture, bool bgra, bool makeMipMap)
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException("Graphics routing requires the session owner thread.");
        }
        int retainedGlFormat = bgra
            ? GameGlTextureTokens.Bgra
            : GameGlTextureTokens.Rgba8;

        GCHandle retainedPin = GCHandle.Alloc(rgbaPixels, GCHandleType.Pinned);
        try
        {
            if (intoTexture.TextureId == 0 || intoTexture.Width * intoTexture.Height != rgbaPixels.Length)
            {
                if (intoTexture.TextureId != 0)
                {
                    RequireDevice().DeleteTexture(intoTexture.TextureId);
                }
                // The mip chain has to be requested at creation; asking for
                // mipmaps afterwards on a one-level image does nothing. GL
                // can grow one at any time, which is what the deferred
                // variant relies on: it uploads with makeMipMap false and
                // the atlas manager calls BuildMipMaps later, in StageB. So
                // the chain is sized whenever mipmapping is on at all, and
                // makeMipMap only decides whether to fill it here.
                intoTexture.TextureId = RequireDevice().CreateTexture2DRaw(
                    intoTexture.Width, intoTexture.Height, retainedGlFormat,
                    retainedPin.AddrOfPinnedObject(), 4, mipmapsEnabled());

                if (clampMode == 1)
                {
                    RequireDevice().SetTextureParameter(intoTexture.TextureId,
                        GameGlTextureTokens.TextureWrapS, 33071);
                    RequireDevice().SetTextureParameter(intoTexture.TextureId,
                        GameGlTextureTokens.TextureWrapT, 33071);
                }
                RequireDevice().SetTextureParameter(intoTexture.TextureId,
                    GameGlTextureTokens.TextureMinFilter, 9729);
                RequireDevice().SetTextureParameter(intoTexture.TextureId,
                    GameGlTextureTokens.TextureMagFilter, linearMag ? 9729 : 9728);

                if (makeMipMap)
                {
                    BuildMipMaps(intoTexture.TextureId);
                }
            }
            else
            {
                RequireDevice().UploadTexture2D(intoTexture.TextureId, 0, 0, 0,
                    intoTexture.Width, intoTexture.Height,
                    VulkanStory.Contracts.TexturePixelFormat.Rgba, retainedPin.AddrOfPinnedObject());
            }
        }
        finally
        {
            retainedPin.Free();
        }
    }

}
