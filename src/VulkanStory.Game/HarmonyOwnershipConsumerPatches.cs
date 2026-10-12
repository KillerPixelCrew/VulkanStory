using System.Reflection;
using HarmonyLib;

namespace VulkanStory.Game;

/// <summary>Keeps legacy instance cleanup scoped to that instance while Vulkan owns the game API.</summary>
internal static class HarmonyOwnershipConsumerPatches
{
    private const string Owner = "vulkanstory.routing.harmony-ownership";
    private static Func<bool>? enabled;

    internal static StartupPatchGroup CreateGroup(Func<bool> routing)
    {
        MethodInfo target = AccessTools.DeclaredMethod(typeof(Harmony), nameof(Harmony.UnpatchAll), [typeof(string)])
            ?? throw new MissingMethodException(typeof(Harmony).FullName, nameof(Harmony.UnpatchAll));
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-harmony-ownership", () =>
        {
            if (target.Module.ModuleVersionId != new Guid("3380bfcb-666d-42a3-898c-a1837034948f") ||
                target.IsStatic || target.ReturnType != typeof(void) || target.GetMethodBody() == null)
                throw new InvalidOperationException("Patch ownership requires the official Vintage Story 1.22.7 Harmony profile.");
        }, () =>
        {
            if (enabled != null) throw new InvalidOperationException("Patch cleanup already has a routing owner.");
            enabled = routing; attempted = true;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HarmonyOwnershipConsumerPatches), nameof(ScopeCleanup))
                { priority = Priority.Last });
        }, () =>
        {
            if (!attempted) return;
            // Explicit owners retain Harmony's original behavior, including our own rollback.
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(enabled, routing)) enabled = null;
            attempted = false;
        });
    }

    private static void ScopeCleanup(Harmony __instance, ref string? __0)
    {
        if (__0 != null || enabled?.Invoke() != true) return;
        // A null argument on the legacy instance API removes every owner's patches.
        // Use the caller's own ID, like UnpatchSelf, so ordinary mod/world disposal
        // cannot discard the native renderer or another mod's still-live hooks.
        __0 = __instance.Id;
        Console.WriteLine("VulkanStory: scoped legacy patch cleanup to owner '" + __0 + "'.");
    }
}
