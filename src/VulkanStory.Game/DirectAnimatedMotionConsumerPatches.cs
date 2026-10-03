using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Echo Chamber's direct draws share one animated pose and bypass the multi-texture seam.
internal static class DirectAnimatedMotionConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-motion-direct-animated";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(IRenderAPI), nameof(IRenderAPI.RenderMesh), [typeof(MeshRef)])!;
    private static void Check(IEnumerable<CodeInstruction> instructions)
    {
        if (instructions.Count(instruction => instruction.Calls(Draw)) != 3)
            throw new InvalidOperationException("Original/incoming Echo Chamber animated draw anchors changed.");
    }
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly survival)
    {
        Type type = survival.GetType("Vintagestory.GameContent.EchoChamberRenderer", true)!;
        MethodInfo target = AccessTools.Method(type, "DoRender3DOpaque", [typeof(float), typeof(bool)]) ??
            throw new MissingMethodException(type.FullName, "DoRender3DOpaque");
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-motion-direct-animated", () =>
        {
            if (target.ReturnType != typeof(void) || target.GetMethodBody() == null)
                throw new InvalidOperationException("Original Echo Chamber renderer signature changed.");
            Check(PatchProcessor.GetOriginalInstructions(target));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Direct animated motion already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(DirectAnimatedMotionConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body);
        foreach (var instruction in body)
            if (instruction.Calls(Draw))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(DirectAnimatedMotionConsumerPatches), nameof(DrawAnimated));
            }
        return body;
    }
    private static void DrawAnimated(IRenderAPI render, MeshRef mesh)
    {
        if (runtime?.Routing.RoutingEnabled != true) { render.RenderMesh(mesh); return; }
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active direct animated routing lost its session.");
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (program is not { PassName: "entityanimated" }) { render.RenderMesh(mesh); return; }
        bool motion = session.Graphics.BeginAnimatedMotionWrite();
        try
        {
            const string sampler = "entityTex";
            session.Graphics.RenderEntityMesh(mesh, sampler, session.Graphics.DeclaredProgramTexture(program.ProgramId, sampler));
        }
        finally { if (motion) session.Graphics.EndMotionWrite(); }
    }
}
