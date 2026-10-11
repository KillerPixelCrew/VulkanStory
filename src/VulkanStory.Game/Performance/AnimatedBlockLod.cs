using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VulkanStory.Game.Performance;

/// <summary>Optimum's animated-block LOD, with real render-frame stamps and continued distant animation.
/// Only animator evaluation is deferred; AnimationUtil's renderer cleanup still executes.</summary>
internal static class AnimatedBlockLod
{
    private sealed class State
    {
        internal float Deferred;
        internal long LastRun = -1;
        internal int Phase;
    }
    private static ConditionalWeakTable<AnimationUtil, State> states = new();
    private static FieldInfo? position, api;
    private static MethodInfo? target, update;
    private static long frame;
    private static int used;

    internal static void BeginFrame() { frame++; used = 0; }
    internal static PerformanceFeature Create() => new("animated-block-lod", s => s.AnimatedBlockLod, () =>
    {
        position = AccessTools.Field(typeof(AnimationUtil), "position");
        api = AccessTools.Field(typeof(AnimationUtil), "capi");
        if (position?.FieldType != typeof(Vec3d) || api == null) throw new InvalidOperationException("Animated block state changed.");
        var candidates = AccessTools.GetDeclaredMethods(typeof(AnimationUtil)).Where(m => m.Name == "OnRenderFrame" && m.GetMethodBody() != null)
            .Select(m => (Method: m, Calls: PatchProcessor.GetOriginalInstructions(m).Where(IsUpdate).ToArray()))
            .Where(m => m.Calls.Length != 0).ToArray();
        if (candidates.Length != 1 || candidates[0].Calls.Length != 1) throw new InvalidOperationException("Animated block update anchor changed.");
        target = candidates[0].Method;
        update = (MethodInfo)candidates[0].Calls[0].operand;
    }, harmony => harmony.Patch(target!, transpiler: new HarmonyMethod(typeof(AnimatedBlockLod), nameof(Transpiler))),
        WorldLeft: () => states = new());

    private static bool IsUpdate(CodeInstruction instruction) => instruction.operand is MethodInfo method &&
        method.Name == "OnFrame" && typeof(IAnimator).IsAssignableFrom(method.DeclaringType) && method.ReturnType == typeof(void) &&
        method.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(Dictionary<string, AnimationMetaData>), typeof(float)]);

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (Equals(instruction.operand, update))
            {
                // Preserve branch and exception boundaries on the first replacement instruction.
                var owner = new CodeInstruction(OpCodes.Ldarg_0);
                owner.labels.AddRange(instruction.labels); instruction.labels.Clear();
                owner.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return owner;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(AnimatedBlockLod), nameof(OnFrame));
            }
            yield return instruction;
        }
    }

    private static void OnFrame(IAnimator animator, Dictionary<string, AnimationMetaData> animations, float dt, AnimationUtil owner)
    {
        State state = states.GetOrCreateValue(owner);
        state.Deferred += dt;
        if (PerformancePatchSet.Current?.AnimatedBlockLod != true)
        {
            animator.OnFrame(animations, state.Deferred); state.Deferred = 0; state.LastRun = frame;
            return;
        }
        var client = api!.GetValue(owner) as ICoreClientAPI;
        var player = client?.World.Player?.Entity;
        var point = position!.GetValue(owner) as Vec3d;
        int stride = 1;
        if (player != null && point != null)
        {
            double dx = point.X - player.Pos.X, dz = point.Z - player.Pos.Z;
            double squared = dx * dx + dz * dz;
            stride = squared > 48 * 48 ? 8 : squared > 24 * 24 ? 4 : 1;
            state.Phase = (((int)point.X * 3) ^ ((int)point.Y * 5) ^ ((int)point.Z * 7)) & (stride - 1);
        }
        // New instances evaluate immediately. Overdue instances bypass the soft budget, guaranteeing progress
        // even when a busy nearby machine population consumes all normal slots every frame.
        bool overdue = state.LastRun < 0 || frame - state.LastRun >= stride * 2;
        if (!overdue && ((frame + state.Phase) % stride != 0 || used >= 256)) return;
        used++;
        animator.OnFrame(animations, state.Deferred);
        state.Deferred = 0; state.LastRun = frame;
    }
}
