using System.Numerics;
using System.Runtime.InteropServices;

namespace VulkanStory.Contracts;

/// <summary>CPU admission snapshot and resident configuration layout, preserving double world positions.</summary>
[StructLayout(LayoutKind.Explicit, Size = 224)]
public struct ParticleSimulationInput
{
    [FieldOffset(0)] public double X;
    [FieldOffset(8)] public double Y;
    [FieldOffset(16)] public double Z;
    [FieldOffset(24)] private double reservedPosition;
    [FieldOffset(32)] public Vector4 VelocityGravity;
    [FieldOffset(48)] public Vector4 Timing;
    [FieldOffset(64)] public Vector4 Parent;
    [FieldOffset(80)] public Vector4 StartingVelocity;
    [FieldOffset(96)] public Vector4 Control;
    [FieldOffset(112)] public Vector4 Physics;
    [FieldOffset(128)] public ParticleEvolution VelocityX;
    [FieldOffset(160)] public ParticleEvolution VelocityY;
    [FieldOffset(192)] public ParticleEvolution VelocityZ;
}

/// <summary>GPU-produced clipped position/motion, pre-bounce velocity, collision flags, age and lifetime.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct ParticleSimulationOutput
{
    // A custom collision callback intersects the actual stock motion query.
    // The adapter must run that ordered CPU tick and rebase later speculation.
    public const uint RequiresCpuCollision = 0x80000000;
    [FieldOffset(0)] public double X;
    [FieldOffset(8)] public double Y;
    [FieldOffset(16)] public double Z;
    [FieldOffset(24)] public uint CollisionFlags;
    [FieldOffset(28)] public uint QueryBounds;
    [FieldOffset(32)] public Vector4 VelocityAge;
    [FieldOffset(48)] public Vector4 MotionAlive;
}

/// <summary>Fresh world observations for one due step; never reused across world updates.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParticleSimulationWorldInput
{
    public Vector4 Liquid;
    public Vector4 Flow;
    public uint BoxStart, BoxCount;
    public float MotionCap;
    // Actual monotonic CachedCuboidList allocation count at batch admission.
    public uint InitialPopulatedSize;
}

/// <summary>One stock collision box in WalkBlocks and collision-array order; Minimum.W marks a custom callback cell.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParticleSimulationBox
{
    public Vector4 Minimum, Maximum;
    public int X, Y, Z;
    private int reserved;
}

/// <summary>Compact command; persistent spawn state supplies position, velocity and configuration.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParticleSimulationCommand
{
    public uint Slot;
    public float Age, LifeDelta, PhysicsDelta;
    public Vector4 Control;
}

/// <summary>An immutable GPU trajectory retained independently of the submission slot.</summary>
public abstract class ParticleSimulationBuffer : IDisposable
{
    public abstract ParticleSimulationBuffer Retain();
    public abstract void Dispose();
}

/// <summary>Instance ordering and stock interpolation for one immutable GPU position.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct ParticleSimulationRenderInput
{
    [FieldOffset(0)] public double CameraX;
    [FieldOffset(8)] public double CameraY;
    [FieldOffset(16)] public double CameraZ;
    [FieldOffset(24)] public uint Source;
    [FieldOffset(28)] public uint Target;
    [FieldOffset(32)] public Vector4 Interpolation;
    [FieldOffset(48)] public uint UploadDeltaX;
    [FieldOffset(52)] public uint UploadDeltaY;
    [FieldOffset(56)] public uint UploadDeltaZ;
}

/// <summary>Fixed-step submissions. Each actor produces its actually due stock step.</summary>
public interface IParticleSimulationBackend
{
    /// <summary>Copies inputs and submits without waiting. Returns zero when the bounded slot or retained-buffer budget is exhausted.</summary>
    long Submit(ReadOnlySpan<ParticleSimulationInput> input, ReadOnlySpan<object> spawns,
        ReadOnlySpan<ParticleSimulationWorldInput> world, ReadOnlySpan<ParticleSimulationBox> boxes);
    /// <summary>Completes accepted work once, copies the CPU facade and transfers ownership of its immutable GPU buffer.</summary>
    ParticleSimulationBuffer Complete(long ticket, Span<ParticleSimulationOutput> output);
    /// <summary>Relinquishes a ticket immediately. Its buffers remain owned by the backend until GPU completion.</summary>
    void Release(long ticket);
    /// <summary>Relinquishes all pending tickets at a world or feature transition, without waiting.</summary>
    void DiscardPending();
}
