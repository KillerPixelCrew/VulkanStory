using System.Numerics;
using System.Runtime.InteropServices;

namespace VulkanStory.Contracts;

/// <summary>One immutable stock producer handoff; visual configuration has a separate dirty lifetime.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct ParticleInstanceInput
{
    [FieldOffset(0)] public double CameraX;
    [FieldOffset(8)] public double CameraY;
    [FieldOffset(16)] public double CameraZ;
    [FieldOffset(24)] public uint Source;
    [FieldOffset(28)] public uint Target;
    [FieldOffset(32)] public Vector4 Interpolation;
    [FieldOffset(48)] public Vector4 VelocityPhase;
    [FieldOffset(64)] public uint Direction;
    [FieldOffset(68)] public uint Light;
    [FieldOffset(72)] public uint Color;
    [FieldOffset(76)] public uint Flags;
}
