using System.Collections.Generic;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VulkanStory.Game.Input;

/// <summary>Semantic slot operations routed through the game's official inventory grid.</summary>
internal enum ControllerInventoryAction { Select, TakeHalf, QuickMove }

/// <summary>Controller cursor-to-slot dispatch that preserves grid permissions and vanilla packet generation.</summary>
internal static class ControllerInventoryActions
{
    /// <summary>Dispatches the first slot containing the cursor using select, half-stack, or quick-transfer semantics.</summary>
    /// <returns>True when a slot consumed the operation, including a denied slot; false when no target contains the cursor.</returns>
    internal static bool TryClick(ICoreClientAPI api, IReadOnlyList<ControllerSlotTarget> targets,
        Vector2 cursor, ControllerInventoryAction action)
    {
        foreach (ControllerSlotTarget target in targets)
        {
            if (!target.Bounds.PointInside(cursor.X, cursor.Y)) continue;
            // Match the official grid's permission check, including filtered-slot index.
            if (target.Grid.CanClickSlot?.Invoke(target.Index) == false) return true;
            target.Grid.tabbedSlotId = target.Index;
            target.Grid.HighlightSlot(target.SlotId);
            target.Grid.SlotClick(api, target.SlotId,
                action == ControllerInventoryAction.TakeHalf ? EnumMouseButton.Right : EnumMouseButton.Left,
                action == ControllerInventoryAction.QuickMove, false, false);
            return true;
        }
        return false;
    }
}
