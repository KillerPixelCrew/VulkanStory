using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace VulkanStory.Game.Input;

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
            if (across >= along * 2f) continue;
            float score = along + 4f * across;
            if (score >= bestScore) continue;
            bestScore = score;
            next = target;
        }
        if (float.IsFinite(bestScore)) return true;
        // At an edge, wrap within the best aligned row/column.
        float alignment = float.PositiveInfinity;
        float edge = float.PositiveInfinity;
        foreach (Vector2 target in targets)
        {
            if ((target - origin).LengthSquared < 64f) continue;
            float across = MathF.Abs((target.X - origin.X) * direction.Y - (target.Y - origin.Y) * direction.X);
            alignment = Math.Min(alignment, across);
        }
        foreach (Vector2 target in targets)
        {
            if ((target - origin).LengthSquared < 64f) continue;
            float across = MathF.Abs((target.X - origin.X) * direction.Y - (target.Y - origin.Y) * direction.X);
            float along = target.X * direction.X + target.Y * direction.Y;
            if (across > alignment + 8f || along >= edge) continue;
            edge = along;
            next = target;
        }
        return float.IsFinite(edge);
    }
}
