using System;
using OpenTK.Mathematics;

namespace VulkanStory.Game.Input;

/// <summary>Radial deadzone and response-curve processing shared by controller movement and look.</summary>
internal static class ControllerStickProcessing
{
    /// <summary>Preserves direction while remapping radial magnitude between coupled inner/outer deadzones.</summary>
    /// <param name="raw">Raw normalized axes; nonfinite components return zero.</param>
    /// <param name="inner">Inner radius, clamped to zero through 0.8.</param>
    /// <param name="outer">Outer saturation band, bounded by 0.2 and 0.95 minus the inner radius.</param>
    /// <param name="exponent">Response exponent, clamped to 0.5 through three.</param>
    /// <returns>A bounded radial vector, or zero inside the inner deadzone.</returns>
    internal static Vector2 Process(Vector2 raw, float inner, float outer, float exponent = 1f)
    {
        if (!float.IsFinite(raw.X) || !float.IsFinite(raw.Y)) return Vector2.Zero;
        raw = new Vector2(Math.Clamp(raw.X, -1f, 1f), Math.Clamp(raw.Y, -1f, 1f));
        inner = Math.Clamp(inner, 0f, 0.8f);
        outer = Math.Clamp(outer, 0f, Math.Min(0.2f, 0.95f - inner));
        float length = raw.Length;
        if (length <= inner) return Vector2.Zero;
        float magnitude = Math.Clamp((length - inner) / (1f - inner - outer), 0f, 1f);
        magnitude = MathF.Pow(magnitude, Math.Clamp(exponent, 0.5f, 3f));
        return raw * (magnitude / length);
    }
}
