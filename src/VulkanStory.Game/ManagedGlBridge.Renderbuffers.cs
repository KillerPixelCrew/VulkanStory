using System.Reflection;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private sealed class RenderbufferObject(int name)
    {
        internal readonly int Name = name;
        internal int Image, Width, Height, Format = 0x8056, Samples;
        internal bool Created, Deleted;
    }
    private readonly Dictionary<int, RenderbufferObject> renderbuffers = new();
    private readonly Dictionary<(int Framebuffer, int Attachment), RenderbufferObject> renderbufferAttachments = new();

    private int GenRenderbuffer()
    { int name = Name(); renderbuffers.Add(name, new(name)); return name; }
    private void BindRenderbuffer(int target, int name)
    {
        if (target != 0x8D41) Error(0x0500);
        RenderbufferObject? value = null;
        if (name != 0 && (!renderbuffers.TryGetValue(name, out value) || value.Deleted)) Error(0x0502);
        if (value is not null) value.Created = true;
        C.Renderbuffer = value;
        CollectRenderbuffers();
    }
    private RenderbufferObject BoundRenderbuffer(int target)
    { if (target != 0x8D41) Error(0x0500); return C.Renderbuffer ?? throw new GlError(0x0502); }
    private void RenderbufferStorage(int target, int samples, int format, int width, int height)
    {
        RenderbufferObject value = BoundRenderbuffer(target);
        if (samples < 0 || samples > Device.MaxGlRenderbufferSamples || width < 0 || height < 0 || width > Device.MaxGlRenderbufferSize || height > Device.MaxGlRenderbufferSize) Error(0x0501);
        if (format is not (0x8051 or 0x8058 or 0x8C43 or 0x8229 or 0x822B or 0x881A or 0x8814 or 0x81A5 or 0x81A6 or 0x8CAC or 0x8D48 or 0x88F0 or 0x8CAD)) Error(0x0500);
        int image = 0, actualSamples = 0;
        try { image = Device.CreateGlRenderbuffer(width, height, format, samples, out actualSamples); }
        catch (NotSupportedException) { Error(0x0502); }
        int previous = value.Image;
        value.Image = image; value.Width = width; value.Height = height; value.Format = format; value.Samples = actualSamples;
        foreach (var entry in renderbufferAttachments.Where(entry => ReferenceEquals(entry.Value, value)).ToArray())
        {
            Device.AttachFramebufferTextureLevel(entry.Key.Framebuffer, entry.Key.Attachment, image, 0);
            session.Graphics.RefreshManagedFramebuffer(entry.Key.Framebuffer);
        }
        if (previous != 0) Device.DeleteTexture(previous);
    }
    private void FramebufferRenderbuffer(int target, int attachment, int renderbufferTarget, int name)
    {
        int framebuffer = FramebufferFor(target);
        if (PublicFramebuffer(framebuffer) == 0) Error(0x0502);
        if (renderbufferTarget != 0x8D41) Error(0x0500);
        if (attachment is not (0x8D00 or 0x8D20 or 0x821A) && (uint)(attachment - 0x8CE0) >= StatedRenderState.MaxColorAttachments) Error(0x0500);
        RenderbufferObject? value = null;
        if (name != 0 && (!renderbuffers.TryGetValue(name, out value) || !value.Created || value.Deleted)) Error(0x0502);
        foreach (int slot in AttachmentTokens(attachment))
        {
            renderbufferAttachments.Remove((framebuffer, slot));
            if (value is not null) renderbufferAttachments.Add((framebuffer, slot), value);
            Device.AttachFramebufferTextureLevel(framebuffer, slot, value?.Image ?? 0, 0);
        }
        session.Graphics.RefreshManagedFramebuffer(framebuffer);
        CollectRenderbuffers();
    }
    private static int[] AttachmentTokens(int attachment) => attachment == 0x821A ? [0x8D00, 0x8D20] : [attachment];
    private void ForgetRenderbufferAttachment(int framebuffer, int attachment)
    {
        foreach (int slot in AttachmentTokens(attachment)) renderbufferAttachments.Remove((framebuffer, slot));
        CollectRenderbuffers();
    }
    private void DeleteRenderbuffer(int name)
    {
        if (!renderbuffers.TryGetValue(name, out RenderbufferObject? value) || value.Deleted) return;
        value.Deleted = true;
        if (ReferenceEquals(C.Renderbuffer, value)) C.Renderbuffer = null;
        foreach (var entry in renderbufferAttachments.Where(entry => ReferenceEquals(entry.Value, value) &&
            (entry.Key.Framebuffer == Target || entry.Key.Framebuffer == ReadTarget)).ToArray())
        {
            renderbufferAttachments.Remove(entry.Key);
            Device.AttachFramebufferTextureLevel(entry.Key.Framebuffer, entry.Key.Attachment, 0, 0);
            session.Graphics.RefreshManagedFramebuffer(entry.Key.Framebuffer);
        }
        CollectRenderbuffers();
    }
    private void CollectRenderbuffers()
    {
        foreach (RenderbufferObject value in renderbuffers.Values.Where(value => value.Deleted &&
            !contexts.Values.Any(context => ReferenceEquals(context.Renderbuffer, value)) &&
            !renderbufferAttachments.Values.Any(attached => ReferenceEquals(attached, value))).ToArray())
        {
            renderbuffers.Remove(value.Name);
            if (value.Image != 0) Device.DeleteTexture(value.Image);
        }
    }
    private int GlFramebufferStatus(int framebuffer)
    {
        if (renderbufferAttachments.Any(entry => entry.Key.Framebuffer == framebuffer && (entry.Value.Width == 0 || entry.Value.Height == 0))) return 0x8CD6;
        return Device.FramebufferStatus(framebuffer);
    }
    private object? RenderbufferQuery(MethodInfo method, object?[] a)
    {
        RenderbufferObject value = BoundRenderbuffer(I(a[0]));
        int depth = value.Format switch { 0x81A5 => 16, 0x81A6 or 0x88F0 => 24, 0x8CAC or 0x8CAD => 32, _ => 0 };
        int stencil = value.Format is 0x8D48 or 0x88F0 or 0x8CAD ? 8 : 0;
        int channel = value.Format switch { 0x881A => 16, 0x8814 => 32, _ => 8 };
        bool color = depth == 0 && stencil == 0 && value.Image != 0;
        int result = I(a[1]) switch
        {
            0x8D42 => value.Width, 0x8D43 => value.Height, 0x8D44 => value.Format, 0x8CAB => value.Samples,
            0x8D50 => color ? channel : 0,
            0x8D51 => color && value.Format != 0x8229 ? channel : 0,
            0x8D52 => color && value.Format is not (0x8229 or 0x822B) ? channel : 0,
            0x8D53 => color && value.Format is not (0x8051 or 0x8229 or 0x822B) ? channel : 0,
            0x8D54 => value.Image != 0 ? depth : 0, 0x8D55 => value.Image != 0 ? stencil : 0,
            _ => throw new GlError(0x0500),
        };
        if (a.Length > 2) WriteOutput(method, a, 2, [result], false);
        return method.ReturnType == typeof(void) ? null : result;
    }
}
