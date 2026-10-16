using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private static void RequireImageFormat(int format, bool immutableStorage = false)
    {
        if (immutableStorage && (format is 0x88F0 or 0x8CAD)) return;
        if (format is not (0x1902 or 0x1903 or 0x1907 or 0x1908 or 0x8227 or 0x8229 or 0x822B or 0x822D or 0x822E or 0x822F or 0x8230 or
            0x8051 or 0x8058 or 0x805B or 0x8814 or 0x8815 or 0x881A or 0x881B or 0x8C3A or 0x8C42 or 0x8C43 or 0x80E1 or 0x81A5 or 0x81A6 or 0x81A7 or 0x8CAC))
            throw new NotSupportedException("GL image storage format 0x" + format.ToString("X") + " has no storage conversion.");
    }

    private TextureObject TextureForImage(int target)
    {
        if (target is not (0x0DE1 or 0x806F)) Error(0x0500);
        int name = BoundTexture(target);
        if (name == 0) Error(0x0502);
        if (textures.TryGetValue(name, out TextureObject? texture)) return texture;
        var image = Device.TextureOf(name);
        if (image is null || image.Cube || image.Layers != 1 || image.Volume != (target == 0x806F)) Error(0x0502);
        texture = new TextureObject(name) { Borrowed = true, Created = true, Target = target, Width = (int)image!.Width,
            Height = (int)image.Height, Depth = (int)image.Depth, Levels = (int)image.MipLevels, Format = image.GlInternalFormat };
        for (int level = 0; level < texture.Levels; level++) texture.Images[level] = new(Math.Max(1, texture.Width >> level), Math.Max(1, texture.Height >> level), Math.Max(1, texture.Depth >> level), texture.Format);
        textures.Add(name, texture);
        return texture;
    }

    private void TextureImage(object?[] a, bool volume)
    {
        int target = I(a[0]), level = I(a[1]), format = I(a[2]), width = I(a[3]), height = I(a[4]), depth = volume ? I(a[5]) : 1;
        if (target != (volume ? 0x806F : 0x0DE1)) Error(0x0500);
        int border = I(a[volume ? 6 : 5]), sourceFormat = I(a[volume ? 7 : 6]), sourceType = I(a[volume ? 8 : 7]);
        object? pixels = a[volume ? 9 : 8];
        int limit = volume ? Device.MaxVolumeSize : Device.MaxTextureSize;
        if (level < 0 || level > (int)Math.Log2(limit) || width < 0 || height < 0 || depth < 0 || border != 0 || width > limit || height > limit || depth > limit) Error(0x0501);
        if (width == 0 || height == 0 || depth == 0) throw new NotSupportedException("Zero-sized mutable image levels need undefined-level storage.");
        RequireImageFormat(format);
        if (volume && (format is 0x1902 or 0x81A5 or 0x81A6 or 0x81A7 or 0x8CAC)) Error(0x0502);
        ValidatePixelFormat(sourceFormat, sourceType);
        bool depthFormat = format is 0x1902 or 0x81A5 or 0x81A6 or 0x81A7 or 0x8CAC;
        if (depthFormat != (sourceFormat == 0x1902)) Error(0x0502);
        TextureObject texture = TextureForImage(target);
        if (texture.Immutable) Error(0x0502);
        int baseWidth = texture.Width, baseHeight = texture.Height, baseDepth = texture.Depth;
        if (level == 0 || baseWidth == 0)
        {
            long w = (long)width << level, h = (long)height << level, d = volume ? (long)depth << level : 1;
            if (w > limit || h > limit || d > limit) Error(0x0501);
            baseWidth = (int)w; baseHeight = (int)h; baseDepth = (int)d;
        }
        else if (width != Math.Max(1, baseWidth >> level) || height != Math.Max(1, baseHeight >> level) || depth != Math.Max(1, baseDepth >> level) || format != texture.Format)
            throw new NotSupportedException("Independent mutable mip extents/formats need per-level image backing.");
        int levels = 1 + (int)Math.Log2(Math.Max(baseWidth, Math.Max(baseHeight, baseDepth)));
        Device.DefineMutableTexture(texture.Name, baseWidth, baseHeight, baseDepth, format, levels, volume, level);
        texture.Width = baseWidth; texture.Height = baseHeight; texture.Depth = baseDepth; texture.Levels = levels; texture.Format = format;
        texture.Images[level] = new(width, height, depth, format);
        if (C.PixelUnpackBuffer is null && pixels is not System.Array && Pointer(pixels) == IntPtr.Zero) return;
        UploadPixels(texture.Name, level, 0, 0, 0, width, height, depth, sourceFormat, sourceType, pixels);
    }

    private void TextureSubImage(object?[] a, bool volume)
    {
        int target = I(a[0]);
        if (target != (volume ? 0x806F : 0x0DE1)) Error(0x0500);
        int level = I(a[1]), x = I(a[2]), y = I(a[3]), z = volume ? I(a[4]) : 0;
        int width = I(a[volume ? 5 : 4]), height = I(a[volume ? 6 : 5]), depth = volume ? I(a[7]) : 1;
        int format = I(a[volume ? 8 : 6]), type = I(a[volume ? 9 : 7]);
        object? pixels = a[volume ? 10 : 8];
        ValidatePixelFormat(format, type);
        TextureObject texture = TextureForImage(target);
        if (level < 0 || x < 0 || y < 0 || z < 0 || width < 0 || height < 0 || depth < 0) Error(0x0501);
        if (!texture.Images.TryGetValue(level, out TextureLevel image)) Error(0x0502);
        if ((long)x + width > image.Width || (long)y + height > image.Height || (long)z + depth > image.Depth) Error(0x0501);
        if (width == 0 || height == 0 || depth == 0) return;
        UploadPixels(texture.Name, level, x, y, z, width, height, depth, format, type, pixels);
    }

    private void GenerateTextureMips(int target)
    {
        TextureObject texture = TextureForImage(target);
        int first = Device.TextureOf(texture.Name)!.EffectiveBaseLevel;
        if (!texture.Images.TryGetValue(first, out TextureLevel image)) Error(0x0502);
        Device.GenerateMipmaps(texture.Name);
        int last = Device.TextureOf(texture.Name)!.EffectiveMaxLevel;
        for (int level = first + 1; level <= last; level++) texture.Images[level] = new(Math.Max(1, image.Width >> (level - first)), Math.Max(1, image.Height >> (level - first)), Math.Max(1, image.Depth >> (level - first)), image.Format);
    }

    private static (int Components, int Bytes, bool Packed) ValidatePixelFormat(int format, int type)
    {
        int components = format switch { 0x1902 or 0x1903 => 1, 0x8227 => 2, 0x1907 => 3, 0x1908 or 0x80E1 => 4, _ => throw new GlError(0x0500) };
        int bytes = type switch { 0x1400 or 0x1401 => 1, 0x1402 or 0x1403 or 0x140B => 2, 0x1404 or 0x1405 or 0x1406 or 0x8035 or 0x8367 => 4, _ => throw new GlError(0x0500) };
        bool packed = type is 0x8035 or 0x8367;
        if (packed && components != 4) Error(0x0502);
        return (components, bytes, packed);
    }

    private void UploadPixels(int name, int level, int x, int y, int z, int width, int height, int depth, int sourceFormat, int sourceType, object? pixels)
    {
        var layout = ValidatePixelFormat(sourceFormat, sourceType);
        var image = Device.TextureOf(name)!;
        bool imageDepth = (image.Aspect & ImageAspectFlags.DepthBit) != 0;
        if (imageDepth != (sourceFormat == 0x1902)) Error(0x0502);
        int pixelBytes = layout.Packed ? 4 : layout.Components * layout.Bytes;
        int rowLength = PixelStoreValue(0x0CF2); if (rowLength == 0) rowLength = width;
        int imageHeight = PixelStoreValue(0x806E); if (imageHeight == 0) imageHeight = height;
        int alignment = PixelStoreValue(0x0CF5), stride = checked((checked(rowLength * pixelBytes) + alignment - 1) & ~(alignment - 1));
        int slice = checked(stride * imageHeight);
        int start = checked(PixelStoreValue(0x0CF4) * pixelBytes + PixelStoreValue(0x0CF3) * stride + (image.Volume ? PixelStoreValue(0x806D) * slice : 0));
        byte[] input = PixelInputBytes(pixels, checked(start + (depth - 1) * slice + (height - 1) * stride + width * pixelBytes));
        byte[] converted = ConvertPixels(input, start, stride, slice, width, height, depth, sourceFormat, sourceType, layout, image.Format, image.GlInternalFormat,
            PixelStoreValue(0x0CF0) != 0, out int storageBytes);
        GCHandle pin = GCHandle.Alloc(converted, GCHandleType.Pinned);
        try { Device.UploadTextureStorage(name, level, x, y, z, width, height, depth, pin.AddrOfPinnedObject(), storageBytes); }
        finally { pin.Free(); }
    }
}
