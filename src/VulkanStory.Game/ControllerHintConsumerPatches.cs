using System.Reflection;
using System.Runtime.CompilerServices;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.Gui;
using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;

namespace VulkanStory.Game;

internal static class ControllerHintConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-controller-hints";
    private static System.Func<bool>? enabled;
    private static ConditionalWeakTable<GuiCompositeSettings, HintListener> listeners = new();
    private static readonly AccessTools.FieldRef<DrawWorldInteractionUtil, ICoreClientAPI> Api =
        AccessTools.FieldRefAccess<DrawWorldInteractionUtil, ICoreClientAPI>("capi");
    private static readonly AccessTools.FieldRef<GuiCompositeSettings, List<ConfigItem>> MouseItems =
        AccessTools.FieldRefAccess<GuiCompositeSettings, List<ConfigItem>>("mousecontrolItems");
    private static readonly AccessTools.FieldRef<GuiCompositeSettings, List<ConfigItem>> KeyItems =
        AccessTools.FieldRefAccess<GuiCompositeSettings, List<ConfigItem>>("keycontrolItems");
    private static readonly AccessTools.FieldRef<GuiCompositeSettings, int?> Clicked =
        AccessTools.FieldRefAccess<GuiCompositeSettings, int?>("clickedItemIndex");
    private static readonly AccessTools.FieldRef<GuiCompositeSettings, GuiComposer> Composer =
        AccessTools.FieldRefAccess<GuiCompositeSettings, GuiComposer>("composer");
    private static readonly MethodInfo Reload = AccessTools.Method(typeof(GuiCompositeSettings), "ReLoadKeyCombinations", [])!;
    private static readonly Action<GuiCompositeSettings> ReloadSettings = Reload.CreateDelegate<Action<GuiCompositeSettings>>();
    private sealed class HintListener
    {
        // The snapshot holds a weak delegate; the original GUI's sidecar keeps
        // it alive exactly as long as that GUI remains alive.
        private readonly Action changed;
        internal HintListener(GuiCompositeSettings settings)
        {
            var weak = new WeakReference<GuiCompositeSettings>(settings);
            changed = () =>
            {
                if (enabled?.Invoke() != true || !weak.TryGetTarget(out var owner) ||
                    Clicked(owner).HasValue || owner.IsCapturingHotKey || Composer(owner) is not { Composed: true } composer ||
                    composer.GetConfigList("configlist") == null) return;
                ReloadSettings(owner);
            };
            ControllerHints.Subscribe(changed);
        }
    }
    internal static StartupPatchGroup CreateGroup(System.Func<bool> routing)
    {
        ConstructorInfo constructor = typeof(GuiCompositeSettings).GetConstructor([typeof(IGameSettingsHandler), typeof(bool)]) ??
            throw new MissingMethodException("Original settings constructor changed.");
        MethodInfo mouse = AccessTools.Method(typeof(GuiCompositeSettings), "LoadMouseCombinations", [])!;
        MethodInfo keys = AccessTools.Method(typeof(GuiCompositeSettings), "LoadKeyCombinations", [])!;
        MethodInfo draw = AccessTools.Method(typeof(DrawWorldInteractionUtil), "DrawHotkey",
            [typeof(HotKey), typeof(double), typeof(double), typeof(Context), typeof(CairoFont),
                typeof(double), typeof(double), typeof(double), typeof(double), typeof(double[])])!;
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-controller-hints", () =>
        {
            foreach (var method in new[] { mouse, keys, Reload })
                if (method == null || method.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    throw new InvalidOperationException("Original settings hint lifecycle changed.");
            if (constructor.GetMethodBody() == null || draw == null || draw.ReturnType != typeof(double) || draw.GetMethodBody() == null)
                throw new InvalidOperationException("Original interaction hint draw changed.");
        }, () =>
        {
            if (enabled != null) throw new InvalidOperationException("Controller hints already have a routing owner.");
            enabled = routing; attempted = true;
            harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(ControllerHintConsumerPatches), nameof(SettingsCreated)));
            harmony.Patch(mouse, postfix: new HarmonyMethod(typeof(ControllerHintConsumerPatches), nameof(MouseLabels)));
            harmony.Patch(keys, postfix: new HarmonyMethod(typeof(ControllerHintConsumerPatches), nameof(KeyLabels)));
            harmony.Patch(draw, prefix: new HarmonyMethod(typeof(ControllerHintConsumerPatches), nameof(DrawHint)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(enabled, routing)) { enabled = null; listeners = new(); }
            attempted = false;
        });
    }
    private static void SettingsCreated(GuiCompositeSettings __instance)
    { if (enabled?.Invoke() == true) listeners.GetValue(__instance, owner => new HintListener(owner)); }
    private static void Labels(GuiCompositeSettings owner, List<ConfigItem> items)
    {
        if (enabled?.Invoke() != true) return;
        listeners.GetValue(owner, settings => new HintListener(settings));
        foreach (var item in items)
            if (!string.IsNullOrEmpty(item.Code)) item.Key = ControllerHints.LabelFor(item.Code, item.Key);
    }
    private static void MouseLabels(GuiCompositeSettings __instance) => Labels(__instance, MouseItems(__instance));
    private static void KeyLabels(GuiCompositeSettings __instance) => Labels(__instance, KeyItems(__instance));
    private static bool DrawHint(DrawWorldInteractionUtil __instance, HotKey __0, double __1, double __2,
        Context __3, CairoFont __4, double __5, double __6, double __7, double __8, double[] __9, ref double __result)
    {
        if (enabled?.Invoke() != true || ControllerHints.GlyphFor(__0.Code) is not { } glyph) return true;
        __result = DrawControllerGlyph(Api(__instance), glyph, __1, __2, __3, __4, __5, __6, __7, __8, __9);
        return false;
    }
    // Retained DrawWorldInteractionUtil.DrawControllerGlyph, moved out of the game type.
    private static double DrawControllerGlyph(ICoreClientAPI api, string glyph, double x, double y,
        Context ctx, CairoFont font, double lineheight, double textHeight, double pluswidth, double spacing, double[] color)
    {
        if (x > 0)
        {
            api.Gui.Text.DrawTextLine(ctx, font, "+", x + spacing, y + (lineheight - textHeight) / 2 + 2);
            x += pluswidth + 2 * spacing;
        }
        if (ControllerPromptFont.TryDraw(ctx, api, glyph, x, y, lineheight, lineheight, color))
            return x + lineheight + spacing + 1;
        double textWidth = font.GetTextExtents(glyph).Width;
        double width = Math.Max(lineheight, textWidth + GuiElement.scaled(12));
        if (glyph.Length == 1) ctx.Arc(x + width / 2, y + lineheight / 2, lineheight / 2 - 1, 0, Math.PI * 2);
        else GuiElement.RoundRectangle(ctx, x + 1, y + 1, width - 2, lineheight - 2, 5);
        ctx.SetSourceRGBA(color); ctx.LineWidth = 1.5; ctx.StrokePreserve();
        ctx.SetSourceRGBA(color[0], color[1], color[2], color[3] * .45); ctx.Fill();
        api.Gui.Text.DrawTextLine(ctx, font, glyph, x + (width - textWidth) / 2, y + (lineheight - textHeight) / 2 + 2);
        return x + width + spacing + 1;
    }
}
