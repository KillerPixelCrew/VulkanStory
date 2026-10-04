using System.Collections.Generic;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VulkanStory.Game.Input;

internal enum ControllerInventoryAction { Select, TakeHalf, QuickMove }

internal static class ControllerInventoryActions
{
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
