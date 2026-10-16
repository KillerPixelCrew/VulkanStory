using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private sealed class FramebufferOwner(GameGraphicsAdapter adapter, int handle)
    {
        internal readonly GameGraphicsAdapter Adapter = adapter;
        internal readonly int Handle = handle;
        internal bool Released;
    }
    private static readonly ConditionalWeakTable<FrameBufferRef, FramebufferOwner> FramebufferOwners = new();
    private readonly Dictionary<int, FrameBufferRef> framebufferViews = new();
    private FrameBufferRef? currentFramebuffer;
    internal int ReadFramebufferId { get; set; } = PassDeclaration.DefaultFramebuffer;

    private FrameBufferRef RegisterFramebuffer(FrameBufferRef target)
    {
        // Original shadow placeholders bind name zero and own no native target.
        int handle = target.FboId == 0 ? PassDeclaration.DefaultFramebuffer : target.FboId;
        FramebufferOwners.Add(target, new FramebufferOwner(this, handle));
        if (handle > 0) framebufferViews.Add(handle, target);
        return target;
    }

    /// <summary>Creates a Vulkan framebuffer from original attachment descriptors and associates the returned game reference with this adapter.</summary>
    /// <param name="attributes">Original game attachment/size descriptor.</param>
    /// <returns>Original game reference with an adapter-owned framebuffer association.</returns>
    internal FrameBufferRef CreateFramebuffer(FramebufferAttrs attributes)
    {
        var renderer = RequireDevice();
        var target = new FrameBufferRef
        {
            Width = attributes.Width, Height = attributes.Height,
            FboId = renderer.CreateFramebuffer(attributes.Width, attributes.Height),
        };
        var colors = new List<int>();
        int mask = 0;
        try
        {
        foreach (FramebufferAttrsAttachment attachment in attributes.Attachments)
        {
            RawTexture texture = attachment.Texture;
            if (texture.TextureId == 0) GenTexture(texture);
            renderer.AttachTexture(target.FboId, GameTextureDefinitions.Attachment(attachment.AttachmentType), texture.TextureId, 0);
            if (attachment.AttachmentType == EnumFramebufferAttachment.DepthAttachment)
                target.DepthTextureId = texture.TextureId;
            else
            {
                colors.Add(texture.TextureId);
                mask |= 1 << ((int)attachment.AttachmentType - (int)EnumFramebufferAttachment.ColorAttachment0);
            }
        }
        target.ColorTextureIds = colors.ToArray();
        Stated.SetDrawBuffers(target.FboId, (uint)mask);
        if (!renderer.CheckFramebufferComplete(target.FboId, out string status))
            throw new Exception("FBO " + attributes.Name + ": " + status);
        return RegisterFramebuffer(target);
        }
        catch
        {
            Stated.ForgetFramebuffer(target.FboId);
            renderer.DeleteFramebuffer(target.FboId);
            throw;
        }
    }

    private FramebufferOwner FramebufferOwnership(FrameBufferRef target)
    {
        if (!FramebufferOwners.TryGetValue(target, out var owner) || !ReferenceEquals(owner.Adapter, this))
            throw new InvalidOperationException("Active framebuffer routing has no renderer owner.");
        return owner;
    }

    private int LiveFramebufferHandle(FrameBufferRef target)
    {
        var owner = FramebufferOwnership(target);
        if (owner.Released || target.Disposed) throw new ObjectDisposedException(nameof(FrameBufferRef));
        return owner.Handle;
    }

    private int CurrentFramebufferHandle()
    {
        RequireDevice();
        int handle = currentFramebuffer is null ? PassDeclaration.DefaultFramebuffer : LiveFramebufferHandle(currentFramebuffer);
        if (handle != CurrentTargetId) throw new InvalidOperationException("Current framebuffer routing state is inconsistent.");
        return handle;
    }

    /// <summary>Binds an owned game framebuffer and optionally preserves the retained viewport.</summary>
    /// <param name="target">Owned target or null for default framebuffer.</param>
    /// <param name="keepViewport">True to leave the current viewport unchanged.</param>
    internal void SetFramebuffer(FrameBufferRef? target, bool keepViewport)
    {
        var renderer = RequireDevice();
        int handle = PassDeclaration.DefaultFramebuffer;
        if (target != null)
            handle = LiveFramebufferHandle(target);
        renderer.EndStagePass();
        currentFramebuffer = target;
        CurrentTargetId = handle;
        ReadFramebufferId = handle;
        GameFramebufferBindings.Set(platform!, target);
        // Original null binding retains the viewport. The private setter always retains it.
        if (target != null && !keepViewport)
            Stated.Viewport = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)target.Width, (uint)target.Height));
    }

    internal int CreateManagedFramebuffer()
    {
        int handle = RequireDevice().CreateFramebuffer(1, 1);
        RegisterFramebuffer(new FrameBufferRef { FboId = handle, Width = 1, Height = 1, ColorTextureIds = [] });
        return handle;
    }

    internal void BindManagedFramebuffer(int handle)
    {
        if (handle <= 0) { SetFramebuffer(null, keepViewport: true); return; }
        if (!framebufferViews.TryGetValue(handle, out FrameBufferRef? view))
            throw new InvalidOperationException("The shared framebuffer name has no game adapter view.");
        SetFramebuffer(view, keepViewport: true);
    }

    internal void RefreshManagedFramebuffer(int handle)
    {
        var renderer = RequireDevice();
        if (!framebufferViews.TryGetValue(handle, out FrameBufferRef? view)) return;
        (view.Width, view.Height) = renderer.FramebufferExtent(handle);
        int count = 0;
        var colors = new int[StatedRenderState.MaxColorAttachments];
        for (int slot = 0; slot < colors.Length; slot++)
            if (renderer.TryFramebufferAttachment(handle, 0x8CE0 + slot, out int texture, out _, out _))
            { colors[slot] = texture; if (texture != 0) count = slot + 1; }
        view.ColorTextureIds = colors[..count];
        if (renderer.TryFramebufferAttachment(handle, 0x8D00, out int depth, out _, out _)) view.DepthTextureId = depth;
    }

    private void ForgetFramebufferView(int handle)
    {
        Stated.ForgetFramebuffer(handle);
        if (ReadFramebufferId == handle) ReadFramebufferId = PassDeclaration.DefaultFramebuffer;
        if (!framebufferViews.Remove(handle, out FrameBufferRef? view)) return;
        if (FramebufferOwners.TryGetValue(view, out FramebufferOwner? owner)) owner.Released = true;
        view.Disposed = true;
        if (ReferenceEquals(currentFramebuffer, view))
        {
            currentFramebuffer = null;
            CurrentTargetId = PassDeclaration.DefaultFramebuffer;
            if (platform is not null) GameFramebufferBindings.Set(platform, null);
        }
    }

    /// <summary>Releases an owned framebuffer and optionally its owned attachment textures.</summary>
    /// <param name="target">Owned game reference; null is accepted.</param>
    /// <param name="disposeTextures">Whether attachment textures should also be deleted.</param>
    internal void DisposeFramebuffer(FrameBufferRef? target, bool disposeTextures)
    {
        var renderer = RequireDevice();
        if (target is null) return;
        var owner = FramebufferOwnership(target);
        if (owner.Released) return;
        if (ReferenceEquals(currentFramebuffer, target)) SetFramebuffer(null, keepViewport: true);
        if (disposeTextures)
        {
            foreach (int texture in target.ColorTextureIds) renderer.DeleteTexture(texture);
            if (target.DepthTextureId > 0) renderer.DeleteTexture(target.DepthTextureId);
        }
        if (owner.Handle > 0)
        {
            Stated.ForgetFramebuffer(owner.Handle);
            renderer.DeleteFramebuffer(owner.Handle);
        }
        owner.Released = true;
        target.Disposed = true;
    }
}
