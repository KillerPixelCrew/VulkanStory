using System.Buffers.Binary;
using Silk.NET.Vulkan;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private static byte[] ConvertPixels(byte[] input, int start, int rowStride, int sliceStride, int width, int height, int depth,
        int sourceFormat, int sourceType, (int Components, int Bytes, bool Packed) layout, Format storage, int internalFormat, bool swap, out int bytesPerPixel)
    {
        int components, scalarBytes;
        switch (storage)
        {
            case Format.R8Unorm: components = 1; scalarBytes = 1; break;
            case Format.R8G8Unorm: components = 2; scalarBytes = 1; break;
            case Format.R8G8B8A8Unorm: case Format.B8G8R8A8Unorm: components = 4; scalarBytes = 1; break;
            case Format.R16Sfloat: case Format.D16Unorm: components = 1; scalarBytes = 2; break;
            case Format.R16G16Sfloat: components = 2; scalarBytes = 2; break;
            case Format.R16G16B16A16Sfloat: case Format.R16G16B16A16Unorm: components = 4; scalarBytes = 2; break;
            case Format.R32Sfloat: case Format.D32Sfloat: case Format.B10G11R11UfloatPack32: components = 1; scalarBytes = 4; break;
            case Format.R32G32Sfloat: components = 2; scalarBytes = 4; break;
            case Format.R32G32B32A32Sfloat: components = 4; scalarBytes = 4; break;
            default: throw new NotSupportedException("GL source conversion is unavailable for " + storage);
        }
        bytesPerPixel = components * scalarBytes;
        var output = new byte[checked(width * height * depth * bytesPerPixel)];
        int sourcePixelBytes = layout.Packed ? 4 : layout.Components * layout.Bytes;
        bool alphaOne = internalFormat is 0x1907 or 0x8051 or 0x881B or 0x8815;
        float Read(int pixelOffset, int channel)
        {
            if (channel == 3 && alphaOne) return 1;
            if (channel >= layout.Components) return channel == 3 ? 1 : 0;
            int component = sourceFormat == 0x80E1 ? channel == 0 ? 2 : channel == 2 ? 0 : channel : channel;
            if (layout.Packed)
            {
                uint word = swap ? BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(pixelOffset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(pixelOffset, 4));
                int shift = sourceType == 0x8367 ? component * 8 : (3 - component) * 8;
                return ((word >> shift) & 255) / 255f;
            }
            int offset = pixelOffset + component * layout.Bytes;
            ushort U16() => swap ? BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(offset, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(offset, 2));
            uint U32() => swap ? BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(offset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset, 4));
            return sourceType switch
            {
                0x1400 => Math.Max(-1f, unchecked((sbyte)input[offset]) / 127f), 0x1401 => input[offset] / 255f,
                0x1402 => Math.Max(-1f, unchecked((short)U16()) / 32767f), 0x1403 => U16() / 65535f,
                0x1404 => (float)Math.Max(-1d, unchecked((int)U32()) / (double)int.MaxValue), 0x1405 => (float)(U32() / (double)uint.MaxValue),
                0x1406 => BitConverter.Int32BitsToSingle(unchecked((int)U32())), 0x140B => (float)BitConverter.UInt16BitsToHalf(U16()),
                _ => throw new GlError(0x0500),
            };
        }
        for (int slice = 0; slice < depth; slice++)
            for (int row = 0; row < height; row++)
                for (int column = 0; column < width; column++)
                {
                    int source = checked(start + slice * sliceStride + row * rowStride + column * sourcePixelBytes);
                    int destination = checked(((slice * height + row) * width + column) * bytesPerPixel);
                    if (storage == Format.B10G11R11UfloatPack32)
                    {
                        uint packed = PackUnsignedFloat(Read(source, 0), 6) | (PackUnsignedFloat(Read(source, 1), 6) << 11) | (PackUnsignedFloat(Read(source, 2), 5) << 22);
                        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(destination, 4), packed);
                        continue;
                    }
                    for (int component = 0; component < components; component++)
                    {
                        int channel = storage == Format.B8G8R8A8Unorm ? component == 0 ? 2 : component == 2 ? 0 : component : component;
                        float value = Read(source, channel);
                        Span<byte> target = output.AsSpan(destination + component * scalarBytes, scalarBytes);
                        switch (storage)
                        {
                            case Format.R8Unorm: case Format.R8G8Unorm: case Format.R8G8B8A8Unorm: case Format.B8G8R8A8Unorm:
                                target[0] = (byte)Math.Round(Math.Clamp(value, 0, 1) * 255); break;
                            case Format.R16Sfloat: case Format.R16G16Sfloat: case Format.R16G16B16A16Sfloat:
                                BinaryPrimitives.WriteUInt16LittleEndian(target, BitConverter.HalfToUInt16Bits((Half)value)); break;
                            case Format.D16Unorm: case Format.R16G16B16A16Unorm:
                                BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535)); break;
                            default:
                                if (storage == Format.D32Sfloat) value = Math.Clamp(value, 0, 1);
                                BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(value)); break;
                        }
                    }
                }
        return output;
    }

    private static uint PackUnsignedFloat(float value, int mantissaBits)
    {
        uint maximumExponent = 31u << mantissaBits;
        if (float.IsNaN(value)) return maximumExponent | 1u;
        if (value <= 0) return 0;
        if (float.IsPositiveInfinity(value)) return maximumExponent;
        int exponent = Math.ILogB(value);
        if (exponent < -14)
            return (uint)Math.Min(1u << mantissaBits, Math.Round(Math.ScaleB((double)value, 14 + mantissaBits)));
        int encodedExponent = exponent + 15;
        if (encodedExponent >= 31) return maximumExponent;
        uint mantissa = (uint)Math.Round((Math.ScaleB((double)value, -exponent) - 1d) * (1u << mantissaBits));
        if (mantissa == 1u << mantissaBits) { mantissa = 0; encodedExponent++; }
        return encodedExponent >= 31 ? maximumExponent : ((uint)encodedExponent << mantissaBits) | mantissa;
    }
}
