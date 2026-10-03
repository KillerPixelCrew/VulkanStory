using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class HeldItemMotionConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-held-item-motion";
    private static ProcessRuntime? runtime;
    private static System.Func<object, Matrixf?>? model;
    private static System.Func<object, object?>? attachmentPoint;
    private static ConditionalWeakTable<object, ConditionalWeakTable<object, object>> identities = new();
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(IRenderAPI), "RenderMultiTextureMesh",
        [typeof(MultiTextureMeshRef), typeof(string), typeof(int)])!;
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(Draw)) != 1)
            throw new InvalidOperationException("Original held-item mesh draw anchor changed.");
    }
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly essentials)
    {
        Type type = essentials.GetType("Vintagestory.GameContent.EntityShapeRenderer", true)!;
        MethodInfo render = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == "RenderItem" && method.GetParameters().Length == 5);
        Type[] parameters = render.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        FieldInfo matrix = AccessTools.Field(type, "ItemModelMat") ?? throw new MissingFieldException("Original held-item matrix is missing.");
        MemberInfo point = (MemberInfo?)parameters[3].GetField("AttachPoint") ?? parameters[3].GetProperty("AttachPoint") ??
            throw new MissingMemberException("Original attachment-point association is missing.");
        var instance = Expression.Parameter(typeof(object), "instance");
        var pose = Expression.Parameter(typeof(object), "pose");
        System.Func<object, Matrixf?> getModel = Expression.Lambda<System.Func<object, Matrixf?>>(
            Expression.Field(Expression.Convert(instance, type), matrix), instance).Compile();
        Expression pointValue = point is FieldInfo field ? Expression.Field(Expression.Convert(pose, parameters[3]), field) :
            Expression.Property(Expression.Convert(pose, parameters[3]), (PropertyInfo)point);
        System.Func<object, object?> getPoint = Expression.Lambda<System.Func<object, object?>>(Expression.Convert(pointValue, typeof(object)), pose).Compile();
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-motion-held-items", () =>
        {
            if (render.ReturnType != typeof(void) || render.GetMethodBody() == null || matrix.FieldType != typeof(Matrixf) ||
                parameters[0] != typeof(float) || parameters[1] != typeof(bool) || parameters[2] != typeof(ItemStack) ||
                parameters[3].IsValueType || parameters[4] != typeof(ItemRenderInfo))
                throw new InvalidOperationException("Original held-item render signature changed.");
            Check(PatchProcessor.GetOriginalInstructions(render));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Held-item motion already has an owner.");
            runtime = owner; model = getModel; attachmentPoint = getPoint; attempted = true;
            harmony.Patch(render, transpiler: new HarmonyMethod(typeof(HeldItemMotionConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) { runtime = null; model = null; attachmentPoint = null; identities = new(); }
            attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body);
        foreach (var instruction in body)
        {
            if (instruction.Calls(Draw))
            {
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                receiver.labels.AddRange(instruction.labels); instruction.labels.Clear();
                receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return receiver;
                yield return new CodeInstruction(OpCodes.Ldarg_S, (byte)4);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(HeldItemMotionConsumerPatches), nameof(DrawItem));
            }
            yield return instruction;
        }
    }
    private static void DrawItem(IRenderAPI render, MultiTextureMeshRef mesh, string sampler, int unit,
        object renderer, object pose, bool shadow)
    {
        if (runtime?.Routing.RoutingEnabled != true) { render.RenderMultiTextureMesh(mesh, sampler, unit); return; }
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active held-item motion lost its session.");
        var temporal = session.Temporal;
        var program = ShaderProgramBase.CurrentShaderProgram;
        object? point = pose == null ? null : attachmentPoint!(pose);
        bool opened = false;
        if (!shadow && temporal.EntityMotion.Enabled && temporal.State.JitterActive && program != null &&
            program.HasUniform("taaHistoryValid") && point != null && model!(renderer) is { } transform)
        {
            bool alreadyOpen = session.Graphics.FrameState.MotionWriteActive;
            opened = session.Graphics.BeginMotionWrite(temporal);
            if (opened || alreadyOpen)
            {
                object identity = identities.GetValue(renderer, _ => new()).GetValue(point, _ => new object());
                temporal.StandardMotion.Apply(program, identity, mesh, transform.Values);
            }
        }
        try { render.RenderMultiTextureMesh(mesh, sampler, unit); }
        finally { if (opened) session.Graphics.EndMotionWrite(); }
    }
}
