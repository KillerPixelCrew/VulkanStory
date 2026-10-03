using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class DroppedFallingMotionConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-motion-dropped-falling";
    private static ProcessRuntime? runtime;
    private static Func<object, float[]>? droppedMatrix, fallingMatrix;
    private static MethodInfo? droppedTarget, fallingTarget;
    private static FieldInfo? fallingMesh;
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(IRenderAPI), nameof(IRenderAPI.RenderMultiTextureMesh),
        [typeof(MultiTextureMeshRef), typeof(string), typeof(int)])!;

    private static Func<object, float[]> MatrixGetter(Type type, bool matrixf)
    {
        FieldInfo field = AccessTools.Field(type, "ModelMat") ?? throw new MissingFieldException(type.FullName, "ModelMat");
        if (field.FieldType != (matrixf ? typeof(Matrixf) : typeof(float[])))
            throw new InvalidOperationException("Original moving object matrix changed.");
        var instance = Expression.Parameter(typeof(object), "renderer");
        Expression value = Expression.Field(Expression.Convert(instance, type), field);
        if (matrixf) value = Expression.PropertyOrField(value, nameof(Matrixf.Values));
        return Expression.Lambda<Func<object, float[]>>(value, instance).Compile();
    }
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly essentials)
    {
        Type dropped = essentials.GetType("Vintagestory.GameContent.EntityItemRenderer", true)!;
        Type falling = essentials.GetType("Vintagestory.GameContent.ModSystemRenderFallingBlocksFast", true)!;
        Type entity = essentials.GetType("Vintagestory.GameContent.EntityBlockFalling", true)!;
        MethodInfo itemRender = AccessTools.Method(dropped, "DoRender3DOpaque", [typeof(float), typeof(bool)]) ??
            throw new MissingMethodException(dropped.FullName, "DoRender3DOpaque");
        MethodInfo fallingRender = AccessTools.Method(falling, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]) ??
            throw new MissingMethodException(falling.FullName, "OnRenderFrame");
        FieldInfo mesh = AccessTools.Field(entity, "meshRef") ?? throw new MissingFieldException(entity.FullName, "meshRef");
        var itemGetter = MatrixGetter(dropped, false); var blockGetter = MatrixGetter(falling, true);
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-motion-dropped-falling", () =>
        {
            foreach (var target in new[] { itemRender, fallingRender })
                if (target.ReturnType != typeof(void) || target.GetMethodBody() == null)
                    throw new InvalidOperationException("Original moving object render signature changed.");
            if (mesh.FieldType != typeof(MultiTextureMeshRef)) throw new InvalidOperationException("Original falling mesh type changed.");
            Check(PatchProcessor.GetOriginalInstructions(itemRender), false, mesh);
            Check(PatchProcessor.GetOriginalInstructions(fallingRender), true, mesh);
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Dropped/falling motion already has an owner.");
            runtime = owner; droppedMatrix = itemGetter; fallingMatrix = blockGetter;
            droppedTarget = itemRender; fallingTarget = fallingRender; fallingMesh = mesh; attempted = true;
            foreach (var target in new[] { itemRender, fallingRender })
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(DroppedFallingMotionConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner))
            { runtime = null; droppedMatrix = fallingMatrix = null; droppedTarget = fallingTarget = null; fallingMesh = null; }
            attempted = false;
        });
    }
    private static bool LoadsLocal(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldloc ||
        instruction.opcode == OpCodes.Ldloc_S || instruction.opcode == OpCodes.Ldloc_0 ||
        instruction.opcode == OpCodes.Ldloc_1 || instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Ldloc_3;
    private static int Check(IEnumerable<CodeInstruction> instructions, bool falling, FieldInfo mesh)
    {
        var body = instructions.ToList();
        int index = body.FindIndex(instruction => instruction.Calls(Draw));
        if (index < 0 || body.Count(instruction => instruction.Calls(Draw)) != 1)
            throw new InvalidOperationException("Original/incoming moving object draw anchor changed.");
        // The entity local that supplies this draw's mesh also supplies its history
        // identity. Never key all falling blocks on their shared renderer or mesh.
        if (falling && (index < 4 || body[index - 3].opcode != OpCodes.Ldfld ||
            !Equals(body[index - 3].operand, mesh) || !LoadsLocal(body[index - 4]) || body[index - 2].opcode != OpCodes.Ldstr ||
            body[index - 1].opcode != OpCodes.Ldc_I4_0))
            throw new InvalidOperationException("Original/incoming falling entity identity anchor changed.");
        return index;
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        bool falling = Equals(__originalMethod, fallingTarget);
        if (!falling && !Equals(__originalMethod, droppedTarget)) throw new InvalidOperationException("Unknown moving object target.");
        var body = instructions.ToList(); int draw = Check(body, falling, fallingMesh!);
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (index == draw)
            {
                var renderer = new CodeInstruction(OpCodes.Ldarg_0);
                renderer.labels.AddRange(instruction.labels); instruction.labels.Clear();
                renderer.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return renderer;
                yield return falling ? new CodeInstruction(body[draw - 4].opcode, body[draw - 4].operand) : new CodeInstruction(OpCodes.Ldarg_2);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(DroppedFallingMotionConsumerPatches), falling ? nameof(DrawFalling) : nameof(DrawDropped));
            }
            yield return instruction;
        }
    }
    private static GameRenderSession? Session()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active dropped/falling motion lost its session.");
        return session;
    }
    private static bool Begin(GameRenderSession session, object identity, object mesh, float[] matrix)
    {
        var temporal = session.Temporal;
        if (!temporal.EntityMotion.Enabled || !temporal.State.JitterActive ||
            ShaderProgramBase.CurrentShaderProgram is not { PassName: "standard" } program || !program.HasUniform("taaHistoryValid")) return false;
        bool opened = session.Graphics.BeginMotionWrite(temporal);
        if (!opened && !session.Graphics.FrameState.MotionWriteActive) return false;
        try { temporal.StandardMotion.Apply(program, identity, mesh, matrix); return opened; }
        catch { if (opened) session.Graphics.EndMotionWrite(); throw; }
    }
    private static void DrawDropped(IRenderAPI render, MultiTextureMeshRef mesh, string sampler, int unit, object renderer, bool shadow)
    {
        var session = Session(); bool motion = !shadow && session != null && Begin(session, renderer, mesh, droppedMatrix!(renderer));
        try { render.RenderMultiTextureMesh(mesh, sampler, unit); }
        finally { if (motion) session!.Graphics.EndMotionWrite(); }
    }
    private static void DrawFalling(IRenderAPI render, MultiTextureMeshRef mesh, string sampler, int unit, object renderer, object entity)
    {
        var session = Session(); bool motion = session != null && Begin(session, entity, mesh, fallingMatrix!(renderer));
        try { render.RenderMultiTextureMesh(mesh, sampler, unit); }
        finally { if (motion) session!.Graphics.EndMotionWrite(); }
    }
}
