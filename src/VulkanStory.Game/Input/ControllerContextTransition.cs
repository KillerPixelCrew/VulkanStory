using OpenTK.Mathematics;

namespace VulkanStory.Game.Input;

// Synthetic controls entering another context remain blocked until released.
/// <summary>Suppresses held synthetic controls across world/foreground changes until their physical release.</summary>
internal sealed class ControllerContextTransition
{
    private bool initialized, world;
    private object? owner;
    private uint blockedButtons;
    private bool primaryBlocked, secondaryBlocked, moveBlocked, lookBlocked;

    /// <summary>Records the current owner and updates release-to-resume masks when the input context changes.</summary>
    /// <param name="inWorld">Whether the sample targets gameplay rather than GUI navigation.</param>
    /// <param name="foreground">Current foreground owner, compared by reference identity.</param>
    /// <param name="buttons">Current held-button bitmask.</param>
    /// <param name="primary">Current primary trigger action.</param>
    /// <param name="secondary">Current secondary trigger action.</param>
    /// <param name="move">Current processed movement axes.</param>
    /// <param name="look">Current processed look axes.</param>
    /// <returns>True when an already initialized owner or world mode changed.</returns>
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

    /// <summary>Removes buttons awaiting release in the new context.</summary>
    internal uint Buttons(uint raw) => raw & ~blockedButtons;
    /// <summary>Filters a primary trigger awaiting release.</summary>
    internal bool Primary(bool raw) => raw && !primaryBlocked;
    /// <summary>Filters a secondary trigger awaiting release.</summary>
    internal bool Secondary(bool raw) => raw && !secondaryBlocked;
    /// <summary>Suppresses movement until the transition's held stick returns to zero.</summary>
    internal Vector2 Movement(Vector2 raw) => moveBlocked ? Vector2.Zero : raw;
    /// <summary>Suppresses look until the transition's held stick returns to zero.</summary>
    internal Vector2 Look(Vector2 raw) => lookBlocked ? Vector2.Zero : raw;
    /// <summary>Drops the old owner so the next sample performs a release-to-resume transition.</summary>
    internal void Reset()
    {
        // Drop the old GUI/world owner and force release-to-resume on the next sample.
        initialized = true;
        owner = this;
        blockedButtons = 0;
        primaryBlocked = secondaryBlocked = moveBlocked = lookBlocked = false;
    }
}
