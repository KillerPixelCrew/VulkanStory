using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Retained temporal producers on the unchanged official ClientMain.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class TemporalConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-temporal";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo MainLoop = Target(nameof(ClientMain.MainRenderLoop), typeof(void), [typeof(float)]);
    private static readonly MethodInfo Stage = Target(nameof(ClientMain.TriggerRenderStage), typeof(void), [typeof(EnumRenderStage), typeof(float)]);
    private static readonly MethodInfo Projection = Target(nameof(ClientMain.Set3DProjection), typeof(void), [typeof(float), typeof(float)]);
    private static readonly MethodInfo AfterPost = Target(nameof(ClientMain.RenderAfterPostProcessing), typeof(void), [typeof(float)]);
    private static readonly MethodInfo CurrentProjection = Target("get_CurrentProjectionMatrix", typeof(float[]), []);
    private static readonly MethodInfo DisposeClient = Target(nameof(ClientMain.Dispose), typeof(void), []);
    private static readonly MethodInfo StackTop = AccessTools.PropertyGetter(typeof(StackMatrix4), nameof(StackMatrix4.Top)) ??
        throw new MissingMethodException("Original matrix-stack Top getter is missing.");
    private static readonly MethodInfo ProjectionStack = AccessTools.PropertyGetter(typeof(IRenderAPI), nameof(IRenderAPI.PMatrix)) ??
        throw new MissingMethodException("Original render API projection-stack getter is missing.");
    private static readonly AccessTools.FieldRef<ClientMain, double[]> Perspective =
        AccessTools.FieldRefAccess<ClientMain, double[]>("set3DProjectionTempMat4");

    private static MethodInfo Target(string name, Type result, Type[] parameters)
    {
        MethodInfo? method = typeof(ClientMain).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            null, parameters, null);
        if (method == null || method.ReturnType != result || method.GetMethodBody() == null)
            throw new MissingMethodException(typeof(ClientMain).FullName, name);
        return method;
    }
    private static void CheckScene(IReadOnlyList<CodeInstruction> body)
    {
        // Original loop obtains the unjittered PMatrix.Top and MvMatrix.Top once.
        if (body.Count(instruction => instruction.Calls(StackTop)) != 2 ||
            body.Count(instruction => instruction.Calls(Stage)) != 8 ||
            Enumerable.Range(1, body.Count - 1).Count(index => body[index].Calls(StackTop) &&
                body[index - 1].Calls(ProjectionStack)) != 1)
            throw new InvalidOperationException("Official world-loop camera/stage anchors changed.");
    }
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner);
        bool attempted = false;
        return new StartupPatchGroup("graphics-temporal", () =>
        {
            if (AccessTools.Field(typeof(ClientMain), "set3DProjectionTempMat4")?.FieldType != typeof(double[]))
                throw new MissingFieldException("Original perspective projection field changed.");
            CheckScene(PatchProcessor.GetOriginalInstructions(MainLoop));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Temporal routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(MainLoop, prefix: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(Enter)),
                postfix: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(SceneMotion)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(Exit)),
                transpiler: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(CameraTranspiler)) { priority = Priority.First });
            harmony.Patch(Stage, prefix: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(BeforeStage)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(StageFinished)) { priority = Priority.Last });
            harmony.Patch(Projection, postfix: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(ProjectionLoaded)));
            harmony.Patch(AfterPost, prefix: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(PostFinished)) { priority = Priority.First });
            harmony.Patch(CurrentProjection, prefix: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(JitterProjection)) { priority = Priority.First });
            harmony.Patch(DisposeClient, prefix: new HarmonyMethod(typeof(TemporalConsumerPatches), nameof(ClientDisposed)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static bool TryOwner(ClientMain client, out GameTemporalOwner owner)
    {
        owner = null!;
        if (runtime?.Routing.RoutingEnabled != true) return false;
        if (client.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active temporal routing has no original-platform session.");
        owner = session.Temporal;
        return true;
    }
    private static void Enter(ClientMain __instance, out GameTemporalOwner? __state)
    {
        __state = null;
        if (!TryOwner(__instance, out var owner)) return;
        owner.EnterScene(__instance);
        __state = owner;
        runtime!.Session.RefreshTerrainLodBias(__instance);
    }
    private static Exception? Exit(Exception? __exception, GameTemporalOwner? __state)
    { __state?.ExitScene(__exception); return __exception; }
    private static void SceneMotion(ClientMain __instance)
    {
        if (!TryOwner(__instance, out var owner)) return;
        var graphics = runtime!.Session.Graphics;
        bool complete = false;
        string? tailFailure = owner.SceneSampleFailure;
        if (owner.HasCurrentSceneSample && graphics.CompiledMotionTargetsMatch)
        {
            bool transparentRendered = __instance.doTransparentRenderPass;
            bool liquid = !transparentRendered || graphics.RenderLiquidMotion(__instance, owner);
            bool sky = graphics.RenderSkyMotion(owner, transparentRendered);
            complete = liquid && sky;
            tailFailure = !liquid && !sky ? "liquid and sky motion passes declined" :
                !liquid ? "liquid motion pass declined" : !sky ? "sky motion pass declined" : null;
        }
        else if (tailFailure == null) tailFailure = graphics.CompiledMotionStatus;
        owner.CompleteSceneMotion(complete, tailFailure);
    }
    private static void BeforeStage(ClientMain __instance, EnumRenderStage __0, float __1,
        out (GameGraphicsAdapter Graphics, EnumRenderStage? Previous)? __state)
    {
        __state = null;
        if (__0 == EnumRenderStage.Before && TryOwner(__instance, out var owner) && owner.InScene)
            owner.Begin(__1);
        if (TryOwner(__instance, out var stageOwner))
        {
            stageOwner.NoteSceneStage(__0);
            var graphics = runtime!.Session.Graphics;
            __state = (graphics, graphics.EnterModRenderStage(__0));
        }
    }
    private static Exception? StageFinished(EnumRenderStage __0, Exception? __exception,
        (GameGraphicsAdapter Graphics, EnumRenderStage? Previous)? __state)
    {
        if (__state is not { } state) return __exception;
        try { if (__exception == null) state.Graphics.RunModPasses(__0); }
        finally { state.Graphics.EndModRenderStage(__0, state.Previous); }
        return __exception;
    }
    private static void ProjectionLoaded(ClientMain __instance, float __1)
    {
        if (TryOwner(__instance, out var owner)) owner.RecordProjection(__instance, __1, Perspective(__instance));
    }
    private static void PostFinished(ClientMain __instance)
    { if (TryOwner(__instance, out var owner)) owner.CloseJitter(); }
    private static void ClientDisposed(ClientMain __instance)
    {
        if (!TryOwner(__instance, out var owner)) return;
        AnalogClientConsumerPatches.ClientLeaving(__instance);
        // A delayed disposal for an older client must not clear the newer
        // world's mod registry. Both clients share the process platform.
        if (owner.CurrentClient != null && !ReferenceEquals(owner.CurrentClient, __instance)) return;
        runtime!.Session.ReleaseControllerWorld(__instance);
        ModRendering.VulkanStoryModPasses.Clear();
        runtime!.Session.Graphics.ClearModPassPlans();
        owner.DetachClient(__instance);
    }
    private static bool JitterProjection(ClientMain __instance, ref float[] __result)
    {
        if (!TryOwner(__instance, out var owner) || !owner.State.JitterActive) return true;
        double[] top = __instance.PMatrix.Top;
        double[] reference = Perspective(__instance);
        if (top is not { Length: 16 } || reference is not { Length: 16 }) return true;
        for (int index = 0; index < 16; index++) if (top[index] != reference[index]) return true;
        __result = owner.State.ApplyJitterCopy(top);
        return false;
    }
    private static IEnumerable<CodeInstruction> CameraTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        CheckScene(body); // Other Harmony owners must preserve the pinned original anchors.
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            yield return instruction;
            if (index > 0 && instruction.Calls(StackTop) && body[index - 1].Calls(ProjectionStack))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TemporalConsumerPatches), nameof(CaptureWorld)));
            }
        }
    }
    private static double[] CaptureWorld(double[] projection, ClientMain client)
    {
        if (TryOwner(client, out var owner)) owner.CaptureWorld(projection);
        return projection; // Culling continues to consume the original unjittered matrix.
    }
}
