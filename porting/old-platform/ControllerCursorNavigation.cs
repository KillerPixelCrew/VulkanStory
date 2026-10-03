using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace Optimum.Render.Vulkan.Platform;

internal static class ControllerCursorNavigation
{
    /// <summary>Pick the nearest useful UI target in a cardinal direction.</summary>
    public static bool TryNext(Vector2 origin, Vector2 direction, IReadOnlyList<Vector2> targets, out Vector2 next)
    {
        float bestScore = float.PositiveInfinity;
        next = default;
        foreach (Vector2 target in targets)
        {
            float dx = target.X - origin.X;
            float dy = target.Y - origin.Y;
            float along = dx * direction.X + dy * direction.Y;
            if (along <= 8f) continue;
            float across = MathF.Abs(dx * direction.Y - dy * direction.X);
            float score = along + 2.25f * across;
            if (score >= bestScore) continue;
            bestScore = score;
            next = target;
        }
        return float.IsFinite(bestScore);
    }
}
