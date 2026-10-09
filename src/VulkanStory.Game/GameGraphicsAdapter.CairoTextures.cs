using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.ClientNative;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Retained Cairo and six-face cube uploads from VulkanClientPlatform.Textures.cs.
internal sealed partial class GameGraphicsAdapter
{
    /// <summary>Uploads a Cairo image surface as a backend texture using its retained BGRA pixel layout.</summary>
    /// <param name="surface">Borrowed Cairo surface; uploaded synchronously without taking surface ownership.</param>
    /// <param name="linearMag">Whether the original texture requests linear magnification.</param>
    /// <returns>Owned backend texture identifier.</returns>
    internal int LoadCairoTexture(ImageSurface surface, bool linearMag)
    {
        var renderer = RequireDevice();
        int retainedTextureId = renderer.CreateTexture2DRaw(surface.Width, surface.Height,
            GameGlTextureTokens.Bgra, surface.DataPtr, 4);
        renderer.SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureMinFilter, 9729);
        renderer.SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureMagFilter, linearMag ? 9729 : 9728);
        return retainedTextureId;
    }


    internal void LoadOrUpdateCairoTexture(ImageSurface surface, bool linearMag, ref LoadedTexture intoTexture)
    {
        var renderer = RequireDevice();
        if (intoTexture.TextureId == 0 || intoTexture.Width != surface.Width || intoTexture.Height != surface.Height)
        {
            if (intoTexture.TextureId != 0)
            {
                renderer.DeleteTexture(intoTexture.TextureId);
            }
            intoTexture.TextureId = renderer.CreateTexture2DRaw(surface.Width, surface.Height,
                GameGlTextureTokens.Bgra, surface.DataPtr, 4);
            intoTexture.Width = surface.Width;
            intoTexture.Height = surface.Height;
            renderer.SetTextureParameter(intoTexture.TextureId, GameGlTextureTokens.TextureMinFilter, 9729);
            renderer.SetTextureParameter(intoTexture.TextureId, GameGlTextureTokens.TextureMagFilter, linearMag ? 9729 : 9728);
        }
        else
        {
            // The image is BGRA-ordered; the upload is a byte copy at four
            // bytes per pixel, which is what Rgba selects here.
            renderer.UploadTexture2D(intoTexture.TextureId, 0, 0, 0,
                surface.Width, surface.Height, VulkanStory.Contracts.TexturePixelFormat.Rgba, surface.DataPtr);
        }
        CheckGraphicsError("LoadOrUpdateCairoTexture");
    }


    internal unsafe int Load3DTextureCube(BitmapRef[] bmps)
    {
        var renderer = RequireDevice();
        IntPtr[] retainedFaces = new IntPtr[6];
        int retainedSize = 0;
        for (int k = 0; k < 6; k++)
        {
            BitmapExternal retainedFace = (BitmapExternal)bmps[k];
            retainedSize = retainedFace.Width;
            retainedFaces[k] = (IntPtr)retainedFace.PixelsPtrAndLock;
        }
        // BGRA like the other bitmap uploads, so the raw overload rather than
        // the EnumTextureInternalFormat one.
        int retainedCubeId = renderer.CreateTextureCubeRaw(retainedSize,
            GameGlTextureTokens.Bgra, retainedFaces, 4);
        SetupTextureSampler(retainedCubeId, 9729, 33071);
        return retainedCubeId;
    }

}
