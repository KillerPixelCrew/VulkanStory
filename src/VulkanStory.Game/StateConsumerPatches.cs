using System.Reflection;
using HarmonyLib;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal static class StateConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-state";
    private static Func<bool>? routingEnabled;
    private sealed record Binding(string Target, Type Result, Type[] Parameters, string Prefix);
    private static readonly Binding[] Profile1227 =
    [
        new("GlViewport", typeof(void), [typeof(int), typeof(int), typeof(int), typeof(int)], nameof(Viewport)),
        new("GlScissor", typeof(void), [typeof(int), typeof(int), typeof(int), typeof(int)], nameof(Scissor)),
        new("GlScissorFlag", typeof(void), [typeof(bool)], nameof(ScissorFlag)),
        new("get_GlScissorFlagEnabled", typeof(bool), [], nameof(ScissorEnabled)),
        new("GlEnableDepthTest", typeof(void), [], nameof(DepthOn)),
        new("GlDisableDepthTest", typeof(void), [], nameof(DepthOff)),
        new("GlDepthMask", typeof(void), [typeof(bool)], nameof(DepthMask)),
        new("GlDepthFunc", typeof(void), [typeof(EnumDepthFunction)], nameof(DepthFunction)),
        new("GlEnableCullFace", typeof(void), [], nameof(CullOn)),
        new("GlDisableCullFace", typeof(void), [], nameof(CullOff)),
        new("GlCullFaceBack", typeof(void), [], nameof(CullBack)),
        new("GlCullFaceFront", typeof(void), [], nameof(CullFront)),
        new("GlColorMask", typeof(void), [typeof(bool), typeof(bool), typeof(bool), typeof(bool)], nameof(ColorMask)),
        new("GLLineWidth", typeof(void), [typeof(float)], nameof(LineWidth)),
        new("GLWireframes", typeof(void), [typeof(bool)], nameof(Wireframes)),
        new("GlToggleBlend", typeof(void), [typeof(bool), typeof(EnumBlendMode)], nameof(Blend)),
        new("SmoothLines", typeof(void), [typeof(bool)], nameof(Smooth)),
        new("GlEnableStencilTest", typeof(void), [], nameof(StencilOn)),
        new("GlDisableStencilTest", typeof(void), [], nameof(StencilOff)),
    ];

    internal static void ValidateBindings() { foreach (var binding in Profile1227) Resolve(binding); }

    // This subset is not a complete mandatory graphics-api group and cannot
    // satisfy StartupRoutingTransaction.RequiredGroups by itself.
    internal static StartupPatchGroup CreateSubset(Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var harmony = new Harmony(Owner);
        (MethodInfo Original, MethodInfo Prefix)[]? methods = null;
        bool attempted = false;
        return new StartupPatchGroup("graphics-states", () =>
        {
            methods = Profile1227.Select(binding => (Resolve(binding),
                typeof(StateConsumerPatches).GetMethod(binding.Prefix,
                    BindingFlags.NonPublic | BindingFlags.Static)!)).ToArray();
        }, () =>
        {
            if (methods is null) throw new InvalidOperationException("State targets were not validated.");
            if (routingEnabled != null) throw new InvalidOperationException("State routing already has an owner.");
            routingEnabled = enabled;
            attempted = true;
            foreach (var method in methods)
                harmony.Patch(method.Original, prefix: new HarmonyMethod(method.Prefix) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(routingEnabled, enabled)) routingEnabled = null;
            attempted = false;
        });
    }

    private static MethodInfo Resolve(Binding binding)
    {
        MethodInfo? method = typeof(ClientPlatformWindows).GetMethod(binding.Target,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: binding.Parameters, modifiers: null);
        if (method is null || method.ReturnType != binding.Result ||
            method.ContainsGenericParameters || method.GetMethodBody() is null)
            throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, binding.Target);
        return method;
    }

    private static bool TryAdapter(ClientPlatformWindows platform, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (routingEnabled?.Invoke() != true) return false;
        if (!GameGraphicsAdapter.TryGet(platform, out var found) || found is null)
            throw new InvalidOperationException("Active graphics routing has no renderer adapter for this platform.");
        adapter = found;
        return true;
    }

    private static bool Viewport(ClientPlatformWindows __instance, int __0, int __1, int __2, int __3)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        adapter.RequireStatedState().Viewport = new Rect2D(new Offset2D(__0, __1), new Extent2D((uint)Math.Max(0, __2), (uint)Math.Max(0, __3)));
        return false;
    }
    private static bool Scissor(ClientPlatformWindows __instance, int __0, int __1, int __2, int __3)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        int x = Math.Max(0, __0), y = Math.Max(0, __1);
        adapter.RequireStatedState().Scissor = new Rect2D(new Offset2D(x, y),
            new Extent2D((uint)Math.Max(0, __2 - (x - __0)), (uint)Math.Max(0, __3 - (y - __1))));
        return false;
    }
    private static bool ScissorFlag(ClientPlatformWindows __instance, bool __0)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().ScissorEnabled = __0; return false; }
    private static bool ScissorEnabled(ClientPlatformWindows __instance, ref bool __result)
    { if (!TryAdapter(__instance, out var a)) return true; __result = a.RequireStatedState().ScissorEnabled; return false; }
    private static bool DepthOn(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().DepthTest = true; return false; }
    private static bool DepthOff(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().DepthTest = false; return false; }
    private static bool DepthMask(ClientPlatformWindows __instance, bool __0)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().DepthWrite = __0; return false; }
    private static bool DepthFunction(ClientPlatformWindows __instance, EnumDepthFunction __0)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().DepthCompare = GlEnums.CompareOpFrom((int)__0); return false; }
    private static bool CullOn(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().CullEnabled = true; return false; }
    private static bool CullOff(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().CullEnabled = false; return false; }
    private static bool CullBack(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().CullBack = true; return false; }
    private static bool CullFront(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().CullBack = false; return false; }
    private static bool ColorMask(ClientPlatformWindows __instance, bool __0, bool __1, bool __2, bool __3)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().SetColorMask(__0, __1, __2, __3); return false; }
    private static bool LineWidth(ClientPlatformWindows __instance, float __0)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().LineWidth = __0; return false; }
    private static bool Wireframes(ClientPlatformWindows __instance, bool __0)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().Wireframe = __0; return false; }
    private static bool Blend(ClientPlatformWindows __instance, bool __0, EnumBlendMode __1)
    { if (!TryAdapter(__instance, out var a)) return true; a.ToggleBlend(__0, __1); return false; }
    private static bool Smooth(ClientPlatformWindows __instance, bool __0)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState(); return false; }
    private static bool StencilOn(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().StencilTest = true; return false; }
    private static bool StencilOff(ClientPlatformWindows __instance)
    { if (!TryAdapter(__instance, out var a)) return true; a.RequireStatedState().StencilTest = false; return false; }
}