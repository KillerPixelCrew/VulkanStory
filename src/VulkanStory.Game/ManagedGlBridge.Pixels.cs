using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private byte[] PixelInputBytes(object? source, int required)
    {
        if (C.PixelUnpackBuffer is not { } buffer) return CopyBytes(source, required, false);
        if (source is System.Array) Error(0x0502);
        long offset = L(source);
        if (offset < 0 || offset > buffer.Data.Length - (long)required) Error(0x0502);
        return buffer.Data.AsSpan((int)offset, required).ToArray();
    }
    private int PixelStoreValue(int token) => C.PixelStorage.TryGetValue(token, out int value) ? value : token is 0x0CF5 or 0x0D05 ? 4 : 0;

    private void PixelStore(int token, int value)
    {
        if (token is not (>= 0x0CF0 and <= 0x0CF5 or >= 0x0D00 and <= 0x0D05 or 0x806B or 0x806C or 0x806D or 0x806E)) Error(0x0500);
        if (token is 0x0CF5 or 0x0D05)
        { if (value is not (1 or 2 or 4 or 8)) Error(0x0501); }
        else if (token is 0x0CF0 or 0x0CF1 or 0x0D00 or 0x0D01) value = value == 0 ? 0 : 1;
        else if (value < 0) Error(0x0501);
        C.PixelStorage[token] = value;
    }

    private object? TextureLevelQuery(MethodInfo method, object?[] a)
    {
        int target = I(a[0]); if (target is not (0x0DE1 or 0x806F)) Error(0x0500);
        int level = I(a[1]); if (level < 0) Error(0x0501);
        int name = BoundTexture(target);
        TextureLevel image = default;
        bool defined;
        if (textures.TryGetValue(name, out TextureObject? texture)) defined = texture.Images.TryGetValue(level, out image);
        else
        {
            var storage = Device.TextureOf(name);
            defined = storage is not null && storage.Volume == (target == 0x806F) && level < storage.MipLevels;
            if (defined) image = new(Math.Max(1, (int)storage!.Width >> level), Math.Max(1, (int)storage.Height >> level), Math.Max(1, (int)storage.Depth >> level), storage.GlInternalFormat);
        }
        int value = I(a[2]) switch
        { 0x1000 => defined ? image.Width : 0, 0x1001 => defined ? image.Height : 0,
            0x1003 => defined ? image.Format : 0, 0x8071 => defined ? image.Depth : 0, _ => throw new GlError(0x0500) };
        if (a.Length > 3) WriteOutput(method, a, 3, [value], false);
        return method.ReturnType == typeof(void) ? null : value;
    }

    private void GetTexImage(object?[] a)
    {
        if (I(a[0]) != 0x0DE1) Error(0x0500);
        int name = BoundTexture(), level = I(a[1]);
        if (level < 0) Error(0x0501);
        if (textures.TryGetValue(name, out TextureObject? image) &&
            (!image.Images.TryGetValue(level, out TextureLevel definition) || definition.Width == 0 || definition.Height == 0)) return;
        Device.ResolveGlTextureLevel(ref name, ref level);
        if (!Device.TryGetTexture2DDescription(name, out int width, out int height, out int levels, out _) || level >= levels) Error(0x0502);
        PackPixels(name, level, 0, 0, Math.Max(1, width >> level), Math.Max(1, height >> level), I(a[2]), I(a[3]), a[4], null);
    }

    private void GetTextureSubImage(object?[] a)
    {
        int level = I(a[1]), x = I(a[2]), y = I(a[3]), z = I(a[4]), width = I(a[5]), height = I(a[6]), depth = I(a[7]), capacity = I(a[10]);
        if (z != 0 || depth != 1) throw new NotSupportedException("3D texture readback needs a volume-image transfer path.");
        if (capacity < 0) Error(0x0501);
        PackPixels(I(a[0]), level, x, y, width, height, I(a[8]), I(a[9]), a[11], capacity);
    }

    private void ReadPixels(object?[] a)
    {
        if (GlFramebufferStatus(ReadTarget) != 0x8CD5) Error(0x0506);
        if (Device.FramebufferSamples(ReadTarget) != 0) Error(0x0502);
        int format = I(a[4]);
        int attachment = format == 0x1902 ? 0x8D00 : ReadBuffer;
        if (PublicFramebuffer(ReadTarget) == 0 && attachment != 0x8D00) attachment = 0x8CE0;
        int texture = 0, level = 0;
        if (attachment == 0 || !Device.TryFramebufferAttachment(ReadTarget, attachment, out texture, out level, out _)) Error(0x0502);
        PackPixels(texture, level, I(a[0]), I(a[1]), I(a[2]), I(a[3]), format, I(a[5]), a[6], null, clip: true);
    }

    private void PackPixels(int texture, int level, int x, int y, int width, int height, int format, int type, object? destination, int? capacity, bool clip = false)
    {
        int[] channels = format switch
        { 0x1902 or 0x1903 => [0], 0x8227 => [0, 1], 0x1907 => [0, 1, 2], 0x1908 => [0, 1, 2, 3], 0x80E1 => [2, 1, 0, 3], _ => throw new GlError(0x0500) };
        int scalarBytes = type switch { 0x1401 => 1, 0x1403 or 0x140B => 2, 0x1405 or 0x1406 or 0x8367 => 4, _ => throw new GlError(0x0500) };
        bool packed = type == 0x8367; // GL_UNSIGNED_INT_8_8_8_8_REV
        if (packed && channels.Length != 4) Error(0x0502);
        if (level < 0 || !clip && (x < 0 || y < 0) || width < 0 || height < 0) Error(0x0501);
        if (width == 0 || height == 0) return;
        Device.ResolveGlTextureLevel(ref texture, ref level);
        if (!Device.TryGetTexture2DDescription(texture, out int storageWidth, out int storageHeight, out int levels, out _) || level >= levels) Error(0x0502);
        if (Device.TextureOf(texture)!.DefinedLevels is { } defined && !defined.Contains(level)) Error(0x0502);
        bool depth = Device.IsDepthTexture(texture);
        if (depth != (format == 0x1902)) Error(0x0502);
        int imageWidth = Math.Max(1, storageWidth >> level), imageHeight = Math.Max(1, storageHeight >> level);
        if (!clip && ((long)x + width > imageWidth || (long)y + height > imageHeight)) Error(0x0501);
        if (width == 0 || height == 0) return;
        int pixelBytes = packed ? 4 : channels.Length * scalarBytes;
        int rowLength = PixelStoreValue(0x0D02); if (rowLength == 0) rowLength = width;
        int alignment = PixelStoreValue(0x0D05), stride = checked((checked(rowLength * pixelBytes) + alignment - 1) & ~(alignment - 1));
        int start = checked(PixelStoreValue(0x0D03) * stride + PixelStoreValue(0x0D04) * pixelBytes);
        int required = checked(start + (height - 1) * stride + width * pixelBytes);
        int bufferOffset = 0;
        if (C.PixelPackBuffer is { } pack)
        {
            if (destination is System.Array) Error(0x0502);
            long offset = L(destination);
            if (offset < 0 || offset > pack.Data.Length - (long)required) Error(0x0502);
            bufferOffset = (int)offset;
        }
        if (capacity.HasValue && required > capacity.Value) Error(0x0502);
        if (destination is Array array && System.Buffer.ByteLength(array) < required) Error(0x0502);
        if (C.PixelPackBuffer is null && destination is not System.Array && Pointer(destination) == IntPtr.Zero) Error(0x0502);
        int sourceX = Math.Clamp(x, 0, imageWidth), sourceY = Math.Clamp(y, 0, imageHeight);
        int sourceWidth = (int)Math.Max(0, Math.Min((long)x + width, imageWidth) - sourceX);
        int sourceHeight = (int)Math.Max(0, Math.Min((long)y + height, imageHeight) - sourceY);
        TextureCaptureData? data = sourceWidth > 0 && sourceHeight > 0 ? Device.ReadTexturePixels(texture, level, sourceX, sourceY, sourceWidth, sourceHeight) : null;
        float Component(int sourceColumnIndex, int sourceRowIndex, int channel)
        {
            long sx = (long)x + sourceColumnIndex - sourceX, sy = (long)y + sourceRowIndex - sourceY;
            if (data is null || sx < 0 || sy < 0 || sx >= sourceWidth || sy >= sourceHeight) return 0; // Outside the framebuffer is undefined in GL.
            int pixel = checked((int)(sy * sourceWidth + sx));
            return data.Bytes is { } bytes ? bytes[pixel * 4 + channel] / 255f : depth ? data.Floats![pixel] : data.Floats![pixel * 4 + channel];
        }
        bool swap = PixelStoreValue(0x0D00) != 0;
        var row = new byte[checked(width * pixelBytes)];
        for (int rowIndex = 0; rowIndex < height; rowIndex++)
        {
            for (int column = 0; column < width; column++)
                for (int component = 0; component < channels.Length; component++)
                {
                    float value = Component(column, rowIndex, channels[component]);
                    int at = column * pixelBytes + component * (packed ? 1 : scalarBytes);
                    Span<byte> output = row.AsSpan(at, packed ? 1 : scalarBytes);
                    switch (type)
                    {
                        case 0x1401: case 0x8367: output[0] = (byte)Math.Round(Math.Clamp(value, 0, 1) * 255); break;
                        case 0x1403: BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535)); break;
                        case 0x1405: BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)Math.Round((double)Math.Clamp(value, 0, 1) * uint.MaxValue)); break;
                        case 0x1406: BinaryPrimitives.WriteInt32LittleEndian(output, BitConverter.SingleToInt32Bits(value)); break;
                        case 0x140B: BinaryPrimitives.WriteUInt16LittleEndian(output, BitConverter.HalfToUInt16Bits((Half)value)); break;
                    }
                    if (swap && !packed && scalarBytes > 1) output.Reverse();
                }
            if (swap && packed)
                for (int at = 0; at < row.Length; at += 4) row.AsSpan(at, 4).Reverse();
            int offset = checked(start + rowIndex * stride);
            if (C.PixelPackBuffer is { } packBuffer) row.CopyTo(packBuffer.Data, checked(bufferOffset + offset));
            else if (destination is Array outputArray) System.Buffer.BlockCopy(row, 0, outputArray, offset, row.Length);
            else Marshal.Copy(row, 0, IntPtr.Add(Pointer(destination), offset), row.Length);
        }
    }
}
