using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Connects original menu and world Graphics pages to the shared VulkanStory Options owner.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class MenuSettingsConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-menu-settings";
    private static Func<bool>? enabled;
    private static readonly MethodInfo Header = AccessTools.Method(typeof(GuiCompositeSettings), "ComposerHeader", [typeof(string), typeof(string)]);
    private static readonly Type Escape = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogEscapeMenu");
    private static readonly MethodInfo Graphics = AccessTools.Method(typeof(GuiCompositeSettings), "OnGraphicsOptions", [typeof(bool)]);
    private static readonly MethodInfo GraphicsValues = AccessTools.Method(typeof(GuiCompositeSettings), "SetGfxValues", []);
    private static readonly Dictionary<MethodInfo, string> SelectionReaders = new()
    {
        [AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.GraphicsPresetId))] = nameof(PresetSelection),
        [AccessTools.Method(typeof(GuiCompositeSettings), "GetWindowModeIndex", [])] = nameof(WindowSelection),
        [AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.VsyncMode))] = nameof(VsyncSelection),
        [AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.OptimizeRamMode))] = nameof(RamSelection),
        [AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.CloudRenderMode))] = nameof(CloudSelection),
        [AccessTools.PropertyGetter(typeof(ClientPlatformAbstract), nameof(ClientPlatformAbstract.WindowBorder))] = nameof(BorderSelection),
    };
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="routing">Predicate read by routed callbacks after the complete startup transaction commits.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
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
            foreach (MethodInfo target in new[] { Graphics, GraphicsValues })
                if (target == null || target.ReturnType != typeof(void) || target.GetMethodBody() == null)
                    throw new InvalidOperationException("Original Graphics dropdown composition changed.");
        }, () =>
        {
            if (enabled != null) throw new InvalidOperationException("Options settings already have an owner.");
            enabled = routing; attempted = true;
            foreach (var (target, prefix, postfix) in Patches())
                harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(MenuSettingsConsumerPatches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(MenuSettingsConsumerPatches), postfix));
            foreach (MethodInfo target in new[] { Graphics, GraphicsValues })
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(MenuSettingsConsumerPatches), nameof(GraphicsSelections)));
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

    // The game's dropdown accepts an unchecked selected index and later indexes Names with it.
    // Normalize only reads made by its Graphics page, including the post-compose refresh;
    // keep saved settings and the rendered scene unchanged until the player chooses a value.
    private static IEnumerable<CodeInstruction> GraphicsSelections(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int readers = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo reader && SelectionReaders.TryGetValue(reader, out string? normalizer))
            {
                readers++;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MenuSettingsConsumerPatches), normalizer));
            }
        }
        int expected = __originalMethod == Graphics ? 6 : 4;
        if (readers != expected)
            throw new InvalidOperationException($"Original Graphics selection reads changed: {__originalMethod.Name} has {readers}, expected {expected}.");
    }

    private static int PresetSelection(int selected)
    {
        if (enabled?.Invoke() != true || (uint)selected < (uint)GraphicsPreset.Presets.Count) return selected;
        // A missing preset does not mean the player's individual graphics values changed.
        return GraphicsPreset.Presets.FindIndex(preset => preset.Langcode == "preset-custom");
    }
    private static int WindowSelection(int selected) => enabled?.Invoke() == true ? Math.Clamp(selected, 0, 3) : selected;
    private static int VsyncSelection(int selected) => enabled?.Invoke() == true ? Math.Clamp(selected, 0, 2) : selected;
    private static int RamSelection(int selected) => enabled?.Invoke() == true ? Math.Clamp(selected, 1, 2) : selected;
    private static int CloudSelection(int selected) => enabled?.Invoke() == true ? Math.Clamp(selected, 0, 2) : selected;
    private static EnumWindowBorder BorderSelection(EnumWindowBorder selected) => enabled?.Invoke() == true
        ? (EnumWindowBorder)Math.Clamp((int)selected, 0, 2) : selected;
}
