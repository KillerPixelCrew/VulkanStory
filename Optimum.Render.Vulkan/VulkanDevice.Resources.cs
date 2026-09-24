using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    // -------------------------------------------------------------------- textures

    public int CreateTexture2D(
        int width, int height, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels, bool generateMipmaps)
    {
        int id = _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), generateMipmaps: generateMipmaps);
        RecordGlInternalFormat(id, (int)internalFormat);

        if (pixels != IntPtr.Zero)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, BytesPerPixel(internalFormat));
            if (generateMipmaps) _textures.GenerateMipmaps(id);
        }
        return id;
    }

    public int CreateTexture2DRaw(int width, int height, int glInternalFormat, IntPtr pixels, int bytesPerPixel,
        bool generateMipmaps = false)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)width, (uint)height, format, generateMipmaps: generateMipmaps);
        RecordGlInternalFormat(id, glInternalFormat);

        if (pixels != IntPtr.Zero && bytesPerPixel > 0)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, bytesPerPixel);

            // A chain that was asked for has to be filled here. GL's texture is
            // complete the moment glGenerateMipmap runs, but an image created
            // with levels and never blitted into keeps whatever its memory held,
            // and every sample above level 0 reads that - which looks like other
            // textures bleeding onto a surface as it turns away from the camera.
            if (generateMipmaps) _textures.GenerateMipmaps(id);
        }
        RenderTrace.TextureCreated(id, width, height, format, pixels, bytesPerPixel);
        return id;
    }

    /// <summary>
    /// A post-chain colour texture (framebuffer slots in
    /// <see cref="Graph.TransientAllocator.PostChainSlots" />): created in the Transient
    /// memory pool class and registered with the transient allocator. Until the frame
    /// graph binds it (<see cref="BindTransientForFrame" />) it behaves like any texture.
    /// </summary>
    public int CreateTransientTexture2D(int width, int height, EnumTextureInternalFormat internalFormat,
        int framebufferSlot)
    {
        int id = _textures.Create((uint)width, (uint)height, GlEnums.TextureFormatFrom(internalFormat),
            poolClass: MemoryPoolClass.Transient);
        RecordGlInternalFormat(id, (int)internalFormat);
        _transients.OptIn(id, framebufferSlot);
        return id;
    }

    /// <summary>Registers (or re-tags) a texture as the transient of client framebuffer slot <paramref name="framebufferSlot" />.</summary>
    public void OptInTransient(int textureId, int framebufferSlot) => _transients.OptIn(textureId, framebufferSlot);

    /// <summary><see cref="CreateTransientTexture2D" /> with a raw GL internal format token, no pixels.</summary>
    public int CreateTransientTexture2DRaw(int width, int height, int glInternalFormat, int framebufferSlot)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)width, (uint)height, format, poolClass: MemoryPoolClass.Transient);
        RecordGlInternalFormat(id, glInternalFormat);
        RenderTrace.TextureCreated(id, width, height, format, IntPtr.Zero, 0);
        _transients.OptIn(id, framebufferSlot);
        return id;
    }

    /// <summary>
    /// Serves a texture for passes [<paramref name="firstPass" />, <paramref name="lastPass" />]
    /// of the current frame through the transient allocator and returns the texture id
    /// that backs it (itself unless aliasing is on). Call after BeginFrame, in pass order.
    /// </summary>
    public int BindTransientForFrame(int textureId, int firstPass, int lastPass) =>
        _transients.Bind(textureId, firstPass, lastPass);

    /// <summary>The transient allocator the frame graph acquires physical images from.</summary>
    internal Graph.TransientAllocator Transients => _transients;

    /// <summary>The ReadSelf copy pool. Tests only.</summary>
    internal Graph.FeedbackCopyPool ReadSelfCopiesForTests => _readSelfCopies;

    /// <summary>Forces transient aliasing on or off before Initialize (default: <c>OPTIMUM_VULKAN_ALIAS</c>).</summary>
    internal bool? TransientAliasingOverride { get; set; }

    private int CreateReadSelfCopy(Graph.FeedbackCopyDesc desc) =>
        _textures.Create(desc.Width, desc.Height, desc.Format, layers: desc.Layers, cube: desc.Cube,
            generateMipmaps: desc.MipLevels > 1, poolClass: MemoryPoolClass.Transient);

    /// <summary>Gives the previous draw's ReadSelf copies back to the pool.</summary>
    private void ReleaseReadSelfCopies()
    {
        if (_sampledTextureOverrides.Count == 0) return;
        foreach (int copy in _sampledTextureOverrides.Values) _readSelfCopies.Release(copy);
        _sampledTextureOverrides.Clear();
    }

    public int CreateTextureCubeRaw(int size, int glInternalFormat, IntPtr[] facePixels, int bytesPerPixel)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)size, (uint)size, format, cube: true);

        for (uint face = 0; face < 6 && face < facePixels.Length; face++)
        {
            if (facePixels[face] == IntPtr.Zero) continue;
            _textures.Upload(id, 0, 0, 0, (uint)size, (uint)size,
                facePixels[face], bytesPerPixel, face);
        }
        return id;
    }

    public int CreateTextureCube(
        int size, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr[] facePixels)
    {
        Format format = GlEnums.TextureFormatFrom(internalFormat);
        int id = _textures.Create((uint)size, (uint)size, format, cube: true);

        for (uint face = 0; face < 6 && face < facePixels.Length; face++)
        {
            if (facePixels[face] == IntPtr.Zero) continue;
            _textures.Upload(id, 0, 0, 0, (uint)size, (uint)size,
                facePixels[face], BytesPerPixel(internalFormat), face);
        }
        return id;
    }

    public int CreateTexture2DArray(
        int width, int height, int layers,
        EnumTextureInternalFormat internalFormat, EnumTexturePixelFormat pixelFormat) =>
        _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), layers: (uint)layers);

    public void UploadTexture2D(
        int textureId, int level, int x, int y, int width, int height,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels)
    {
        FlushPendingClears(textureId);
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels,
            pixelFormat == EnumTexturePixelFormat.Red ? 1 : 4);
    }

    public void UploadTexture2DRaw(
        int textureId, int level, int x, int y, int width, int height, IntPtr pixels, int bytesPerPixel)
    {
        if (bytesPerPixel <= 0) return;
        FlushPendingClears(textureId);
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels, bytesPerPixel);
    }

    public void GenerateMipmaps(int textureId)
    {
        FlushPendingClears(textureId);
        _textures.GenerateMipmaps(textureId);
    }

    public void DeleteTexture(int textureId) => ReleaseTexture(textureId);

    /// <summary>
    /// Deletes a texture and evicts every descriptor set that names it.
    ///
    /// The eviction is the important half. The texture itself is destroyed once
    /// the Frame timeline passed every frame that could name it, but a cached set would outlive it and, once the driver
    /// reused the view handle for a new texture, be served to draws of that new
    /// texture - which is a GPU read of freed memory. The GUI re-renders its text
    /// into fresh textures constantly, so this was the loading-screen crash.
    /// </summary>
    private void ReleaseTexture(int textureId)
    {
        if (_sampledTextureOverrides.Remove(textureId, out int copy)) _readSelfCopies?.Release(copy);
        _transients?.Forget(textureId);
        _textures.RestoreBinding(textureId);
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null)
        {
            _descriptors.Release(texture.Id);
            _targets.DropPendingClears(texture);
        }
        _textures.Delete(textureId, _frames);
        // Only a delete that found something is a delete. Deleting an id twice
        // (framebuffers share a depth texture) otherwise inflated the counter
        // past the number of textures that ever existed.
        if (texture != null) VulkanStats.NoteTextureDeleted();
    }

    public void SetTextureParameter(int textureId, int parameterName, int value) =>
        _textures.SetParameter(textureId, parameterName, value);

    public void SetTextureParameter(int textureId, int parameterName, float value) =>
        _textures.SetParameter(textureId, parameterName, value);

    public void SetTextureBorderColor(int textureId, float r, float g, float b, float a) =>
        _textures.SetBorderColor(textureId, r, g, b, a);

    public int GetTextureParameter(int textureId, int parameterName)
    {
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture == null) return 0;

        return parameterName == GlEnums.TextureCompareMode
            ? texture.State.CompareEnable ? GlEnums.TextureCompareRefToTexture : GlEnums.TextureCompareModeNone
            : 0;
    }

    public void UploadTexture2DArrayLayer(int textureId, int layer, int x, int y,
        int width, int height, IntPtr pixels)
    {
        FlushPendingClears(textureId);
        _textures.Upload(textureId, 0, x, y, (uint)width, (uint)height, pixels, 4, (uint)layer);
    }

    public void UploadTexture2DNormalizedShorts(int textureId, int level, int x, int y,
        int width, int height, short[] pixels)
    {
        FlushPendingClears(textureId);
        _textures.UploadNormalizedShorts(textureId, level, x, y, width, height, pixels);
    }

    private readonly Dictionary<int, SamplerState> _standaloneSamplers = new();
    private int _nextSamplerId = 1;

    public int CreateSampler(bool linear)
    {
        int id = _nextSamplerId++;
        _standaloneSamplers[id] = SamplerState.Default with
        {
            MagFilter = linear ? Filter.Linear : Filter.Nearest,
            // GenSampler uses GL_NEAREST_MIPMAP_LINEAR for both variants;
            // the flag changes magnification only. Terrain relies on this
            // override retaining the atlas mip chain at a distance.
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Linear,
            Mipmapped = true,
        };
        return id;
    }

    public void SetSamplerParameter(int samplerId, int parameterName, float value)
    {
        if (!_standaloneSamplers.TryGetValue(samplerId, out SamplerState state)) return;

        _standaloneSamplers[samplerId] = parameterName == GlEnums.TextureLodBias
            ? state with { LodBias = value }
            : state;
    }

    public void DeleteSampler(int samplerId) => _standaloneSamplers.Remove(samplerId);

    private static int BytesPerPixel(EnumTextureInternalFormat format) => format switch
    {
        EnumTextureInternalFormat.Rgba8 => 4,
        EnumTextureInternalFormat.Rgba16f => 8,
        EnumTextureInternalFormat.R16f => 2,
        EnumTextureInternalFormat.DepthComponent32 => 4,
        _ => 4,
    };

    // ---------------------------------------------------------------- framebuffers

    public int CreateFramebuffer(int width, int height) => _targets.Create((uint)width, (uint)height);

    public void AttachTexture(int framebufferId, EnumFramebufferAttachment attachment, int textureId, int layer)
    {
        int index = attachment == EnumFramebufferAttachment.DepthAttachment
            ? -1
            : (int)attachment - (int)EnumFramebufferAttachment.ColorAttachment0;

        _targets.Attach(framebufferId, index, textureId, (uint)layer);
    }

    public bool CheckFramebufferComplete(int framebufferId, out string status)
    {
        // Dynamic rendering has no framebuffer object to validate, so
        // completeness reduces to having a target with attachments.
        VulkanFramebuffer? framebuffer = _targets.Get(framebufferId);
        if (framebuffer == null)
        {
            status = "no such framebuffer";
            return false;
        }

        status = "complete";
        return true;
    }

    public void DeleteFramebuffer(int framebufferId)
    {
        _targets.Delete(framebufferId);
        FramebufferDeleted?.Invoke(framebufferId);
    }

    /// <summary>The platform whose graphics this device is; null for a bare device (the GPU tests).</summary>
    internal Platform.VulkanClientPlatform? OwnerPlatform { get; set; }

    /// <summary>
    /// Raised after a framebuffer is deleted. Its id is reused by the next one created, so a
    /// record keyed on it (the stated draw buffers) has to forget it here.
    /// </summary>
    internal Action<int>? FramebufferDeleted;

    /// <summary>Whether the frame graph records this device's frames. Change only between frames.</summary>
    internal bool FrameGraphEnabled
    {
        get => _graph.Enabled;
        set => _graph.Enabled = value;
    }

    /// <summary>The frame graph's totals. Tests only.</summary>
    internal Graph.FrameGraph FrameGraphForTests => _graph;

    /// <summary>The bound render target's id (0 before any bind).</summary>
    internal int BoundFramebufferId => _targets.Bound?.Id ?? 0;

    /// <summary>The render target standing for the default framebuffer (0 when headless).</summary>
    internal int DefaultFramebufferId => _defaultFramebuffer;

    /// <summary>
    /// Ends the pass a render stage left open (closes its scope): a native draw recorded with
    /// keepScope stays in the stage's declaration until here. No-op with the frame graph off.
    /// </summary>
    internal void EndStagePass()
    {
        if (_frameActive) _targets.EndPass(Commands);
    }

    /// <summary>Lands the clears promoted into a texture before it is written some other way.</summary>
    private void FlushPendingClears(int textureId)
    {
        if (!_frameActive || !_graph.HasPendingClears) return;
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null) _targets.FlushPendingClears(Commands, texture);
    }

    /// <summary>
    /// A colour clear of one attachment of an explicit target, outside every native pass: the
    /// promoted LOAD_OP_CLEAR of the next pass on it, or an attachment clear inside an open scope.
    /// The caller has applied the draw buffers and colour mask it stated (VulkanClientPlatform).
    /// </summary>
    internal void ClearNativeColor(int framebufferId, int attachment, float r, float g, float b, float a)
    {
        if (!BindForNativeClear(framebufferId)) return;
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("clearColor attachment=" + attachment + " target=" + _targets.Bound!.Id +
                " rgba=" + r + "," + g + "," + b + "," + a);
        }
        _targets.ClearColor(Commands, attachment, r, g, b, a);
    }

    /// <summary>The depth clear of an explicit target; the caller has applied the stated depth mask.</summary>
    internal void ClearNativeDepth(int framebufferId, float depth)
    {
        if (!BindForNativeClear(framebufferId)) return;
        if (RenderTrace.Enabled) RenderTrace.Write("clearDepth target=" + _targets.Bound!.Id + " depth=" + depth);
        _targets.ClearDepth(Commands, depth);
    }

    private bool BindForNativeClear(int framebufferId)
    {
        EndNativePass();
        if (!_frameActive) return false;
        int id = ResolveNativeFramebuffer(framebufferId);
        VulkanFramebuffer? target = _targets.Get(id);
        if (target == null) return false;
        if (!ReferenceEquals(_targets.Bound, target)) _targets.Bind(Commands, id);
        return true;
    }
}
