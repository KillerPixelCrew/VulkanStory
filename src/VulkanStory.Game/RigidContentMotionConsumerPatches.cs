using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Retained rigid contents histories attached at the original final mesh draws.
/// <summary>Adds stable previous transforms around built-in rigid block-content draws and restores draw state on exit.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class RigidContentMotionConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-motion-rigid-content";
    private static ProcessRuntime? runtime;
    private static Dictionary<Type, Func<object, Matrixf>> matrices = new();
    private static Dictionary<MethodBase, (int Single, int Multi)> anchors = new();
    private static readonly MethodInfo Single = AccessTools.Method(typeof(IRenderAPI), nameof(IRenderAPI.RenderMesh), [typeof(MeshRef)])!;
    private static readonly MethodInfo Multi = AccessTools.Method(typeof(IRenderAPI), nameof(IRenderAPI.RenderMultiTextureMesh),
        [typeof(MultiTextureMeshRef), typeof(string), typeof(int)])!;
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <param name="survival">Original Survival assembly containing the pinned built-in consumers.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly survival)
    {
        var targets = new Dictionary<MethodBase, (int Single, int Multi)>();
        var getters = new Dictionary<Type, Func<object, Matrixf>>();
        var profile = new (string Name, int Single, int Multi)[]
        {
            ("BloomeryContentsRenderer", 1, 0), ("FirepitContentsRenderer", 0, 1),
            ("FruitpressContentsRenderer", 0, 1), ("ForgeContentsRenderer", 0, 2),
            ("QuernTopRenderer", 1, 0), ("ResonatorRenderer", 1, 0),
        };
        foreach (var item in profile)
        {
            Type type = survival.GetType("Vintagestory.GameContent." + item.Name, true)!;
            MethodInfo method = AccessTools.Method(type, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]) ??
                throw new MissingMethodException(type.FullName, "OnRenderFrame");
            FieldInfo field = AccessTools.Field(type, "ModelMat") ?? throw new MissingFieldException(type.FullName, "ModelMat");
            if (field.FieldType != typeof(Matrixf)) throw new InvalidOperationException("Original rigid content matrix changed.");
            var instance = Expression.Parameter(typeof(object), "renderer");
            getters.Add(type, Expression.Lambda<Func<object, Matrixf>>(
                Expression.Field(Expression.Convert(instance, type), field), instance).Compile());
            targets.Add(method, (item.Single, item.Multi));
        }
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-motion-rigid-content", () =>
        {
            foreach (var target in targets)
            {
                if (target.Key is not MethodInfo { ReturnType: var result } || result != typeof(void) || target.Key.GetMethodBody() == null)
                    throw new InvalidOperationException("Original rigid content method changed.");
                Check(target.Key, PatchProcessor.GetOriginalInstructions(target.Key), target.Value);
            }
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Rigid content motion already has an owner.");
            runtime = owner; matrices = getters; anchors = targets; attempted = true;
            foreach (var target in targets.Keys)
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(RigidContentMotionConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) { runtime = null; matrices = new(); anchors = new(); }
            attempted = false;
        });
    }
    private static void Check(MethodBase method, IEnumerable<CodeInstruction> instructions, (int Single, int Multi) count)
    {
        var body = instructions.ToList();
        if (body.Count(instruction => instruction.Calls(Single)) != count.Single ||
            body.Count(instruction => instruction.Calls(Multi)) != count.Multi)
            throw new InvalidOperationException("Original/incoming rigid content draw anchors changed: " + method);
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var body = instructions.ToList(); Check(__originalMethod, body, anchors[__originalMethod]);
        foreach (var instruction in body)
        {
            bool single = instruction.Calls(Single), multi = instruction.Calls(Multi);
            if (single || multi)
            {
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                receiver.labels.AddRange(instruction.labels); instruction.labels.Clear();
                receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return receiver;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(RigidContentMotionConsumerPatches), single ? nameof(DrawSingle) : nameof(DrawMulti));
            }
            yield return instruction;
        }
    }
    private static GameRenderSession? Session()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active rigid content routing lost its session.");
        return session;
    }
    private static bool Begin(GameRenderSession session, object renderer, object mesh)
    {
        var temporal = session.Temporal;
        if (!temporal.EntityMotion.Enabled || !temporal.State.JitterActive ||
            ShaderProgramBase.CurrentShaderProgram is not { PassName: "standard" } program ||
            !program.HasUniform("taaHistoryValid")) return false;
        bool opened = session.Graphics.BeginMotionWrite(temporal);
        if (!opened && !session.Graphics.FrameState.MotionWriteActive) return false;
        try
        {
            // Capture only inside the temporal window. Late resonator overlays must
            // not advance the transform that next frame uses as its previous value.
            Type? type = renderer.GetType();
            while (type != null && !matrices.ContainsKey(type)) type = type.BaseType;
            if (type == null) throw new InvalidOperationException("Rigid content renderer lost its matrix binding.");
            temporal.StandardMotion.Apply(program, renderer, mesh, matrices[type](renderer).Values);
            return opened;
        }
        catch { if (opened) session.Graphics.EndMotionWrite(); throw; }
    }
    private static void DrawSingle(IRenderAPI render, MeshRef mesh, object renderer)
    {
        var session = Session(); bool motion = session != null && Begin(session, renderer, mesh);
        try { render.RenderMesh(mesh); }
        finally { if (motion) session!.Graphics.EndMotionWrite(); }
    }
    private static void DrawMulti(IRenderAPI render, MultiTextureMeshRef mesh, string sampler, int unit, object renderer)
    {
        var session = Session(); bool motion = session != null && Begin(session, renderer, mesh);
        try { render.RenderMultiTextureMesh(mesh, sampler, unit); }
        finally { if (motion) session!.Graphics.EndMotionWrite(); }
    }
}
