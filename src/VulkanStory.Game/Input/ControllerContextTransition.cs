using OpenTK.Mathematics;

namespace VulkanStory.Game.Input;

// Synthetic controls entering another context remain blocked until released.
internal sealed class ControllerContextTransition
{
    private bool initialized, world;
    private object? owner;
    private uint blockedButtons;
    private bool primaryBlocked, secondaryBlocked, moveBlocked, lookBlocked;

    internal bool Update(bool inWorld, object? foreground, uint buttons, bool primary, bool secondary,
        Vector2 move, Vector2 look)
    {
        bool changed = initialized && (world != inWorld || !ReferenceEquals(owner, foreground));
        initialized = true;
        world = inWorld;
        owner = foreground;
        if (changed)
        {
            blockedButtons |= buttons;
            primaryBlocked |= primary;
            secondaryBlocked |= secondary;
            moveBlocked |= move != Vector2.Zero;
            lookBlocked |= look != Vector2.Zero;
        }
        blockedButtons &= buttons;
        primaryBlocked &= primary;
        secondaryBlocked &= secondary;
        moveBlocked &= move != Vector2.Zero;
        lookBlocked &= look != Vector2.Zero;
        return changed;
    }

    internal uint Buttons(uint raw) => raw & ~blockedButtons;
    internal bool Primary(bool raw) => raw && !primaryBlocked;
    internal bool Secondary(bool raw) => raw && !secondaryBlocked;
    internal Vector2 Movement(Vector2 raw) => moveBlocked ? Vector2.Zero : raw;
    internal Vector2 Look(Vector2 raw) => lookBlocked ? Vector2.Zero : raw;
    internal void Reset()
    {
        // Drop the old GUI/world owner and force release-to-resume on the next sample.
        initialized = true;
        owner = this;
        blockedButtons = 0;
        primaryBlocked = secondaryBlocked = moveBlocked = lookBlocked = false;
    }
}
