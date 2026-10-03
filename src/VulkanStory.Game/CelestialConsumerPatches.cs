using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class CelestialConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-celestial";
    private static ProcessRuntime? runtime;
    private static readonly Type NightSystem = typeof(ClientMain).Assembly.GetType(
        "Vintagestory.Client.NoObf.SystemRenderNightSky", throwOnError: true)!;
    private static readonly MethodInfo Night = AccessTools.Method(NightSystem, "OnRenderFrame3D", [typeof(float)])!;
    private static readonly MethodInfo Bodies = AccessTools.Method(typeof(SystemRenderSunMoon), "OnRenderFrame3D", [typeof(float)])!;
    private static readonly MethodInfo Draw = AccessTools.Method(typeof(ClientPlatformAbstract), "RenderMesh", [typeof(MeshRef)])!;
    private static void Check(MethodBase method, IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(Draw)) != (method.Equals(Night) ? 1 : 2))
            throw new InvalidOperationException("Original celestial mesh draw anchors changed: " + method.Name);
    }
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-celestial", () =>
        {
            if (!typeof(ClientSystem).IsAssignableFrom(NightSystem))
                throw new InvalidOperationException("Original night renderer no longer derives from ClientSystem.");
            foreach (var method in new[] { Night, Bodies })
            {
                if (method == null || method.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    throw new MissingMethodException("Original celestial renderer changed.");
                Check(method, PatchProcessor.GetOriginalInstructions(method));
            }
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Celestial routing already has an owner.");
            runtime = owner; attempted = true;
            foreach (var method in new[] { Night, Bodies })
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(CelestialConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var body = instructions.ToList(); Check(__originalMethod, body);
        foreach (var instruction in body)
            if (instruction.Calls(Draw))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(CelestialConsumerPatches), __originalMethod.Equals(Night) ? nameof(DrawNight) : nameof(DrawBody));
            }
        return body;
    }
    private static GameGraphicsAdapter? Adapter(ClientPlatformAbstract platform)
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (platform is not ClientPlatformWindows windows || !runtime.TrySession(windows, out var session))
            throw new InvalidOperationException("Active celestial routing lost its session.");
        return session.Graphics;
    }
    private static void DrawNight(ClientPlatformAbstract platform, MeshRef mesh)
    { if (Adapter(platform) is { } graphics) graphics.RenderNightSky(mesh); else platform.RenderMesh(mesh); }
    private static void DrawBody(ClientPlatformAbstract platform, MeshRef mesh)
    { if (Adapter(platform) is { } graphics) graphics.RenderCelestialBody(mesh); else platform.RenderMesh(mesh); }
}
