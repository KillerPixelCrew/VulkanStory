using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private ClientMain? controllerInventoryWorld;
    private ControllerSlotTarget? controllerInventorySource, controllerInventoryDestination;
    private int controllerInventoryStep, controllerInventoryQuantity;
    private long controllerInventoryNextFrame;

    private void StartControllerInventoryDiagnostic(ClientMain world)
    {
        if (!HeadlessHarnessOptions.Enabled) throw new InvalidOperationException("Inventory diagnostics require the isolated harness.");
        if (!world.api.World.Player.InventoryManager.MouseItemSlot.Empty)
            throw new InvalidOperationException("Inventory diagnostic requires an empty carried slot.");
        OpenDiagnosticOptions(world, "inventory");
        controllerInventoryWorld = world;
        controllerInventoryStep = 0;
        controllerInventoryNextFrame = headlessWorldFrame + 20;
    }

    private void DriveControllerInventoryDiagnosticBeforeInput()
    {
        if (controllerInventoryWorld is not { } world || headlessWorldFrame < controllerInventoryNextFrame) return;
        var api = world.api;
        ItemSlot carried = api.World.Player.InventoryManager.MouseItemSlot;
        List<ControllerSlotTarget> targets = controllers?.CurrentSlotTargets() ?? [];
        if (controllerInventoryStep == 0)
        {
            controllerInventorySource = targets.Where(target =>
                target.Grid.renderedSlots[target.SlotId].StackSize >= 2 && target.Grid.CanClickSlot?.Invoke(target.Index) != false)
                .Select(target => (ControllerSlotTarget?)target).FirstOrDefault();
            if (controllerInventorySource == null) throw new InvalidOperationException("No selectable stack of at least two items found.");
            controllerInventoryQuantity = controllerInventorySource.Value.Grid.renderedSlots[controllerInventorySource.Value.SlotId].StackSize;
        }
        ControllerSlotTarget source = controllerInventorySource!.Value;
        ItemSlot sourceSlot = source.Grid.renderedSlots[source.SlotId];
        switch (controllerInventoryStep)
        {
            case 0:
                ControllerInventoryActions.TryClick(api, targets, source.Center, ControllerInventoryAction.TakeHalf);
                break;
            case 1:
                if (carried.StackSize <= 0 || carried.StackSize >= controllerInventoryQuantity ||
                    carried.StackSize + sourceSlot.StackSize != controllerInventoryQuantity)
                    throw new InvalidOperationException("Take-half did not split the stack without changing its total quantity.");
                ControllerInventoryActions.TryClick(api, targets, source.Center, ControllerInventoryAction.Select);
                break;
            case 2:
                if (!carried.Empty || sourceSlot.StackSize != controllerInventoryQuantity)
                    throw new InvalidOperationException("Select did not restore the split stack.");
                ControllerInventoryActions.TryClick(api, targets, source.Center, ControllerInventoryAction.Select);
                break;
            case 3:
                if (!sourceSlot.Empty || carried.StackSize != controllerInventoryQuantity)
                    throw new InvalidOperationException("Select did not pick up the whole stack.");
                controllerInventoryDestination = targets.Where(target =>
                {
                    ItemSlot slot = target.Grid.renderedSlots[target.SlotId];
                    return !ReferenceEquals(slot.Inventory, sourceSlot.Inventory) && slot.Empty && slot.CanHold(carried) &&
                        target.Grid.CanClickSlot?.Invoke(target.Index) != false;
                }).Select(target => (ControllerSlotTarget?)target).FirstOrDefault();
                if (controllerInventoryDestination == null) throw new InvalidOperationException("No empty receiving inventory slot found.");
                ControllerInventoryActions.TryClick(api, targets, controllerInventoryDestination.Value.Center, ControllerInventoryAction.Select);
                break;
            case 4:
                ControllerSlotTarget destination = controllerInventoryDestination!.Value;
                if (!carried.Empty || destination.Grid.renderedSlots[destination.SlotId].StackSize != controllerInventoryQuantity)
                    throw new InvalidOperationException("Select did not place the stack in the receiving inventory.");
                ControllerInventoryActions.TryClick(api, targets, destination.Center, ControllerInventoryAction.QuickMove);
                break;
            case 5:
                ControllerSlotTarget movedFrom = controllerInventoryDestination!.Value;
                if (!carried.Empty || !movedFrom.Grid.renderedSlots[movedFrom.SlotId].Empty || sourceSlot.StackSize != controllerInventoryQuantity)
                    throw new InvalidOperationException("Quick transfer did not return the stack intact to its original inventory slot.");
                platform.Logger.Notification("[VulkanStory] Controller inventory operations PASS: take-half, select, place and quick transfer; quantity={0}, slots={1}",
                    controllerInventoryQuantity, targets.Count);
                controllerInventoryWorld = null;
                return;
        }
        controllerInventoryStep++;
        controllerInventoryNextFrame = headlessWorldFrame + 20;
    }
}
