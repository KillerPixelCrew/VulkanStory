using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace VulkanStory.Input;

/// <summary>Negotiated, bounded controller movement carried by player entity packets.</summary>
public static class AnalogMovement
{
    public const int PacketId = 0x4F505441;
    public const int ProbePacketId = 0x4F505442;
    public const int AckPacketId = 0x4F505443;
    public const byte ProtocolVersion = 1;
    private const long MaxAgeMs = 600;

    private sealed class Axes
    {
        public float X;
        public float Y;
        public long ExpiresAt;
    }

    private static readonly ConditionalWeakTable<EntityControls, Axes> ActiveAxes = new();

    public static float EffectiveFactor(float controllerFactor, bool physicalMovementHeld, bool serverSupportsAnalog)
    {
        if (!serverSupportsAnalog || physicalMovementHeld || !float.IsFinite(controllerFactor)) return 1f;
        return Math.Clamp(controllerFactor, 0f, 1f);
    }

    public static byte Encode(float factor) =>
        (byte)Math.Clamp((int)MathF.Round(Math.Clamp(float.IsFinite(factor) ? factor : 1f, 0f, 1f) * 255f), 0, 255);

    public static float Decode(byte encoded) => encoded / 255f;

    public static byte EncodeAxis(float axis) => (byte)(sbyte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(float.IsFinite(axis) ? axis : 0f, -1f, 1f) * 127f), -127, 127);

    public static float DecodeAxis(byte encoded) => (sbyte)encoded / 127f;

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

    public static void ClearAxes(EntityControls controls)
    {
        if (controls != null) ActiveAxes.Remove(controls);
    }

    /// <summary>Called after vanilla calculates vectors; only active player controls change.</summary>
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
