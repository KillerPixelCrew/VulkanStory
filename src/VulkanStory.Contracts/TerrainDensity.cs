using System.Runtime.InteropServices;

namespace VulkanStory.Contracts;

/// <summary>Already ordered stock octaves; no seed, sorting or normalization is reconstructed by the backend.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TerrainDensityOctave
{
    public long Seed;
    public double X, FrequencyY, Z, Amplitude, Threshold, SmoothingFactor, StopBound;
}

[StructLayout(LayoutKind.Sequential)]
public struct TerrainDensityColumn
{
    public uint OctaveStart, OctaveCount, RequestStart, RequestCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct TerrainDensityRequest
{
    public double Y, InverseThreshold;
}

/// <summary>One result per requested sign, in request order. Only positive/nonpositive classification is consumed.</summary>
public interface ITerrainDensityBatch : IDisposable
{
    bool Complete(Span<uint> solidities);
}

/// <summary>Bounded immediate submission; a refused tile leaves evaluation to the stock CPU ColumnNoise.</summary>
public delegate ITerrainDensityBatch? TerrainDensityBackend(ReadOnlySpan<TerrainDensityColumn> columns,
    ReadOnlySpan<TerrainDensityOctave> octaves, ReadOnlySpan<TerrainDensityRequest> requests);
