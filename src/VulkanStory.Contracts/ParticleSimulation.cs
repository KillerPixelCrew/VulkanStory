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
    // Stock collision response inputs. The resident configuration keeps
    // Bounciness; each command carries the current random drag.
    [FieldOffset(24)] public float Bounciness;
    [FieldOffset(28)] public float RandomDrag;
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

/// <summary>GPU-produced clipped position/motion, post-response velocity, collision flags, age and lifetime.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct ParticleSimulationOutput
{
    public const uint LiquidPolicyApplied = 1u << 24;
    public const uint LiquidCell = 1u << 25;
    public const uint LiquidDeath = 1u << 26;
    // A custom collision callback intersects the actual stock motion query.
    // The adapter must run that ordered CPU tick and rebase later speculation.
    public const uint RequiresCpuCollision = 0x80000000;
    // Stock EnumCollideFlags occupy the low bits. The number of selected boxes
    // that used reused CachedCuboidList rounding follows, so a later consumer
    // can confirm the cache prefix it actually observes at the stock tick.
    public const uint CollisionMask = 7;
    public const int ReusedShift = 8;
    public const uint ReusedMask = 0xfffff;
    [FieldOffset(0)] public double X;
    [FieldOffset(8)] public double Y;
    [FieldOffset(16)] public double Z;
    [FieldOffset(24)] public uint CollisionFlags;
    // Low 24 bits retain collision bounds; high bits carry liquid decisions.
    [FieldOffset(28)] public uint QueryBounds;
    [FieldOffset(32)] public Vector4 VelocityAge;
    [FieldOffset(48)] public Vector4 MotionAlive;
}

/// <summary>Fresh world observations for one due step; never reused across world updates.</summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct ParticleSimulationWorldInput
{
    public const uint DieInAir = 1;
    public const uint DieInLiquid = 2;
    public Vector4 Liquid;
    public Vector4 Flow;
    public uint BoxStart, BoxCount;
    public float MotionCap;
    // Actual monotonic CachedCuboidList allocation count at batch admission.
    public uint InitialPopulatedSize;
    // Dense fresh fluid cells (0 air, 1 liquid, 2 opaque callback), X fastest
    // then Y then Z. Bounds are six
    // four-bit signed offsets (+8) from the truncated admission position.
    public uint LiquidStart, LiquidBounds, LiquidPolicy, Reserved;
}

/// <summary>One stock collision box in WalkBlocks and collision-array order; Minimum.W marks a custom callback cell.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParticleSimulationBox
{
    public Vector4 Minimum, Maximum;
    public int X, Y, Z;
    private int reserved;
}

/// <summary>Compact command; persistent spawn state supplies position, velocity and configuration. Control.Y is the current random drag.</summary>
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

/// <summary>Fixed-step submissions. Each actor's next stock step is prepared ahead and consumed without waiting.</summary>
public interface IParticleSimulationBackend
{
    /// <summary>Copies inputs and submits without waiting. Results are copied into <paramref name="output"/> when completion is observed;
    /// the array must remain untouched until the ticket completes or is released. Returns zero when the bounded slot or retained-buffer budget is exhausted.</summary>
    long Submit(ReadOnlySpan<ParticleSimulationInput> input, ReadOnlySpan<object> spawns,
        ReadOnlySpan<ParticleSimulationWorldInput> world, ReadOnlySpan<ParticleSimulationBox> boxes, ReadOnlySpan<uint> liquidCells,
        ParticleSimulationOutput[] output);
    /// <summary>Never waits. Returns null while the GPU still owns the ticket; otherwise completes it once and transfers ownership of its immutable GPU buffer.</summary>
    ParticleSimulationBuffer? TryComplete(long ticket);
    /// <summary>Relinquishes a ticket immediately. Its buffers remain owned by the backend until GPU completion.</summary>
    void Release(long ticket);
    /// <summary>Relinquishes all pending tickets at a world or feature transition, without waiting.</summary>
    void DiscardPending();
}
