using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.ClientNative;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Retained Cairo and six-face cube uploads from VulkanClientPlatform.Textures.cs.
internal sealed partial class GameGraphicsAdapter
{
    internal int LoadCairoTexture(ImageSurface surface, bool linearMag)
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException("Graphics routing requires the session owner thread.");
        }
        int retainedTextureId = RequireDevice().CreateTexture2DRaw(surface.Width, surface.Height,
            GameGlTextureTokens.Bgra, surface.DataPtr, 4);
        RequireDevice().SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureMinFilter, 9729);
        RequireDevice().SetTextureParameter(retainedTextureId, GameGlTextureTokens.TextureMagFilter, linearMag ? 9729 : 9728);
        return retainedTextureId;
    }


    internal void LoadOrUpdateCairoTexture(ImageSurface surface, bool linearMag, ref LoadedTexture intoTexture)
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException("Graphics routing requires the session owner thread.");
        }
        if (intoTexture.TextureId == 0 || intoTexture.Width != surface.Width || intoTexture.Height != surface.Height)
        {
            if (intoTexture.TextureId != 0)
            {
                RequireDevice().DeleteTexture(intoTexture.TextureId);
            }
            intoTexture.TextureId = RequireDevice().CreateTexture2DRaw(surface.Width, surface.Height,
                GameGlTextureTokens.Bgra, surface.DataPtr, 4);
            intoTexture.Width = surface.Width;
            intoTexture.Height = surface.Height;
            RequireDevice().SetTextureParameter(intoTexture.TextureId, GameGlTextureTokens.TextureMinFilter, 9729);
            RequireDevice().SetTextureParameter(intoTexture.TextureId, GameGlTextureTokens.TextureMagFilter, linearMag ? 9729 : 9728);
        }
        else
        {
            // The image is BGRA-ordered; the upload is a byte copy at four
            // bytes per pixel, which is what Rgba selects here.
            RequireDevice().UploadTexture2D(intoTexture.TextureId, 0, 0, 0,
                surface.Width, surface.Height, VulkanStory.Contracts.TexturePixelFormat.Rgba, surface.DataPtr);
        }
        CheckGraphicsError("LoadOrUpdateCairoTexture");
    }


    internal unsafe int Load3DTextureCube(BitmapRef[] bmps)
    {
        RequireDevice();
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
        int retainedCubeId = RequireDevice().CreateTextureCubeRaw(retainedSize,
            GameGlTextureTokens.Bgra, retainedFaces, 4);
        RequireDevice().SetTextureParameter(retainedCubeId, GameGlTextureTokens.TextureMinFilter, 9729);
        RequireDevice().SetTextureParameter(retainedCubeId, GameGlTextureTokens.TextureMagFilter, 9729);
        RequireDevice().SetTextureParameter(retainedCubeId, GameGlTextureTokens.TextureWrapS, 33071);
        RequireDevice().SetTextureParameter(retainedCubeId, GameGlTextureTokens.TextureWrapT, 33071);
        return retainedCubeId;
    }

}
