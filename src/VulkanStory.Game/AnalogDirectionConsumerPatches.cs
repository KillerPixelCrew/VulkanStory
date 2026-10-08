using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using VulkanStory.Input;

namespace VulkanStory.Game;

// Retained direction application after the original control-vector calculation.
/// <summary>Supplies negotiated analog direction to the original entity movement consumer while preserving digital fallback.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class AnalogDirectionConsumerPatches
{
    private const string Owner = "vulkanstory.routing.controller-analog-direction";
    private static System.Func<bool>? enabled;
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="routing">Predicate read by routed callbacks after the complete startup transaction commits.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(System.Func<bool> routing)
    {
        MethodInfo target = typeof(EntityControls).GetMethod(nameof(EntityControls.CalcMovementVectors),
            [typeof(EntityPos), typeof(float)]) ?? throw new MissingMethodException("Original control-vector method is missing.");
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-controller-analog-direction", () =>
        {
            if (target.ReturnType != typeof(void) || !target.IsVirtual || target.GetMethodBody() == null)
                throw new InvalidOperationException("Original control-vector method changed.");
        }, () =>
        {
            if (enabled != null) throw new InvalidOperationException("Analog direction already has a routing owner.");
            enabled = routing; attempted = true;
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(AnalogDirectionConsumerPatches), nameof(Apply)) { priority = Priority.Last });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(enabled, routing)) enabled = null;
            attempted = false;
        });
    }
    private static void Apply(EntityControls __instance, EntityPos __0, float __1)
    {
        if (enabled?.Invoke() == true) AnalogMovement.ApplyDirection(__instance, __0, __1);
    }
}
