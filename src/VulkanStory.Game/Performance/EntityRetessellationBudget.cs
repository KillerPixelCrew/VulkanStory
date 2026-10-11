using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace VulkanStory.Game.Performance;

/// <summary>Spreads existing entity mesh rebuilds across frames; new meshes are immediate and waiting work cannot starve.</summary>
internal static class EntityRetessellationBudget
{
    private sealed class State { internal long WaitingSince = -1; }
    private static ConditionalWeakTable<object, State> states = new();
    private static FieldInfo? mesh;
    private static MethodInfo? target, tessellate;
    private static Action<object>? original;
    private static long frame;
    private static int used;
    internal static void BeginFrame() { frame++; used = 0; }

    internal static PerformanceFeature Create(Assembly essentials)
    {
        return new("entity-retessellation-budget", s => s.EntityRetessellationBudget, () =>
        {
            Type renderer = essentials.GetType("Vintagestory.GameContent.EntityShapeRenderer", true)!;
            mesh = AccessTools.Field(renderer, "meshRefOpaque");
            tessellate = AccessTools.Method(renderer, "TesselateShape", Type.EmptyTypes);
            if (mesh == null || tessellate?.ReturnType != typeof(void)) throw new InvalidOperationException("Entity tessellation members changed.");
            target = AccessTools.Method(renderer, "BeforeRender", [typeof(float)]);
            if (target == null || PatchProcessor.GetOriginalInstructions(target).Count(i => i.Calls(tessellate)) != 1)
                throw new InvalidOperationException("Entity render rebuild call anchor changed.");
            var call = new DynamicMethod("VulkanStoryEntityTessellate", typeof(void), [typeof(object)], renderer, true);
            var il = call.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, renderer);
            il.Emit(tessellate.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, tessellate); il.Emit(OpCodes.Ret);
            original = call.CreateDelegate<Action<object>>();
        }, harmony => harmony.Patch(target!, transpiler: new HarmonyMethod(typeof(EntityRetessellationBudget), nameof(Transpiler))),
            WorldLeft: () => states = new());
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(tessellate))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(EntityRetessellationBudget), nameof(Rebuild));
            }
            yield return instruction;
        }
    }
    private static void Rebuild(object renderer)
    {
        State state = states.GetOrCreateValue(renderer);
        bool enabled = PerformancePatchSet.Current?.EntityRetessellationBudget == true;
        if (enabled && mesh!.GetValue(renderer) != null && used >= 2)
        {
            if (state.WaitingSince < 0) state.WaitingSince = frame;
            if (frame - state.WaitingSince < 8) return;
        }
        original!(renderer);
        state.WaitingSince = -1;
        used++;
    }
}
