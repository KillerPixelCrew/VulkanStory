using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class ParticleConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-particles";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Render = AccessTools.Method(typeof(SystemRenderParticles), "Render", [typeof(int), typeof(float)])!;
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(ClientPlatformAbstract), "RenderMeshInstanced", [typeof(MeshRef), typeof(int)])!;
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(Draw)) != 2)
            throw new InvalidOperationException("Original particle pool draw anchors changed.");
    }
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-particles", () =>
        {
            if (Render == null || Render.ReturnType != typeof(void) || Render.GetMethodBody() == null)
                throw new MissingMethodException("Original particle renderer changed.");
            Check(PatchProcessor.GetOriginalInstructions(Render));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Particle routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Render, transpiler: new HarmonyMethod(typeof(ParticleConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
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
            if (instruction.Calls(Draw))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ParticleConsumerPatches), nameof(DrawParticles));
            }
        return body;
    }
    private static void DrawParticles(ClientPlatformAbstract platform, MeshRef mesh, int quantity)
    {
        if (runtime?.Routing.RoutingEnabled != true) { platform.RenderMeshInstanced(mesh, quantity); return; }
        if (platform is not ClientPlatformWindows windows || !runtime.TrySession(windows, out var session))
            throw new InvalidOperationException("Active particle routing lost its session.");
        bool motion = session.Graphics.BeginCameraMotionWrite();
        try { session.Graphics.RenderParticles(mesh, quantity); }
        finally { if (motion) session.Graphics.EndMotionWrite(); }
    }
}
