using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using HotkeyMap = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Client.HotKey>;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Makes priority hotkey registration replace an existing code like ordinary registration does.</summary>
internal static class HotkeyRegistrationConsumerPatches
{
    private const string Owner = "vulkanstory.routing.hotkey-registration";
    private static Func<bool>? enabled;
    private static readonly Type DictionaryType = typeof(HotkeyMap);
    private static readonly MethodInfo Insert = AccessTools.DeclaredMethod(DictionaryType, "Insert",
        [typeof(int), typeof(string), typeof(HotKey)])!;
    private static readonly MethodInfo Replace = AccessTools.DeclaredMethod(DictionaryType, "set_Item",
        [typeof(string), typeof(HotKey)])!;
    private static readonly MethodInfo SetDefault = AccessTools.DeclaredMethod(typeof(HotKey), nameof(HotKey.SetDefaultMapping))!;
    private static readonly MethodInfo SavedMapping = AccessTools.DeclaredMethod(typeof(ClientSettings), "get_KeyMapping", Type.EmptyTypes)!;

    /// <summary>Installs the checked registration seam with the complete startup routing transaction.</summary>
    internal static StartupPatchGroup CreateGroup(Func<bool> routing)
    {
        MethodInfo target = AccessTools.DeclaredMethod(typeof(HotkeyManager), nameof(HotkeyManager.RegisterHotKey),
            [typeof(string), typeof(string), typeof(int), typeof(HotkeyType), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)])
            ?? throw new MissingMethodException(typeof(HotkeyManager).FullName, nameof(HotkeyManager.RegisterHotKey));
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-hotkey-registration", () =>
        {
            if (target.Module.ModuleVersionId != new Guid("8a3ad3aa-2940-4c39-ae1b-881de9e65346") ||
                target.IsStatic || target.ReturnType != typeof(void) || target.GetMethodBody() == null)
                throw new InvalidOperationException("Hotkey registration requires the official Vintage Story 1.22.7 Lib profile.");
            Check(PatchProcessor.GetOriginalInstructions(target));
        }, () =>
        {
            if (enabled != null) throw new InvalidOperationException("Hotkey registration already has a routing owner.");
            enabled = routing; attempted = true;
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(HotkeyRegistrationConsumerPatches), nameof(Transpiler))
                { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(enabled, routing)) enabled = null;
            attempted = false;
        });
    }

    private static void Check(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        int insert = body.FindIndex(instruction => instruction.Calls(Insert));
        if (body.Count(instruction => instruction.Calls(Insert)) != 1 || insert < 3 ||
            body[insert - 3].opcode != OpCodes.Ldc_I4_0 ||
            body.Count(instruction => instruction.Calls(Replace)) != 1 ||
            body.Count(instruction => instruction.Calls(SetDefault)) != 1 ||
            body.Count(instruction => instruction.Calls(SavedMapping)) != 1)
            throw new InvalidOperationException("Original/incoming priority hotkey registration anchors changed.");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body);
        foreach (var instruction in body)
        {
            if (instruction.Calls(Insert))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(HotkeyRegistrationConsumerPatches), nameof(InsertFirst));
            }
            yield return instruction;
        }
    }

    private static void InsertFirst(HotkeyMap hotkeys, int index, string code, HotKey hotkey)
    {
        // The original body still builds the new hotkey, sets its default and restores the saved mapping.
        // Handler assignment and world-leave cleanup retain the ordinary registration lifecycle.
        if (enabled?.Invoke() == true) hotkeys.Remove(code);
        hotkeys.Insert(index, code, hotkey);
    }
}
