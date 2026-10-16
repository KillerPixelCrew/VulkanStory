using System.Reflection;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan;
using Silk.NET.Vulkan;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    // FBOs are context-local GL objects. Their names are allocated by the shared
    // renderer, so game FBO names and direct-GL names never collide.
    private readonly Dictionary<int, Context> framebuffers = new();
    private int ReadTarget => C.Primary ? session.Graphics.ReadFramebufferId : C.ReadFramebuffer == 0 ? C.Target : C.ReadFramebuffer;
    private int PublicFramebuffer(int name) => name == (C.Primary ? PassDeclaration.DefaultFramebuffer : C.Target) ? 0 : name;
    private int ReadBuffer => C.ReadBuffers.TryGetValue(ReadTarget, out int token) ? token : PublicFramebuffer(ReadTarget) == 0 ? 0x0405 : 0x8CE0;

    private int GenFramebuffer()
    {
        int name = session.Graphics.CreateManagedFramebuffer();
        framebuffers.Add(name, C);
        return name;
    }

    private void CheckFramebufferName(int name)
    {
        if (name == 0) return;
        if (name < 0 || !Device.IsFramebuffer(name)) Error(0x0502);
        if (framebuffers.TryGetValue(name, out Context? owner))
        { if (!ReferenceEquals(owner, C)) Error(0x0502); }
        else if (!C.Primary) Error(0x0502);
    }

    private int FramebufferFor(int target) => target switch
    { 0x8D40 or 0x8CA9 => Target, 0x8CA8 => ReadTarget, _ => throw new GlError(0x0500) };

    private void BindFramebuffer(int target, int name)
    {
        if (target is not (0x8D40 or 0x8CA8 or 0x8CA9)) Error(0x0500);
        CheckFramebufferName(name);
        int handle = name == 0 ? C.Primary ? PassDeclaration.DefaultFramebuffer : C.Target : name;
        int previousRead = ReadTarget;
        if (target is 0x8D40 or 0x8CA9)
        {
            if (C.Primary) session.Graphics.BindManagedFramebuffer(handle);
            else { Device.EndStagePass(); C.DrawFramebuffer = name; }
        }
        int read = target is 0x8D40 or 0x8CA8 ? handle : previousRead;
        if (C.Primary) session.Graphics.ReadFramebufferId = read;
        else C.ReadFramebuffer = read == C.Target ? 0 : read;
    }

    private void DeleteFramebuffer(int name)
    {
        if (name == 0) return;
        if (!Device.IsFramebuffer(name)) return;
        CheckFramebufferName(name);
        Device.EndStagePass();
        Device.DeleteFramebuffer(name);
    }

    private void ForgetFramebuffer(int name)
    {
        framebuffers.Remove(name);
        foreach (Context context in contexts.Values)
        {
            context.ReadBuffers.Remove(name);
            context.State.ForgetFramebuffer(name);
            if (context.DrawFramebuffer == name) context.DrawFramebuffer = 0;
            if (context.ReadFramebuffer == name) context.ReadFramebuffer = 0;
        }
    }

    private void FramebufferTexture(int target, int attachment, int textureTarget, int texture, int mipLevel)
    {
        int framebuffer = FramebufferFor(target);
        if (PublicFramebuffer(framebuffer) == 0) Error(0x0502);
        int slot = attachment - 0x8CE0;
        if (attachment != 0x8D00 && (uint)slot >= StatedRenderState.MaxColorAttachments) Error(0x0500);
        if (textureTarget != 0x0DE1) Error(0x0500);
        if (mipLevel < 0 || mipLevel > (int)Math.Log2(Device.MaxTextureSize) || texture < 0) Error(0x0501);
        if (texture != 0)
        {
            bool owned = textures.TryGetValue(texture, out TextureObject? image) && image.Created && !image.Deleted && image.Target == 0x0DE1;
            if (!owned && !Device.TryGetTexture2DDescription(texture, out _, out _, out _, out _)) Error(0x0502);
        }
        Device.AttachFramebufferTextureLevel(framebuffer, attachment, texture, mipLevel);
        session.Graphics.RefreshManagedFramebuffer(framebuffer);
    }

    private object? FramebufferAttachmentQuery(MethodInfo method, object?[] a)
    {
        int framebuffer = FramebufferFor(I(a[0]));
        if (PublicFramebuffer(framebuffer) == 0) Error(0x0502);
        if (!Device.TryFramebufferAttachment(framebuffer, I(a[1]), out int texture, out int level, out int layer)) Error(0x0500);
        int value = I(a[2]) switch
        { 0x8CD0 => texture == 0 ? 0 : 0x1702, 0x8CD1 => texture, 0x8CD2 => level, 0x8CD3 => 0, 0x8CD4 => layer, _ => throw new GlError(0x0500) };
        if (a.Length > 3) WriteOutput(method, a, 3, [value], false);
        return method.ReturnType == typeof(void) ? null : value;
    }

    private void SelectDrawBuffers(int[] tokens)
    {
        if (tokens.Length > StatedRenderState.MaxColorAttachments) Error(0x0501);
        var routes = Enumerable.Repeat(-1, StatedRenderState.MaxColorAttachments).ToArray();
        var used = new HashSet<int>();
        bool window = PublicFramebuffer(Target) == 0;
        for (int output = 0; output < tokens.Length; output++)
        {
            int token = tokens[output];
            if (token == 0) continue;
            int slot = window ? token is 0x0402 or 0x0405 ? 0 : -1 : token - 0x8CE0;
            if ((uint)slot >= StatedRenderState.MaxColorAttachments) Error(0x0500);
            if (!used.Add(slot)) Error(0x0502);
            routes[output] = slot;
        }
        State.SetDrawBufferRoutes(Target, routes);
    }

    private int DrawBufferToken(int output)
    {
        int slot = State.DrawBufferAttachment(Target, output);
        return slot < 0 ? 0 : PublicFramebuffer(Target) == 0 ? 0x0405 : 0x8CE0 + slot;
    }

    private void SelectReadBuffer(int token)
    {
        if (token != 0 && (PublicFramebuffer(ReadTarget) == 0 ? token is not (0x0402 or 0x0405) : (uint)(token - 0x8CE0) >= StatedRenderState.MaxColorAttachments)) Error(0x0500);
        C.ReadBuffers[ReadTarget] = token;
    }

    private void BlitFramebuffer(object?[] a)
    {
        int mask = I(a[8]), filter = I(a[9]);
        if ((mask & ~0x4500) != 0) Error(0x0501);
        if (filter is not (0x2600 or 0x2601)) Error(0x0500);
        if ((mask & 0x0500) != 0 && filter != 0x2600) Error(0x0502);
        if (Device.FramebufferStatus(ReadTarget) != 0x8CD5 || Device.FramebufferStatus(Target) != 0x8CD5) Error(0x0506);
        var sourceRect = new TextureBlitRectangle(I(a[0]), I(a[1]), I(a[2]), I(a[3]));
        var destinationRect = new TextureBlitRectangle(I(a[4]), I(a[5]), I(a[6]), I(a[7]));
        var transfers = new List<(int Source, int SourceLevel, int SourceLayer, int Destination, int DestinationLevel, int DestinationLayer)>();
        if ((mask & 0x4000) != 0 && ReadBuffer != 0)
        {
            int attachment = PublicFramebuffer(ReadTarget) == 0 ? 0x8CE0 : ReadBuffer;
            if (Device.TryFramebufferAttachment(ReadTarget, attachment, out int source, out int level, out int sourceLayer) && source != 0)
                for (int output = 0; output < StatedRenderState.MaxColorAttachments; output++)
                {
                    int slot = State.DrawBufferAttachment(Target, output);
                    if (slot >= 0 && Device.TryFramebufferAttachment(Target, 0x8CE0 + slot, out int destination, out int destinationLevel, out int destinationLayer) && destination != 0)
                        transfers.Add((source, level, sourceLayer, destination, destinationLevel, destinationLayer));
                }
        }
        if ((mask & 0x0100) != 0 && Device.TryFramebufferAttachment(ReadTarget, 0x8D00, out int sourceDepth, out int sourceDepthLevel, out int sourceDepthLayer) && sourceDepth != 0 &&
            Device.TryFramebufferAttachment(Target, 0x8D00, out int destinationDepth, out int destinationDepthLevel, out int destinationDepthLayer) && destinationDepth != 0)
        {
            if (Device.TextureOf(sourceDepth)!.Format != Device.TextureOf(destinationDepth)!.Format) Error(0x0502);
            transfers.Add((sourceDepth, sourceDepthLevel, sourceDepthLayer, destinationDepth, destinationDepthLevel, destinationDepthLayer));
        }
        if (transfers.Count == 0) return;
        (int Width, int Height) sourceSize = (int.MaxValue, int.MaxValue), destinationSize = (int.MaxValue, int.MaxValue);
        foreach (var transfer in transfers)
        {
            var read = Device.TextureOf(transfer.Source)!;
            var draw = Device.TextureOf(transfer.Destination)!;
            sourceSize = (Math.Min(sourceSize.Width, (int)Math.Max(1u, read.Width >> transfer.SourceLevel)), Math.Min(sourceSize.Height, (int)Math.Max(1u, read.Height >> transfer.SourceLevel)));
            destinationSize = (Math.Min(destinationSize.Width, (int)Math.Max(1u, draw.Width >> transfer.DestinationLevel)), Math.Min(destinationSize.Height, (int)Math.Max(1u, draw.Height >> transfer.DestinationLevel)));
        }
        Rect2D? clip = State.ScissorEnabled ? State.Scissor : null;
        foreach (var transfer in transfers)
            Device.BlitTextureRectangle(transfer.Source, transfer.SourceLevel, transfer.SourceLayer, transfer.Destination, transfer.DestinationLevel, transfer.DestinationLayer,
                sourceRect, destinationRect, sourceSize.Width, sourceSize.Height, destinationSize.Width, destinationSize.Height, clip, filter == 0x2601);
        // There are no stencil attachments in this renderer; their clear/blit bit has no image to write.
    }

    private void CopyTexSubImage(object?[] a)
    {
        if (I(a[0]) != 0x0DE1) Error(0x0500);
        int level = I(a[1]), x = I(a[2]), y = I(a[3]), sx = I(a[4]), sy = I(a[5]), width = I(a[6]), height = I(a[7]);
        if (level < 0 || x < 0 || y < 0 || width < 0 || height < 0) Error(0x0501);
        if (!Device.TryGetTexture2DDescription(BoundTexture(), out int storageWidth, out int storageHeight, out int levels, out _) || level >= levels) Error(0x0502);
        if ((long)x + width > Math.Max(1, storageWidth >> level) || (long)y + height > Math.Max(1, storageHeight >> level)) Error(0x0501);
        if (Device.FramebufferStatus(ReadTarget) != 0x8CD5) Error(0x0506);
        bool depth = Device.IsDepthTexture(BoundTexture());
        int attachment = depth ? 0x8D00 : PublicFramebuffer(ReadTarget) == 0 ? 0x8CE0 : ReadBuffer;
        int source = 0, sourceLevel = 0, sourceLayer = 0;
        if (attachment == 0 || !Device.TryFramebufferAttachment(ReadTarget, attachment, out source, out sourceLevel, out sourceLayer) || source == 0) Error(0x0502);
        if (Device.IsDepthTexture(source) != depth) Error(0x0502);
        var readImage = Device.TextureOf(source)!;
        Device.BlitTextureRectangle(source, sourceLevel, sourceLayer, BoundTexture(), level, 0,
            new TextureBlitRectangle(sx, sy, checked(sx + width), checked(sy + height)), new TextureBlitRectangle(x, y, checked(x + width), checked(y + height)),
            (int)Math.Max(1u, readImage.Width >> sourceLevel), (int)Math.Max(1u, readImage.Height >> sourceLevel), Math.Max(1, storageWidth >> level), Math.Max(1, storageHeight >> level), null, false);
    }
}
