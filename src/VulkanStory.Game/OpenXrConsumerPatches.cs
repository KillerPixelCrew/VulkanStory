using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
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
        }, () =>
        {
            runtime = owner; installed = true;
            harmony.Patch(Render, prefix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(StereoRender)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(ClientMain), nameof(ClientMain.MainGameLoop), [typeof(float)]),
                prefix: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(GameLoop)) { priority = Priority.First });
            harmony.Patch(WorldRender, transpiler: new HarmonyMethod(typeof(OpenXrConsumerPatches), nameof(WorldTranspiler)) { priority = Priority.First });
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
