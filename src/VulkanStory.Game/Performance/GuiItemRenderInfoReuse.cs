using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>Optimum's inventory allocation port, with a distinct scratch object for each nested GUI call.
/// Public API calls and calls made by mod hooks still construct fresh instances.</summary>
internal static class GuiItemRenderInfoReuse
{
    private sealed class Scope
    {
        internal readonly ItemRenderInfo Scratch = new();
        internal bool Used;
    }
    [ThreadStatic] private static List<Scope>? scopes;
    [ThreadStatic] private static int depth;
    [ThreadStatic] private static int factoryDepth;
    [ThreadStatic] private static int allowedFactoryDepth;
    private static readonly ConstructorInfo Constructor = typeof(ItemRenderInfo).GetConstructor(Type.EmptyTypes)!;
    private static MethodInfo Gui() => AccessTools.Method(typeof(InventoryItemRenderer), "RenderItemstackToGui",
        [typeof(ItemSlot), typeof(double), typeof(double), typeof(double), typeof(float), typeof(int), typeof(float), typeof(bool), typeof(bool), typeof(bool)])!;
    private static MethodInfo Factory() => AccessTools.Method(typeof(InventoryItemRenderer), "GetItemStackRenderInfo")!;

    internal static PerformanceFeature Create() => new("gui-item-render-info-reuse", s => s.GuiItemRenderInfoReuse, () =>
    {
        if (Gui() == null || Factory() == null || Constructor == null ||
            PatchProcessor.GetOriginalInstructions(Factory()).Count(IsConstruction) == 0 ||
            PatchProcessor.GetOriginalInstructions(Gui()).Count(i => i.Calls(Factory())) != 1)
            throw new InvalidOperationException("Inventory render-info construction anchors changed.");
    }, harmony =>
    {
        harmony.Patch(Gui(), prefix: new HarmonyMethod(typeof(GuiItemRenderInfoReuse), nameof(Enter)),
            transpiler: new HarmonyMethod(typeof(GuiItemRenderInfoReuse), nameof(GuiTranspiler)),
            finalizer: new HarmonyMethod(typeof(GuiItemRenderInfoReuse), nameof(Leave)));
        harmony.Patch(Factory(), prefix: new HarmonyMethod(typeof(GuiItemRenderInfoReuse), nameof(FactoryEnter)),
            transpiler: new HarmonyMethod(typeof(GuiItemRenderInfoReuse), nameof(Transpiler)),
            finalizer: new HarmonyMethod(typeof(GuiItemRenderInfoReuse), nameof(FactoryLeave)));
    });

    private static bool IsConstruction(CodeInstruction instruction) => instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, Constructor);
    private static void Enter(out bool __state)
    {
        __state = PerformancePatchSet.Current?.GuiItemRenderInfoReuse == true;
        if (!__state) return;
        scopes ??= new();
        if (scopes.Count == depth) scopes.Add(new Scope());
        scopes[depth++].Used = false;
    }
    private static void Leave(bool __state) { if (__state) depth--; }
    private static void FactoryEnter() => factoryDepth++;
    private static void FactoryLeave() => factoryDepth--;
    private static IEnumerable<CodeInstruction> GuiTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Factory()))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(GuiItemRenderInfoReuse), nameof(GetGuiInfo));
            }
            yield return instruction;
        }
    }
    private static ItemRenderInfo GetGuiInfo(ClientMain game, ItemSlot slot, EnumItemRenderTarget target, float dt)
    {
        int previous = allowedFactoryDepth;
        allowedFactoryDepth = factoryDepth + 1;
        try { return InventoryItemRenderer.GetItemStackRenderInfo(game, slot, target, dt); }
        finally { allowedFactoryDepth = previous; }
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (IsConstruction(instruction))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(GuiItemRenderInfoReuse), nameof(CreateInfo));
            }
            yield return instruction;
        }
    }
    private static ItemRenderInfo CreateInfo()
    {
        if (depth == 0 || factoryDepth != allowedFactoryDepth || scopes![depth - 1].Used || PerformancePatchSet.Current?.GuiItemRenderInfoReuse != true)
            return new ItemRenderInfo();
        Scope scope = scopes[depth - 1];
        scope.Used = true; // A mod's recursive public API call must never receive this scratch.
        ItemRenderInfo info = scope.Scratch;
        info.ModelRef = null;
        info.Transform = null;
        info.CullFaces = false;
        info.TextureId = 0;
        info.TextureSize.Width = 0;
        info.TextureSize.Height = 0;
        info.AlphaTest = 0;
        info.HalfTransparent = false;
        info.NormalShaded = false;
        info.ApplyColor = false;
        info.OverlayOpacity = 0;
        info.DamageEffect = 0;
        info.InSlot = null;
        info.dt = 0;
        return info;
    }
}
