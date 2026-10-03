using System.Linq;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Datastructures;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class ControllerDamageHapticsTests
{
    [Fact]
    public void DamageListenerFollowsPlayerAttributesAndDropsOldWorldPulse()
    {
        using var input = new SdlGamepadInput(new VulkanClientPlatform(null!));
        var first = new SyncedTreeAttribute();
        var second = new SyncedTreeAttribute();
        input.BindDamageAttributes(first);
        input.BindDamageAttributes(first);
        Assert.Single(first.OnModified.Where(listener => listener.path == "onHurt"));
        first.SetFloat("onHurt", 5);
        first.MarkPathDirty("onHurt");
        Assert.Equal(5f, input.PendingDamageRumbleForTests);

        input.BindDamageAttributes(second);
        Assert.Empty(first.OnModified.Where(listener => listener.path == "onHurt"));
        Assert.Equal(0f, input.PendingDamageRumbleForTests);
        second.SetFloat("onHurt", 3);
        second.MarkPathDirty("onHurt");
        Assert.Equal(3f, input.PendingDamageRumbleForTests);

        input.BindDamageAttributes(null);
        Assert.Empty(second.OnModified.Where(listener => listener.path == "onHurt"));
    }
}
