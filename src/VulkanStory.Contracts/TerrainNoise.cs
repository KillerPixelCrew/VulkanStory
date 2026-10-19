using System.Runtime.InteropServices;

namespace VulkanStory.Contracts;

/// <summary>Stock double rotation/floor performed by the producer; all hash arithmetic is modulo 2^64.
/// X/Y/Z retain their binary32 encodings in the shader's uint inputs; the 48-byte ABI includes trailing padding.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct TerrainNoiseInput
{
    [FieldOffset(0)] public long Seed;
    [FieldOffset(8)] public long PrimeX;
    [FieldOffset(16)] public long PrimeY;
    [FieldOffset(24)] public long PrimeZ;
    [FieldOffset(32)] public float X;
    [FieldOffset(36)] public float Y;
    [FieldOffset(40)] public float Z;
}

/// <summary>A bounded submitted tile. Successful completion copies the full result into caller-owned storage
/// and releases the tile immediately; subsequent completion returns false and disposal is a no-op.
/// Disposal without completion waits for submitted work before releasing the tile.</summary>
public interface ITerrainNoiseBatch : IDisposable
{
    bool Complete(Span<float> results);
}

/// <summary>Returns null immediately when both tiles are leased; callers retain the stock CPU path.</summary>
public delegate ITerrainNoiseBatch? TerrainNoiseBackend(ReadOnlySpan<TerrainNoiseInput> input);
