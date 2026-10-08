using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Remaining public legacy state methods from the retained platform implementation.
/// <summary>Redirects the remaining pinned platform graphics operations, including GUI scaling and pixel readback.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class RemainingPlatformConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-platform-remaining";
    private static Func<bool>? routingEnabled;
    private sealed record Binding(string Name, Type[] Parameters, string Prefix, int GlCalls);
    private static readonly Binding[] Profile =
    [
        new("BindTexture2d", [typeof(int)], nameof(Bind2D), 2),
        new("BindTextureCubeMap", [typeof(int)], nameof(BindCube), 1),
        new("UnBindTextureCubeMap", [], nameof(UnbindCube), 1),
        new("GlGenerateTex2DMipmaps", [], nameof(GenerateMips), 1),
        new("GlStencilMask", [typeof(int)], nameof(StencilMask), 1),
        new("GlStencilFunc", [typeof(int), typeof(int), typeof(int)], nameof(StencilFunction), 1),
        new("GlStencilOp", [typeof(int), typeof(int), typeof(int)], nameof(StencilOperation), 1),
        new("GlClearStencil", [], nameof(ClearStencil), 1),
    ];
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="enabled">Predicate read by routed callbacks after the complete startup transaction commits.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var harmony = new Harmony(Owner); bool attempted = false;
        (MethodInfo Target, MethodInfo Prefix)[]? methods = null;
        return new StartupPatchGroup("graphics-platform-remaining", () =>
        {
            methods = Profile.Select(binding =>
            {
                MethodInfo target = typeof(ClientPlatformWindows).GetMethod(binding.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                    binder: null, types: binding.Parameters, modifiers: null) ??
                    throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, binding.Name);
                if (target.ReturnType != typeof(void) || target.GetMethodBody() == null ||
                    PatchProcessor.GetOriginalInstructions(target).Count(instruction =>
                        instruction.operand is MethodInfo call && call.DeclaringType == typeof(GL)) != binding.GlCalls)
                    throw new InvalidOperationException("Original remaining-platform signature/body changed: " + binding.Name);
                return (target, AccessTools.Method(typeof(RemainingPlatformConsumerPatches), binding.Prefix));
            }).ToArray();
        }, () =>
        {
            if (methods == null) throw new InvalidOperationException("Remaining platform methods were not validated.");
            if (routingEnabled != null) throw new InvalidOperationException("Remaining platform routing already has an owner.");
            routingEnabled = enabled; attempted = true;
            foreach (var method in methods)
                harmony.Patch(method.Target, prefix: new HarmonyMethod(method.Prefix) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(routingEnabled, enabled)) routingEnabled = null;
            attempted = false;
        });
    }
    private static bool TryAdapter(ClientPlatformWindows platform, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (routingEnabled?.Invoke() != true) return false;
        if (!GameGraphicsAdapter.TryGet(platform, out var found) || found == null)
            throw new InvalidOperationException("Active legacy state routing lost its renderer adapter.");
        adapter = found; return true;
    }
    private static bool Bind2D(ClientPlatformWindows __instance, int __0)
    { if (!TryAdapter(__instance, out var graphics)) return true; graphics.BindLegacyTexture2D(__0); return false; }
    private static bool BindCube(ClientPlatformWindows __instance, int __0)
    { if (!TryAdapter(__instance, out var graphics)) return true; graphics.BindLegacyCube(__0); return false; }
    private static bool UnbindCube(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var graphics)) return true; graphics.BindLegacyCube(0); return false; }
    private static bool GenerateMips(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var graphics)) return true; graphics.GenerateLegacyTextureMips(); return false; }
    // Retained behavior: the original client targets have no stencil aspect.
    // Enable/disable state is already tracked by StateConsumerPatches; these
    // commands cannot change nonexistent stencil storage or test results.
    private static bool StencilMask(ClientPlatformWindows __instance, int __0) => !TryAdapter(__instance, out _);
    private static bool StencilFunction(ClientPlatformWindows __instance, int __0, int __1, int __2) => !TryAdapter(__instance, out _);
    private static bool StencilOperation(ClientPlatformWindows __instance, int __0, int __1, int __2) => !TryAdapter(__instance, out _);
    private static bool ClearStencil(ClientPlatformWindows __instance) => !TryAdapter(__instance, out _);
}
