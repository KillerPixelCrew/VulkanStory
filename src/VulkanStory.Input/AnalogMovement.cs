using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace VulkanStory.Input;

/// <summary>Negotiated, bounded controller movement carried by player entity packets.</summary>
public static class AnalogMovement
{
    /// <summary>Player-entity packet carrying the quantized movement factor and optional signed axes.</summary>
    public const int PacketId = 0x4F505441;
    /// <summary>Player-entity packet requesting analog protocol negotiation.</summary>
    public const int ProbePacketId = 0x4F505442;
    /// <summary>Server reply acknowledging the negotiated analog protocol.</summary>
    public const int AckPacketId = 0x4F505443;
    /// <summary>Version byte required by the companion's negotiation packet.</summary>
    public const byte ProtocolVersion = 1;
    /// <summary>Lifetime in milliseconds of one received analog sample before both sides fall back to vanilla movement.</summary>
    public const long MaxAgeMs = 600;

    /// <summary>Weakly associated control state whose axis sample expires after the bounded input window.</summary>
    private sealed class Axes
    {
        public float X;
        public float Y;
        public long ExpiresAt;
    }

    private static readonly ConditionalWeakTable<EntityControls, Axes> ActiveAxes = new();

    /// <summary>Selects a bounded analog speed factor only when the server supports it and physical movement is idle.</summary>
    /// <param name="controllerFactor">Requested normalized stick speed.</param>
    /// <param name="physicalMovementHeld">Whether keyboard/physical movement takes precedence.</param>
    /// <param name="serverSupportsAnalog">Whether this connection acknowledged the analog protocol.</param>
    /// <returns>A factor in zero through one; one for digital fallback, physical precedence, or nonfinite input.</returns>
    public static float EffectiveFactor(float controllerFactor, bool physicalMovementHeld, bool serverSupportsAnalog)
    {
        if (!serverSupportsAnalog || physicalMovementHeld || !float.IsFinite(controllerFactor)) return 1f;
        return Math.Clamp(controllerFactor, 0f, 1f);
    }

    /// <summary>Quantizes a speed factor to an unsigned byte; nonfinite input uses full speed.</summary>
    public static byte Encode(float factor) =>
        (byte)(int)MathF.Round(Math.Clamp(float.IsFinite(factor) ? factor : 1f, 0f, 1f) * 255f);

    /// <summary>Decodes an unsigned speed factor to zero through one.</summary>
    public static float Decode(byte encoded) => encoded / 255f;

    /// <summary>Quantizes a clamped signed axis to signed-byte bits; nonfinite input uses zero.</summary>
    public static byte EncodeAxis(float axis) => (byte)(sbyte)(int)MathF.Round(
        Math.Clamp(float.IsFinite(axis) ? axis : 0f, -1f, 1f) * 127f);

    /// <summary>Interprets signed-byte axis bits using the protocol's 127-step scale.</summary>
    /// <remarks>Protocol-generated values exclude -128; arbitrary byte 128 decodes slightly below -1.</remarks>
    public static float DecodeAxis(byte encoded) => (sbyte)encoded / 127f;

    /// <summary>Associates a bounded axis sample with controls for <see cref="MaxAgeMs"/>, clearing invalid or zero input.</summary>
    /// <param name="controls">Control object receiving the weakly associated state; null is ignored.</param>
    /// <param name="x">Horizontal movement axis, clamped to minus one through one.</param>
    /// <param name="y">Forward/back movement axis, clamped to minus one through one.</param>
    public static void SetAxes(EntityControls controls, float x, float y)
    {
        if (controls == null) return;
        if (!float.IsFinite(x) || !float.IsFinite(y) || (x == 0f && y == 0f))
        {
            ClearAxes(controls);
            return;
        }
        Axes state = ActiveAxes.GetValue(controls, _ => new Axes());
        lock (state)
        {
            state.X = Math.Clamp(x, -1f, 1f);
            state.Y = Math.Clamp(y, -1f, 1f);
            state.ExpiresAt = Environment.TickCount64 + MaxAgeMs;
        }
    }

    /// <summary>Removes analog direction state from controls; null is ignored.</summary>
    public static void ClearAxes(EntityControls controls)
    {
        if (controls != null) ActiveAxes.Remove(controls);
    }

    /// <summary>Called after vanilla calculates vectors; only active player controls change.</summary>
    /// <param name="controls">Controls whose walk/fly vectors may be overwritten by a live analog sample.</param>
    /// <param name="pos">Player orientation supplying the yaw/pitch transform.</param>
    /// <param name="dt">Movement timestep used with the game speed multipliers.</param>
    /// <remarks>Expired, idle, or absent samples preserve vanilla vectors; fly-plane locks are retained.</remarks>
    public static void ApplyDirection(EntityControls controls, EntityPos pos, float dt)
    {
        if (!ActiveAxes.TryGetValue(controls, out Axes? state) || !controls.TriesToMove) return;
        float x, y;
        lock (state)
        {
            if (Environment.TickCount64 > state.ExpiresAt) return;
            x = state.X;
            y = state.Y;
        }
        double length = Math.Sqrt((double)x * x + (double)y * y);
        if (length <= 0.0001) return;
        double moveSpeed = dt * GlobalConstants.BaseMoveSpeed * controls.MovespeedMultiplier *
            GlobalConstants.OverallSpeedMultiplier;
        double dx = -x / length * moveSpeed;
        double dz = -y / length * moveSpeed;
        double cosPitch = Math.Cos(pos.Pitch);
        double sinPitch = Math.Sin(pos.Pitch);
        double cosYaw = Math.Cos(-pos.Yaw);
        double sinYaw = Math.Sin(-pos.Yaw);
        controls.WalkVector.Set(dx * cosYaw - dz * sinYaw, 0,
            dx * sinYaw + dz * cosYaw);
        if (controls.FlyPlaneLock == EnumFreeMovAxisLock.Y) cosPitch = -1;
        controls.FlyVector.Set(dx * cosYaw + dz * cosPitch * sinYaw,
            dz * sinPitch, dx * sinYaw - dz * cosPitch * cosYaw);
        if (controls.FlyPlaneLock == EnumFreeMovAxisLock.X) controls.FlyVector.X = 0;
        if (controls.FlyPlaneLock == EnumFreeMovAxisLock.Y) controls.FlyVector.Y = 0;
        if (controls.FlyPlaneLock == EnumFreeMovAxisLock.Z) controls.FlyVector.Z = 0;
    }
}
