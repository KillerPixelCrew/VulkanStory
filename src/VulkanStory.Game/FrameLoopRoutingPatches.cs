using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Substitutes the original window run boundary with the committed SDL loop and session frame dispatch.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class FrameLoopRoutingPatches
{
    private const string Owner = "vulkanstory.routing.frame-loop";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo NativeRun = typeof(GameWindow).GetMethod(nameof(GameWindow.Run), Type.EmptyTypes)!;
    private static MethodInfo Frame => typeof(ClientPlatformWindows).GetMethod("window_RenderFrame",
        BindingFlags.Instance | BindingFlags.NonPublic, binder: null, types: [typeof(FrameEventArgs)], modifiers: null) ??
        throw new MissingMethodException("Original game frame callback is missing.");
    private static MethodInfo Start => (MethodInfo)StartupTargets.Resolve(typeof(ClientProgram), StartupTargets.Profile1227.Single(target => target.Event == "game.client.start"));
    private static void CheckRun(IEnumerable<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(NativeRun)) != 1)
            throw new InvalidOperationException("Original client startup must have exactly one GameWindow.Run call.");
    }
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner);
        bool attempted = false;
        return new StartupPatchGroup("frame-loop", () =>
        {
            GameFrameBindings.Validate();
            if (Frame.ReturnType != typeof(void) || Frame.GetMethodBody() is null) throw new InvalidOperationException("Original frame callback changed.");
            CheckRun(PatchProcessor.GetOriginalInstructions(Start));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Frame routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Frame, prefix: new HarmonyMethod(typeof(FrameLoopRoutingPatches), nameof(Render)) { priority = Priority.First });
            harmony.Patch(Start, transpiler: new HarmonyMethod(typeof(FrameLoopRoutingPatches), nameof(RunTranspiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static bool Render(ClientPlatformWindows __instance)
    {
        if (runtime?.HasCommitted != true) return true;
        if (!runtime.TrySession(__instance, out var session)) throw new InvalidOperationException("Frame routing lost its active session.");
        session.RenderFrame(); return false;
    }
    private static IEnumerable<CodeInstruction> RunTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); CheckRun(body);
        foreach (var instruction in body)
            if (instruction.Calls(NativeRun))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = typeof(FrameLoopRoutingPatches).GetMethod(nameof(Run), BindingFlags.Static | BindingFlags.NonPublic)!;
            }
        return body;
    }
    private static void Run(GameWindow window)
    {
        if (runtime?.HasCommitted != true) { window.Run(); return; }
        runtime.Run();
    }
}
