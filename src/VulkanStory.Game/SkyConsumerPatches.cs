using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Routes original sky-dome draws with their sky and glow textures and captured model-view matrix.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class SkyConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-sky";
    private static ProcessRuntime? runtime;
    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> Origin = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
    private static readonly AccessTools.FieldRef<ClientMain, int> SkyTexture = AccessTools.FieldRefAccess<ClientMain, int>("skyTextureId");
    private static readonly AccessTools.FieldRef<ClientMain, int> GlowTexture = AccessTools.FieldRefAccess<ClientMain, int>("skyGlowTextureId");
    private static readonly Type SkySystem = typeof(ClientMain).Assembly.GetType(
        "Vintagestory.Client.NoObf.SystemRenderSkyColor", throwOnError: true)!;
    private static readonly MethodInfo Render = AccessTools.Method(SkySystem, "OnRenderFrame3D", [typeof(float)])!;
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(ClientPlatformAbstract), "RenderMesh", [typeof(MeshRef)])!;
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(Draw)) != 1)
            throw new InvalidOperationException("Original sky dome draw anchor changed.");
    }
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-sky", () =>
        {
            if (!typeof(ClientSystem).IsAssignableFrom(SkySystem))
                throw new InvalidOperationException("Original sky renderer no longer derives from ClientSystem.");
            if (Render == null || Render.ReturnType != typeof(void) || Render.GetMethodBody() == null)
                throw new MissingMethodException("Original sky renderer changed.");
            if (AccessTools.Field(typeof(ClientSystem), "game")?.FieldType != typeof(ClientMain))
                throw new MissingFieldException("Original sky client association changed.");
            foreach (string name in new[] { "skyTextureId", "skyGlowTextureId" })
                if (AccessTools.Field(typeof(ClientMain), name)?.FieldType != typeof(int))
                    throw new MissingFieldException("Original sky texture binding changed: " + name);
            Check(PatchProcessor.GetOriginalInstructions(Render));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Sky routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Render, transpiler: new HarmonyMethod(typeof(SkyConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
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
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                receiver.labels.AddRange(instruction.labels); instruction.labels.Clear();
                receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return receiver;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SkyConsumerPatches), nameof(DrawSky));
            }
            yield return instruction;
        }
    }
    private static void DrawSky(ClientPlatformAbstract platform, MeshRef mesh, ClientSystem system)
    {
        if (runtime?.Routing.RoutingEnabled != true) { platform.RenderMesh(mesh); return; }
        if (platform is not ClientPlatformWindows windows || !runtime.TrySession(windows, out var session))
            throw new InvalidOperationException("Active sky routing lost its session.");
        ClientMain client = Origin(system);
        if (!ReferenceEquals(client.Platform, platform)) throw new InvalidOperationException("Sky renderer belongs to another platform.");
        session.Graphics.RenderSkyDome(mesh, SkyTexture(client), GlowTexture(client), client.CurrentModelViewMatrix);
    }
}
