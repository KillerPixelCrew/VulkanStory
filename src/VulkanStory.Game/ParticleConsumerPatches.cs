using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Brackets original particle drawing with the session camera-motion window and native particle path.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class ParticleConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-particles";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Render = AccessTools.Method(typeof(SystemRenderParticles), "Render", [typeof(int), typeof(float)])!;
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(ClientPlatformAbstract), "RenderMeshInstanced", [typeof(MeshRef), typeof(int)])!;
    private static readonly MethodInfo Advance = AccessTools.Method(typeof(IParticlePool), nameof(IParticlePool.OnNewFrame))!;
    private static readonly MethodInfo Fountain = AccessTools.Method(typeof(SystemRenderParticles), "UpdateParticleFountain")!;
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, ClientMain> PoolGame = AccessTools.FieldRefAccess<ParticlePoolQuads, ClientMain>("game");
    private static readonly AccessTools.FieldRef<SystemRenderParticles, ClientMain> SystemGame = AccessTools.FieldRefAccess<SystemRenderParticles, ClientMain>("game");
    private static readonly MethodInfo Spawn = AccessTools.Method(typeof(ParticleGeneric), "Spawned", [typeof(ParticlePhysics)])!;
    private static readonly MethodInfo Produce = AccessTools.Method(typeof(ParticleGeneric), "UpdateBuffers",
        [typeof(MeshData), typeof(Vintagestory.API.MathTools.Vec3d), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType()])!;
    private static readonly MethodInfo BaseProduce = AccessTools.Method(typeof(ParticleBase), nameof(ParticleBase.UpdateBuffers))!;
    private static readonly MethodInfo[] Producers = [AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrame))!,
        AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrameOffThread))!];
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(Draw)) != 2)
            throw new InvalidOperationException("Original particle pool draw anchors changed.");
        if (body.Count(instruction => instruction.Calls(Advance)) != 2 || Fountain == null)
            throw new InvalidOperationException("Original particle frame producers changed.");
    }
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-particles", () =>
        {
            if (Render == null || Render.ReturnType != typeof(void) || Render.GetMethodBody() == null)
                throw new MissingMethodException("Original particle renderer changed.");
            Check(PatchProcessor.GetOriginalInstructions(Render));
            if (Spawn == null || Produce == null) throw new MissingMethodException("Original particle instance producers changed.");
            foreach (var producer in Producers)
                if (PatchProcessor.GetOriginalInstructions(producer).Count(instruction => instruction.Calls(BaseProduce)) != 1)
                    throw new InvalidOperationException("Original virtual particle producer anchor changed.");
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Particle routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Render, transpiler: new HarmonyMethod(typeof(ParticleConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
            harmony.Patch(Spawn, postfix: new HarmonyMethod(typeof(ParticleConsumerPatches), nameof(Spawned)));
            harmony.Patch(Produce, prefix: new HarmonyMethod(typeof(ParticleConsumerPatches), nameof(BeforeProduce)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(ParticleConsumerPatches), nameof(Produced)));
            foreach (var producer in Producers) harmony.Patch(producer, transpiler: new HarmonyMethod(typeof(ParticleConsumerPatches), nameof(ProducerTranspiler)));
            harmony.Patch(Fountain, prefix: new HarmonyMethod(typeof(ParticleConsumerPatches), nameof(BeforeFountain)));
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
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ParticleConsumerPatches), nameof(DrawParticles));
            }
            else if (instruction.Calls(Advance))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ParticleConsumerPatches), nameof(AdvanceParticles));
            }
        }
        return body;
    }
    private static bool ReplayingEye(ClientMain game) => runtime?.Routing.RoutingEnabled == true &&
        game.Platform is ClientPlatformWindows windows && runtime.TrySession(windows, out var session) && session.OpenXrEye == 1;
    private static bool BeforeFountain(SystemRenderParticles __instance) => !ReplayingEye(SystemGame(__instance));
    private static void AdvanceParticles(IParticlePool pool, float dt, Vec3d camera)
    {
        if (pool is ParticlePoolQuads stock && (pool.GetType() == typeof(ParticlePoolQuads) || pool.GetType() == typeof(ParticlePoolCubes)) &&
            ReplayingEye(PoolGame(stock))) return;
        pool.OnNewFrame(dt, camera);
    }
    private static void DrawParticles(ClientPlatformAbstract platform, MeshRef mesh, int quantity)
    {
        if (runtime?.Routing.RoutingEnabled != true) { platform.RenderMeshInstanced(mesh, quantity); return; }
        if (platform is not ClientPlatformWindows windows || !runtime.TrySession(windows, out var session))
            throw new InvalidOperationException("Active particle routing lost its session.");
        bool motion = session.Graphics.BeginCameraMotionWrite();
        try { session.Graphics.RenderParticles(mesh, quantity, session.Temporal.State); }
        finally { if (motion) session.Graphics.EndMotionWrite(); }
    }
    private static void Spawned(ParticleGeneric __instance) => ParticleMotionHistory.Spawned(__instance);
    private static IEnumerable<CodeInstruction> ProducerTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        if (body.Count(instruction => instruction.Calls(BaseProduce)) != 1) throw new InvalidOperationException("Original virtual particle producer anchor changed.");
        foreach (var instruction in body)
        {
            if (instruction.Calls(BaseProduce)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(ParticleConsumerPatches), nameof(ProduceParticle)); }
            yield return instruction;
        }
    }
    private static void ProduceParticle(ParticleBase particle, MeshData data, Vec3d camera, ref int position, ref int rgba, ref int flags)
    {
        Performance.GpuParticleSimulation.BeginInstanceProduction(particle, data, position);
        particle.UpdateBuffers(data, camera, ref position, ref rgba, ref flags);
    }
    private static bool BeforeProduce(ParticleGeneric __instance, MeshData __0, Vec3d __1, ref int __2, ref int __3, ref int __4) =>
        !Performance.GpuParticleSimulation.TryProduceInstance(__instance, __0, __1, ref __2, ref __3, ref __4);
    private static void Produced(ParticleGeneric __instance, MeshData __0, Vec3d __1, ref int __2)
    {
        if (Performance.GpuParticleSimulation.InstanceDeferred(__0, __2 / 4 - 1)) return;
        ParticleMotionHistory.Produced(__instance, __0, __2);
        Performance.GpuParticleSimulation.Produced(__instance, __0, __1, __2);
    }
}
