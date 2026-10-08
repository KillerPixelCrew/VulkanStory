using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
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
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(Draw)) != 1)
            throw new InvalidOperationException("Original multi-texture mesh draw anchor changed.");
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
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Entity routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Multi, transpiler: new HarmonyMethod(typeof(EntityConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
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
        foreach (var instruction in body)
        {
            if (instruction.Calls(Draw))
            {
                var sampler = new CodeInstruction(OpCodes.Ldarg_2);
                sampler.labels.AddRange(instruction.labels); instruction.labels.Clear();
                sampler.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock); yield return sampler;
                instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(EntityConsumerPatches), nameof(DrawEntity));
            }
            yield return instruction;
        }
    }
    private static void DrawEntity(ClientPlatformAbstract platform, MeshRef mesh, string sampler)
    {
        if (runtime?.Routing.RoutingEnabled != true) { platform.RenderMesh(mesh); return; }
        if (platform is not ClientPlatformWindows windows || !runtime.TrySession(windows, out var session))
            throw new InvalidOperationException("Active entity routing lost its session.");
        int program = ShaderProgramBase.CurrentShaderProgram?.ProgramId ?? 0;
        bool motion = session.Graphics.BeginAnimatedMotionWrite();
        try { session.Graphics.RenderEntityMesh(mesh, sampler, session.Graphics.DeclaredProgramTexture(program, sampler)); }
        finally { if (motion) session.Graphics.EndMotionWrite(); }
    }
}
