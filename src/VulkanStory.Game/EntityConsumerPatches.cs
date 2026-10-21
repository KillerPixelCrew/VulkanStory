using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using VulkanStory.Render.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Routes original textured entity mesh calls to native entity pipelines when session graphics routing is active.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class EntityConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-entities";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Multi = AccessTools.Method(typeof(RenderAPIBase), "RenderMultiTextureMesh",
        [typeof(MultiTextureMeshRef), typeof(string), typeof(int)])!;
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(ClientPlatformAbstract), "RenderMesh", [typeof(MeshRef)])!;
    private static readonly MethodInfo Opaque = AccessTools.Method(typeof(EntityRenderer), nameof(EntityRenderer.DoRender3DOpaque), [typeof(float), typeof(bool)])!;
    private static readonly MethodInfo Batched = AccessTools.Method(typeof(EntityRenderer), nameof(EntityRenderer.DoRender3DOpaqueBatched), [typeof(float), typeof(bool)])!;
    private static readonly MethodInfo After = AccessTools.Method(typeof(EntityRenderer), nameof(EntityRenderer.DoRender3DAfterOIT), [typeof(float), typeof(bool)])!;
    private static readonly MethodInfo OpaqueDispatch = AccessTools.Method(typeof(SystemRenderEntities), "OnRenderOpaque3D", [typeof(float)])!;
    private static readonly MethodInfo AfterDispatch = AccessTools.Method(typeof(SystemRenderEntities), "OnRenderAfterOIT", [typeof(float)])!;
    private static readonly MethodInfo ShadowDispatch = AccessTools.Method(typeof(SystemRenderEntities), "OnRenderFrameShadows", [typeof(float)])!;
    private static readonly MethodInfo RemoveRenderer = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.RemoveEntityRenderer), [typeof(Entity)])!;
    private static readonly MethodInfo DisposeEntities = AccessTools.Method(typeof(SystemRenderEntities), nameof(SystemRenderEntities.Dispose), [typeof(ClientMain)])!;
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(Draw)) != 1)
            throw new InvalidOperationException("Original multi-texture mesh draw anchor changed.");
        _ = SubMeshIndex(body);
    }
    private static CodeInstruction SubMeshIndex(IReadOnlyList<CodeInstruction> body)
    {
        var meshes = AccessTools.Field(typeof(MultiTextureMeshRef), nameof(MultiTextureMeshRef.meshrefs));
        var indices = new List<CodeInstruction>();
        for (int i = 0; i + 2 < body.Count; i++)
            if (body[i].LoadsField(meshes) && body[i + 1].IsLdloc() && body[i + 2].opcode == OpCodes.Ldelem_Ref)
                indices.Add(body[i + 1]);
        if (indices.Count != 1) throw new InvalidOperationException("Original entity sub-mesh index anchor changed.");
        return indices[0];
    }
    private static void CheckDispatch(MethodBase method, IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        bool opaque = Equals(method, OpaqueDispatch) || Equals(method, ShadowDispatch);
        if ((!opaque && !Equals(method, AfterDispatch)) ||
            body.Count(instruction => instruction.Calls(Opaque)) != (opaque ? 1 : 0) ||
            body.Count(instruction => instruction.Calls(Batched)) != (opaque ? 1 : 0) ||
            body.Count(instruction => instruction.Calls(After)) != (opaque ? 0 : 1))
            throw new InvalidOperationException("Original/incoming entity dispatch anchors changed.");
    }
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-entities", () =>
        {
            if (Multi == null || Multi.ReturnType != typeof(void) || Multi.GetMethodBody() == null)
                throw new MissingMethodException("Original multi-texture mesh method changed.");
            Check(PatchProcessor.GetOriginalInstructions(Multi));
            foreach (var target in new[] { OpaqueDispatch, AfterDispatch, ShadowDispatch })
            {
                if (target == null || target.ReturnType != typeof(void) || target.GetMethodBody() == null)
                    throw new MissingMethodException("Original entity dispatch method changed.");
                CheckDispatch(target, PatchProcessor.GetOriginalInstructions(target));
            }
            if (RemoveRenderer?.ReturnType != typeof(void) || DisposeEntities?.ReturnType != typeof(void))
                throw new MissingMethodException("Original entity renderer lifecycle changed.");
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Entity routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Multi, transpiler: new HarmonyMethod(typeof(EntityConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
            foreach (var target in new[] { OpaqueDispatch, AfterDispatch, ShadowDispatch })
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(EntityConsumerPatches), nameof(BeginStage)),
                    postfix: new HarmonyMethod(typeof(EntityConsumerPatches), nameof(FlushStage)),
                    finalizer: new HarmonyMethod(typeof(EntityConsumerPatches), nameof(EndStage)),
                    transpiler: new HarmonyMethod(typeof(EntityConsumerPatches), nameof(DispatchTranspiler)) { priority = Priority.First });
            harmony.Patch(RemoveRenderer, prefix: new HarmonyMethod(typeof(EntityConsumerPatches), nameof(ReleaseEntity)));
            harmony.Patch(DisposeEntities, prefix: new HarmonyMethod(typeof(EntityConsumerPatches), nameof(ReleaseWorld)));
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body);
        var subMeshIndex = SubMeshIndex(body);
        foreach (var instruction in body)
        {
            if (instruction.Calls(Draw))
            {
                var sampler = new CodeInstruction(OpCodes.Ldarg_2);
                sampler.labels.AddRange(instruction.labels); instruction.labels.Clear();
                sampler.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock); yield return sampler;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(subMeshIndex.opcode, subMeshIndex.operand);
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(EntityConsumerPatches), nameof(DrawEntity));
            }
            yield return instruction;
        }
    }
    private static IEnumerable<CodeInstruction> DispatchTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var body = instructions.ToList(); CheckDispatch(__originalMethod, body);
        foreach (var instruction in body)
        {
            string? wrapper = instruction.Calls(Opaque) ? nameof(RenderOpaque) :
                instruction.Calls(Batched) ? nameof(RenderBatched) : instruction.Calls(After) ? nameof(RenderAfter) : null;
            if (wrapper != null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(EntityConsumerPatches), wrapper);
            }
            yield return instruction;
        }
    }
    private static void RenderOpaque(EntityRenderer renderer, float dt, bool shadow) => Dispatch(renderer, dt, shadow, 0);
    private static void RenderBatched(EntityRenderer renderer, float dt, bool shadow) => Dispatch(renderer, dt, shadow, 1);
    private static void RenderAfter(EntityRenderer renderer, float dt, bool shadow) => Dispatch(renderer, dt, shadow, 2);
    private static GameGraphicsAdapter? ActiveGraphics()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active entity stage lost its session.");
        return session.Graphics;
    }
    private static void BeginStage(MethodBase __originalMethod, out GameGraphicsAdapter? __state)
    {
        __state = ActiveGraphics();
        __state?.BeginResidentEntityStage(__originalMethod);
    }
    private static void FlushStage(GameGraphicsAdapter? __state) => __state?.DrawResidentEntityStage();
    private static void EndStage(GameGraphicsAdapter? __state) => __state?.EndResidentEntityStage();
    private static void ReleaseEntity(Entity __0) => ActiveGraphics()?.ReleaseResidentEntity(__0.EntityId);
    private static void ReleaseWorld() => ActiveGraphics()?.ResetResidentEntities();
    private static void Dispatch(EntityRenderer renderer, float dt, bool shadow, int kind)
    {
        GameGraphicsAdapter? graphics = null;
        GameGraphicsAdapter.RigidEntityScope previous = default;
        GameGraphicsAdapter.ResidentEntityScope previousResident = default;
        if (runtime?.Routing.RoutingEnabled == true)
        {
            if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
                throw new InvalidOperationException("Active entity dispatch lost its session.");
            graphics = session.Graphics;
            previousResident = graphics.EnterResidentEntityScope(renderer, shadow);
            previous = graphics.EnterRigidEntityMotionScope(shadow ? null : renderer.entity);
        }
        try
        {
            switch (kind)
            {
                case 0: renderer.DoRender3DOpaque(dt, shadow); break;
                case 1: renderer.DoRender3DOpaqueBatched(dt, shadow); break;
                case 2: renderer.DoRender3DAfterOIT(dt, shadow); break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
        finally
        {
            graphics?.ExitRigidEntityMotionScope(previous);
            graphics?.ExitResidentEntityScope(previousResident);
        }
    }
    private static void DrawEntity(ClientPlatformAbstract platform, MeshRef mesh, string sampler, MultiTextureMeshRef shape, int subMesh)
    {
        if (runtime?.Routing.RoutingEnabled != true) { platform.RenderMesh(mesh); return; }
        if (platform is not ClientPlatformWindows windows || !runtime.TrySession(windows, out var session))
            throw new InvalidOperationException("Active entity routing lost its session.");
        int program = ShaderProgramBase.CurrentShaderProgram?.ProgramId ?? 0;
        bool motion = false;
        try
        {
            motion = session.Graphics.BeginAnimatedMotionWrite() || session.Graphics.BeginRigidEntityMotionWrite(shape);
            var previousCategory = session.Device.SceneDrawCategory;
            session.Device.SceneDrawCategory = SceneDrawCategory.Entity;
            try { session.Graphics.RenderEntityMesh(mesh, sampler, session.Graphics.DeclaredProgramTexture(program, sampler), shape, subMesh); }
            finally { session.Device.SceneDrawCategory = previousCategory; }
        }
        finally
        {
            if (motion) session.Graphics.EndMotionWrite();
        }
    }
}
