using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VulkanStory.Game.Performance;

/// <summary>Merges settled drops during their first eight seconds, using the game's automatic stack rules.</summary>
/// <remarks>Inspired by telleznilsson/RAM-Garbage-Collector. Runs in the integrated server's existing entity tick,
/// never removes entries from its entity enumeration, and retains no strong references to drops or worlds.</remarks>
internal static class EarlyItemMerge
{
    private sealed class ItemState { internal long NextAttempt; }
    private sealed class WorldBudget { internal long Window, WorkTicks; internal int Searches; }
    private static ConditionalWeakTable<EntityItem, ItemState> items = new();
    private static ConditionalWeakTable<IWorldAccessor, WorldBudget> worlds = new();
    private static MethodInfo Target() => AccessTools.Method(typeof(EntityItem), nameof(EntityItem.OnGameTick), [typeof(float)])
        ?? throw new MissingMethodException(nameof(EntityItem), nameof(EntityItem.OnGameTick));

    internal static PerformanceFeature Create() => new("early-item-merge", s => s.EarlyItemMerge,
        () =>
        {
            _ = Target();
            _ = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.TryMergeStacks), [typeof(ItemStackMergeOperation)])
                ?? throw new MissingMethodException(nameof(CollectibleObject), nameof(CollectibleObject.TryMergeStacks));
            _ = AccessTools.Method(typeof(ItemSlot), nameof(ItemSlot.TryPutInto), [typeof(IWorldAccessor), typeof(ItemSlot), typeof(int)])
                ?? throw new MissingMethodException(nameof(ItemSlot), nameof(ItemSlot.TryPutInto));
        }, harmony => harmony.Patch(Target(), postfix: new HarmonyMethod(typeof(EarlyItemMerge), nameof(Tick))),
        Uninstall: Reset, WorldLeft: Reset);

    private static void Reset() { items = new(); worlds = new(); }
    private static bool Settled(EntityItem item) => item.Alive && !item.ShouldDespawn && !item.IsOnFire &&
        !item.InLava && !item.Swimming && item.OnGround &&
        Math.Abs(item.Pos.Motion.X) < .055 && Math.Abs(item.Pos.Motion.Y) < .055 && Math.Abs(item.Pos.Motion.Z) < .055;

    private static void Tick(EntityItem __instance)
    {
        EntityItem source = __instance;
        if (PerformancePatchSet.Current?.EarlyItemMerge != true || source.World?.Side != EnumAppSide.Server ||
            source.GetType() != typeof(EntityItem) || !source.Alive || source.Itemstack?.Collectible is not { MaxStackSize: > 1 }) return;
        long now = source.World.ElapsedMilliseconds, age = now - source.itemSpawnedMilliseconds;
        if (age < 300 || age > 8000) return;
        ItemState state = items.GetOrCreateValue(source);
        if (now < state.NextAttempt || !Settled(source)) return;
        WorldBudget budget = worlds.GetOrCreateValue(source.World);
        long window = now / 100;
        if (window != budget.Window) { budget.Window = window; budget.Searches = 0; budget.WorkTicks = 0; }
        if (budget.Searches >= 16 || budget.WorkTicks >= Stopwatch.Frequency / 500) return;
        state.NextAttempt = now + 250;
        budget.Searches++;
        long start = Stopwatch.GetTimestamp();
        try
        {
            Interlocked.Increment(ref PerformanceCounters.ItemMergeSearches);
            Entity[] nearby = source.World.GetEntitiesAround(source.Pos.XYZ, 2f, 1.5f,
                entity => entity is EntityItem && entity.EntityId != source.EntityId);
            int examined = 0;
            foreach (Entity entity in nearby)
            {
                // Bound work even in unusually dense modded piles. Deferred drops retry on later ticks.
                if (++examined > 64 || (examined > 8 &&
                    Stopwatch.GetTimestamp() - start + budget.WorkTicks >= Stopwatch.Frequency / 500)) break;
                if (source.Slot.Empty || !source.Alive) break;
                if (entity is not EntityItem target || target.GetType() != typeof(EntityItem) || !Settled(target) ||
                    target.Pos.Dimension != source.Pos.Dimension || now - target.itemSpawnedMilliseconds < 300 ||
                    !string.Equals(source.ByPlayerUid, target.ByPlayerUid, StringComparison.Ordinal) ||
                    target.Itemstack?.Collectible is not { MaxStackSize: > 1 } collectible ||
                    target.Slot.StackSize >= collectible.MaxStackSize ||
                    !target.Itemstack.Equals(source.World, source.Itemstack)) continue;
                int moved = source.Slot.TryPutInto(source.World, target.Slot, source.Slot.StackSize);
                if (moved <= 0) continue;
                target.Itemstack = target.Slot.Itemstack;
                target.WatchedAttributes.MarkPathDirty("itemstack");
                // Combining with an older drop must not bypass the newer drop's pickup delay.
                target.itemSpawnedMilliseconds = Math.Max(target.itemSpawnedMilliseconds, source.itemSpawnedMilliseconds);
                source.Itemstack = source.Slot.Itemstack;
                source.WatchedAttributes.MarkPathDirty("itemstack");
                Interlocked.Increment(ref PerformanceCounters.ItemStacksMerged);
                Interlocked.Add(ref PerformanceCounters.ItemUnitsMerged, moved);
                if (source.Slot.Empty)
                {
                    // The original server tick removes dead entities after finishing its enumeration.
                    source.Die(EnumDespawnReason.Removed);
                    Interlocked.Increment(ref PerformanceCounters.ItemEntitiesMerged);
                    break;
                }
            }
        }
        finally { budget.WorkTicks += Stopwatch.GetTimestamp() - start; }
    }
}
