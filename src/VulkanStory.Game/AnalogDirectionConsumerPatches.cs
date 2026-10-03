using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using VulkanStory.Input;

namespace VulkanStory.Game;

// Retained direction application after the original control-vector calculation.
internal static class AnalogDirectionConsumerPatches
{
    private const string Owner = "vulkanstory.routing.controller-analog-direction";
    private static System.Func<bool>? enabled;
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
