using System.Reflection;
using VulkanStory.Render.Vulkan.Graph;

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
            bool owned = textures.TryGetValue(texture, out TextureObject? image) && image.Created && !image.Deleted;
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
}
