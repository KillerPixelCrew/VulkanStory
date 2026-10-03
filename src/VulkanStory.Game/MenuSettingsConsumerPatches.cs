using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class MenuSettingsConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-menu-settings";
    private static Func<bool>? enabled;
    private static readonly MethodInfo Header = AccessTools.Method(typeof(GuiCompositeSettings), "ComposerHeader", [typeof(string), typeof(string)]);
    private static readonly Type Escape = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogEscapeMenu");
    internal static StartupPatchGroup CreateGroup(Func<bool> routing)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-menu-settings", () =>
        {
            if (Header == null || Header.ReturnType != typeof(GuiComposer) || Header.GetMethodBody() == null || Escape == null)
                throw new InvalidOperationException("Original Options settings profile changed.");
            OptionsSettingsOwner.ValidateProfile();
            foreach (var (target, _, _) in Patches())
                if (target == null || target.GetMethodBody() == null)
                    throw new InvalidOperationException("Original Options host lifecycle changed.");
        }, () =>
        {
            if (enabled != null) throw new InvalidOperationException("Options settings already have an owner.");
            enabled = routing; attempted = true;
            foreach (var (target, prefix, postfix) in Patches())
                harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(MenuSettingsConsumerPatches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(MenuSettingsConsumerPatches), postfix));
        }, () =>
        {
            if (!attempted) return;
            OptionsSettingsOwner.ClearAll(); harmony.UnpatchAll(Owner);
            if (ReferenceEquals(enabled, routing)) enabled = null;
            attempted = false;
        });
    }
    private static IEnumerable<(MethodInfo Target, string? Prefix, string? Postfix)> Patches()
    {
        yield return (Header, null, nameof(HeaderComposed));
        yield return (AccessTools.Method(typeof(GuiScreen), "RenderToDefaultFramebuffer", [typeof(float)]), nameof(RenderOwner), null);
        yield return (AccessTools.Method(typeof(GuiScreen), "Dispose", []), nameof(CloseOwner), null);
        yield return (AccessTools.Method(Escape, "OnRenderGUI", [typeof(float)]), nameof(RenderOwner), null);
        yield return (AccessTools.Method(Escape, "OnGuiClosed", []), nameof(CloseOwner), null);
        yield return (AccessTools.Method(Escape, "Dispose", []), nameof(CloseOwner), null);
    }
    private static void HeaderComposed(GuiCompositeSettings __instance, string currentTab, GuiComposer __result)
    {
        if (enabled?.Invoke() == true) OptionsSettingsOwner.Get(__instance).AddTab(__result, currentTab);
    }
    private static void RenderOwner(object __instance)
    {
        if (enabled?.Invoke() == true) OptionsSettingsOwner.RenderHost(__instance);
    }
    private static void CloseOwner(object __instance) => OptionsSettingsOwner.CloseHost(__instance);
}
