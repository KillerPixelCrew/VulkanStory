using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Repeat only ScreenManager.Render, after OnNewFrame has drained tasks once.
// The second world pass skips MainRenderLoop's simulation preamble and the
// MainGameLoop task drain; render callbacks still run with a zero time step.
internal static class OpenXrConsumerPatches
{
    private const string Owner = "vulkanstory.routing.openxr";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Render = AccessTools.Method(typeof(ScreenManager), "Render", [typeof(float)]);
    private static readonly MethodInfo WorldRender = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.MainRenderLoop), [typeof(float)]);
    private static readonly MethodInfo Stage = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.TriggerRenderStage), [typeof(EnumRenderStage), typeof(float)]);
    private static readonly MethodInfo EntityBefore = AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender", [typeof(float)]);
    private static readonly MethodInfo AnimateEntity = AccessTools.Method(typeof(IAnimationManager), nameof(IAnimationManager.OnClientFrame), [typeof(float)]);
    private static readonly MethodInfo PoolFrame = AccessTools.Method(typeof(MeshDataPoolMasterManager), nameof(MeshDataPoolMasterManager.OnFrame),
        [typeof(float), typeof(float[]), typeof(float[])]);
    private static readonly Type CloudMap = typeof(Vintagestory.GameContent.EntityShapeRenderer).Assembly.GetType("FluffyClouds.CloudRendererMap", true)!;
    // These callbacks publish or mutate shared world state, never view resources.
    private static readonly MethodInfo[] LogicalFrameUpdates =
    [
        AccessTools.Method(typeof(AmbientManager), nameof(AmbientManager.UpdateAmbient), [typeof(float)]),
        AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame), [typeof(float)]),
        AccessTools.Method(typeof(SystemRenderTerrain), "UpdatePerformanceInfo", [typeof(float)]),
        AccessTools.Method(typeof(GuiManager), nameof(GuiManager.OnBeforeRenderFrame3D), [typeof(float)]),
        AccessTools.Method(typeof(GuiManager), nameof(GuiManager.OnFinalizeFrame), [typeof(float)]),
        AccessTools.Method(typeof(SystemCinematicCamera), nameof(SystemCinematicCamera.OnFinalizeFrame), [typeof(float)]),
        AccessTools.Method(typeof(ClientMain).Assembly.GetType("Vintagestory.Client.NoObf.SystemCompressChunks", true)!,
            "OnFinalizeFrame", [typeof(float)]),
        AccessTools.Method(typeof(SystemMouseInWorldInteractions), nameof(SystemMouseInWorldInteractions.OnFinalizeFrame), [typeof(float)]),
        AccessTools.Method(typeof(SystemScreenshot), nameof(SystemScreenshot.OnFrameDone), [typeof(float)]),
        AccessTools.Method(typeof(SystemScreenshot), "AfterFinalCompo", [typeof(float)]),
        AccessTools.Method(typeof(SystemVideoRecorder), nameof(SystemVideoRecorder.OnFinalizeFrame), [typeof(float)]),
        AccessTools.Method(typeof(SystemSoundEngine), nameof(SystemSoundEngine.OnRenderFrame), [typeof(float), typeof(EnumRenderStage)]),
        AccessTools.Method(typeof(AnimationUtil), nameof(AnimationUtil.OnRenderFrame), [typeof(float), typeof(EnumRenderStage)]),
        AccessTools.Method(typeof(PerceptionEffects), nameof(PerceptionEffects.OnBeforeGameRender), [typeof(float)]),
        AccessTools.Method(CloudMap, "CloudTick", [typeof(float)]),
    ];
    // The draws still run twice; only their shared dither/frame counters step once.
    private static readonly (MethodInfo Method, string Field, int Count)[] ViewFrameCounters =
    [
        (AccessTools.Method(typeof(ClientMain).Assembly.GetType("Vintagestory.Client.NoObf.SystemRenderSkyColor", true)!,
            "OnRenderFrame3D", [typeof(float)]), "frameSeed", 2),
        (AccessTools.Method(typeof(ClientMain).Assembly.GetType("Vintagestory.Client.NoObf.SystemRenderNightSky", true)!,
            "OnRenderFrame3D", [typeof(float)]), "frameSeed", 1),
        (AccessTools.Method(typeof(Vintagestory.GameContent.EntityShapeRenderer).Assembly.GetType("FluffyClouds.CloudRendererVolumetric", true)!,
            "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]), "frame", 1),
    ];
    private static readonly Action<ScreenManager, float> OriginalRender = AccessTools.MethodDelegate<Action<ScreenManager, float>>(Render);
    private static readonly AccessTools.FieldRef<PlayerCamera, ClientMain> CameraClient = AccessTools.FieldRefAccess<PlayerCamera, ClientMain>("game");
    internal static readonly AccessTools.FieldRef<ClientMain, double[]> Perspective = AccessTools.FieldRefAccess<ClientMain, double[]>("set3DProjectionTempMat4");

    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner);
        bool installed = false;
        return new("openxr", () =>
        {
            if (Render?.ReturnType != typeof(void) || CameraClient == null || Perspective == null)
                throw new InvalidOperationException("Official stereo render/camera bindings changed.");
            FindBeforeStage(PatchProcessor.GetOriginalInstructions(WorldRender));
            CheckEntityBefore(PatchProcessor.GetOriginalInstructions(EntityBefore));
            foreach (var method in LogicalFrameUpdates.Append(PoolFrame))
                if (method?.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    throw new InvalidOperationException("Official logical-frame callback changed.");
            foreach (var counter in ViewFrameCounters)
                CheckFrameCounter(PatchProcessor.GetOriginalInstructions(counter.Method), counter.Field, counter.Count);
        }, () =>
        {
            runtime = owner; installed = true;
            harmony.Patch(Render, prefix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(StereoRender)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(ClientMain), nameof(ClientMain.MainGameLoop), [typeof(float)]),
                prefix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(GameLoop)) { priority = Priority.First });
            harmony.Patch(WorldRender, transpiler: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(WorldTranspiler)) { priority = Priority.First });
            harmony.Patch(EntityBefore, transpiler: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(EntityBeforeTranspiler)) { priority = Priority.First });
            harmony.Patch(PoolFrame, prefix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(PoolView)) { priority = Priority.First });
            foreach (var method in LogicalFrameUpdates)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(LogicalFrameUpdate)) { priority = Priority.First });
            foreach (var counter in ViewFrameCounters)
                harmony.Patch(counter.Method, transpiler: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(FrameCounterTranspiler)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(PlayerCamera), nameof(PlayerCamera.OnBeforeRenderFrame3D)),
                prefix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(CameraUpdate)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(CameraUpdated)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(ClientMain), nameof(ClientMain.Set3DProjection), [typeof(float), typeof(float)]),
                prefix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(Projection)) { priority = Priority.First });
        }, () =>
        {
            if (!installed) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            installed = false;
        });
    }

    private static bool Session(out GameRenderSession session)
    {
        session = null!;
        return runtime?.Routing.RoutingEnabled == true && ScreenManager.Platform is ClientPlatformWindows platform &&
            runtime.TrySession(platform, out session);
    }
    private static bool RightEye() => Session(out var session) && session.OpenXrEye == 1;
    private static bool LogicalFrameUpdate() => !RightEye();
    private static int FrameCounterStep() => RightEye() ? 0 : 1;
    private static bool IsFrameCounterStep(IReadOnlyList<CodeInstruction> body, int index, string field) =>
        index > 0 && index + 1 < body.Count && body[index].LoadsConstant(1) && body[index + 1].opcode == OpCodes.Add &&
        body[index - 1].opcode == OpCodes.Ldfld && body[index - 1].operand is FieldInfo { FieldType: var type, Name: var name } &&
        type == typeof(int) && name == field;
    private static void CheckFrameCounter(IReadOnlyList<CodeInstruction> body, string field, int expected)
    {
        if (Enumerable.Range(0, body.Count).Count(index => IsFrameCounterStep(body, index, field)) != expected)
            throw new InvalidOperationException("Official view frame-counter anchors changed: " + field);
    }
    private static IEnumerable<CodeInstruction> FrameCounterTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var body = instructions.ToList();
        var counter = ViewFrameCounters.Single(value => value.Method.Equals(__originalMethod));
        CheckFrameCounter(body, counter.Field, counter.Count);
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (IsFrameCounterStep(body, index, counter.Field))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(OpenXrConsumerPatches), nameof(FrameCounterStep));
            }
            yield return instruction;
        }
    }
    private static bool PoolView(MeshDataPoolMasterManager __instance, float __0, float[] __1, float[] __2)
    {
        if (!RightEye()) return true;
        // OnFrame also rotates the delayed-removal queues. Only its view inputs
        // change for the second eye; geometry must survive the whole frame.
        __instance.currentDt = __0;
        __instance.currentModelViewMatrix = __1;
        __instance.shadowMVPMatrix = __2;
        return false;
    }
    private static void AnimateForFrame(IAnimationManager animator, float dt)
    {
        if (!RightEye()) animator.OnClientFrame(dt);
    }
    private static void CheckEntityBefore(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(AnimateEntity)) != 1)
            throw new InvalidOperationException("Official entity animation frame anchor changed.");
    }
    private static IEnumerable<CodeInstruction> EntityBeforeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); CheckEntityBefore(body);
        foreach (var instruction in body)
        {
            if (instruction.Calls(AnimateEntity))
            {
                // Visibility and renderer preparation remain view-dependent.
                // Animation callbacks, triggers and sounds advance on the left.
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(OpenXrConsumerPatches), nameof(AnimateForFrame));
            }
            yield return instruction;
        }
    }
    private static bool StereoRender(ScreenManager __instance, float __0)
    {
        if (!Session(out var session) || session.OpenXrEye >= 0 || session.Device.OpenXr is not { ShouldRender: true } xr) return true;
        // The loading screen draws with MinimalGui until DoGameInitStage2 loads
        // the regular GUI program. Read its published identity, not ShaderRegistry:
        // touching the registry here would itself create uncompiled GUI programs.
        if (ShaderPrograms.Gui is not { ProgramId: > 0, LoadError: false, Disposed: false })
        {
            xr.SuppressFrame();
            return true;
        }
        session.RenderStereo(__instance, __0, OriginalRender);
        return false;
    }
    private static bool GameLoop(ClientMain __instance)
    {
        // TODO(VR-01): make render-stage producers explicitly view-aware; dt=0
        // prevents stock time advancement but is not a side-effect contract for arbitrary mods.
        if (!RightEye()) return true;
        __instance.MainRenderLoop(0);
        return false;
    }
    private static bool CameraUpdate() => !RightEye();
    private static void CameraUpdated(PlayerCamera __instance)
    {
        if (Session(out var session) && session.OpenXrEye >= 0) session.ApplyEyeCamera(CameraClient(__instance));
    }
    private static bool Projection(ClientMain __instance, float __0)
    {
        if (!Session(out var session) || session.OpenXrEye < 0) return true;
        double[] matrix = Perspective(__instance);
        session.FillEyeProjection(matrix, __instance.MainCamera.ZNear, __0);
        __instance.GlMatrixModeProjection();
        __instance.GlLoadMatrix(matrix);
        __instance.shUniforms.ZNear = __instance.MainCamera.ZNear;
        __instance.shUniforms.ZFar = __0;
        __instance.GlMatrixModeModelView();
        return false;
    }

    private static int FindBeforeStage(IReadOnlyList<CodeInstruction> body)
    {
        for (int index = 3; index < body.Count; index++)
            if (body[index].Calls(Stage) && body[index - 3].opcode == OpCodes.Ldarg_0 &&
                body[index - 2].LoadsConstant((int)EnumRenderStage.Before) && body[index - 1].opcode == OpCodes.Ldarg_1)
                return index - 3;
        throw new InvalidOperationException("Official world-loop Before-stage anchor changed; stereo simulation split was rejected.");
    }
    private static IEnumerable<CodeInstruction> WorldTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var body = instructions.ToList();
        int before = FindBeforeStage(body);
        Label renderOnly = generator.DefineLabel();
        body[before].labels.Add(renderOnly);
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(OpenXrConsumerPatches), nameof(RightEye)));
        yield return new CodeInstruction(OpCodes.Brtrue, renderOnly);
        foreach (var instruction in body) yield return instruction;
    }
}
