using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class UiConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-ui";
    private static ProcessRuntime? runtime;
    private static int tracedScreenFrames;
    private static bool captureScreenCalls;
    private static string lastScreenCall = "not captured";
    private static readonly MethodInfo Blit = AccessTools.Method(typeof(ClientPlatformWindows), "BlitPrimaryToDefault", [])!;
    private static readonly MethodInfo ScreenRender = AccessTools.Method(typeof(ScreenManager), "Render", [typeof(float)])!;
    private static readonly MethodInfo ClientRender = AccessTools.Method(typeof(ClientMain), "RenderToDefaultFramebuffer", [typeof(float)])!;
    private static readonly MethodInfo Stage = AccessTools.Method(typeof(ClientMain), "TriggerRenderStage", [typeof(EnumRenderStage), typeof(float)])!;
    private static readonly MethodInfo ReloadShaders = AccessTools.Method(typeof(ShaderRegistry), "ReloadShaders", [])!;
    private static readonly MethodInfo ClientOrtho = AccessTools.Method(typeof(ClientMain), "OrthoMode", [typeof(int), typeof(int), typeof(bool)])!;
    private static readonly MethodInfo ClientPerspective = AccessTools.Method(typeof(ClientMain), "PerspectiveMode", [])!;
    private static readonly MethodInfo ClearDepth = typeof(GL).GetMethod(nameof(GL.ClearBuffer), [typeof(ClearBuffer), typeof(int), typeof(float).MakeByRefType()]) ??
        throw new MissingMethodException("Original screen depth-clear overload is missing.");
    private static readonly MethodInfo DepthRange = typeof(GL).GetMethod(nameof(GL.DepthRange), [typeof(float), typeof(float)]) ??
        typeof(GL).GetMethod(nameof(GL.DepthRange), [typeof(double), typeof(double)]) ??
        throw new MissingMethodException("Original screen depth-range overload is missing.");

    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner);
        bool attempted = false;
        return new StartupPatchGroup("graphics-ui", () =>
        {
            foreach (MethodInfo? method in new[] { Blit, ScreenRender, ClientRender, Stage })
                if (method?.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    throw new MissingMethodException("Original UI composition target changed.");
            CheckScreen(PatchProcessor.GetOriginalInstructions(ScreenRender));
            DoneAnchor(PatchProcessor.GetOriginalInstructions(ClientRender));
            foreach (var method in new[] { ClientOrtho, ClientPerspective })
            {
                if (method?.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    throw new MissingMethodException("Original client projection mode changed.");
                CheckClientDepth(PatchProcessor.GetOriginalInstructions(method));
            }
            if (ReloadShaders?.ReturnType != typeof(bool) || !ReloadShaders.IsStatic || ReloadShaders.GetMethodBody() == null)
                throw new MissingMethodException("Original shader reload target changed.");
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("UI routing already has an owner.");
            runtime = owner; attempted = true;
            tracedScreenFrames = 0; captureScreenCalls = false; lastScreenCall = "not captured";
            harmony.Patch(Blit, postfix: new HarmonyMethod(typeof(UiConsumerPatches), nameof(AfterBlit)));
            harmony.Patch(ScreenRender, prefix: new HarmonyMethod(typeof(UiConsumerPatches), nameof(BeforeScreen)),
                postfix: new HarmonyMethod(typeof(UiConsumerPatches), nameof(AfterScreen)),
                transpiler: new HarmonyMethod(typeof(UiConsumerPatches), nameof(ScreenTranspiler)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(UiConsumerPatches), nameof(ScreenFailure)));
            harmony.Patch(ClientRender, transpiler: new HarmonyMethod(typeof(UiConsumerPatches), nameof(ClientTranspiler)) { priority = Priority.First });
            foreach (var method in new[] { ClientOrtho, ClientPerspective })
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(UiConsumerPatches), nameof(ClientDepthTranspiler)) { priority = Priority.First });
            harmony.Patch(ReloadShaders, prefix: new HarmonyMethod(typeof(UiConsumerPatches), nameof(ReloadUiShader)));
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static bool TryGraphics(ClientPlatformWindows platform, out GameGraphicsAdapter graphics)
    {
        graphics = null!;
        if (runtime?.Routing.RoutingEnabled != true) return false;
        if (!runtime.TrySession(platform, out var session)) throw new InvalidOperationException("Active UI routing lost its session.");
        graphics = session.Graphics;
        return true;
    }
    private static bool TryScreen(out GameGraphicsAdapter graphics)
    {
        graphics = null!;
        return ScreenManager.Platform is ClientPlatformWindows platform && TryGraphics(platform, out graphics);
    }
    private static void AfterBlit(ClientPlatformWindows __instance)
    {
        if (!TryGraphics(__instance, out var graphics)) return;
        graphics.CaptureSceneNoHud();
        graphics.OpenUiScope();
    }
    private static void AfterScreen()
    {
        NoteScreenCall("UI postfix: ComposeUiTarget");
        if (TryScreen(out var graphics)) graphics.ComposeUiTarget();
    }
    private static void BeforeScreen()
    {
        captureScreenCalls = runtime?.IsActive == true && ++tracedScreenFrames <= 4;
        lastScreenCall = captureScreenCalls ? "screen entry" : "not captured (frame bound exceeded or routing inactive)";
    }
    private static void NoteScreenCall(string call)
    {
        if (captureScreenCalls) lastScreenCall = call;
    }
    private static void ReloadUiShader()
    {
        if (!TryScreen(out var graphics)) return;
        graphics.ReloadUiProgram(); graphics.ReloadTemporalPrograms();
        graphics.ReloadAmbientOcclusionProgram();
        graphics.ReloadBlitPrograms();
        graphics.ReloadSkyMotionProgram();
        graphics.ReloadLiquidMotionProgram();
        runtime!.Session.Temporal.State.RequestReset(EnumTemporalResetReason.ShaderReload);
    }
    private static Exception? ScreenFailure(Exception? __exception)
    {
        if (__exception == null) return null;
        runtime?.Notify("render.screen.failed", $"frame={tracedScreenFrames}; lastCall={lastScreenCall}\n{__exception}");
        try { if (TryScreen(out var graphics)) graphics.AbortUiScope(); }
        catch (Exception cleanup) { runtime?.Notify("render.screen.cleanup.failed", cleanup.ToString()); }
        // The trace keeps the first throw's detail before generated finalizer
        // rethrowing; cleanup errors must not replace the original exception.
        return __exception;
    }
    private static void ComposeClient(ClientMain client)
    {
        if (client.Platform is ClientPlatformWindows platform && TryGraphics(platform, out var graphics))
            graphics.ComposeUiTarget();
    }
    private static void CheckScreen(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(ClearDepth)) != 1 ||
            body.Count(instruction => instruction.Calls(DepthRange)) != 1)
            throw new InvalidOperationException("Official screen UI depth anchors changed.");
    }
    private static bool Constant(CodeInstruction instruction, int value)
    {
        if (instruction.opcode == OpCodes.Ldc_I4 || instruction.opcode == OpCodes.Ldc_I4_S)
            return Convert.ToInt32(instruction.operand) == value;
        OpCode[] compact = [OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_2, OpCodes.Ldc_I4_3,
            OpCodes.Ldc_I4_4, OpCodes.Ldc_I4_5, OpCodes.Ldc_I4_6, OpCodes.Ldc_I4_7, OpCodes.Ldc_I4_8];
        return value is >= 0 and <= 8 && instruction.opcode == compact[value];
    }
    private static int DoneAnchor(IReadOnlyList<CodeInstruction> body)
    {
        var matches = new List<int>();
        for (int index = 3; index < body.Count; index++)
            if (body[index].Calls(Stage) && Constant(body[index - 2], (int)EnumRenderStage.Done) &&
                body[index - 3].opcode == OpCodes.Ldarg_0 && body[index - 1].opcode == OpCodes.Ldarg_1)
                matches.Add(index - 3);
        if (matches.Count != 1 || body.Count(instruction => instruction.Calls(Stage)) != 2)
            throw new InvalidOperationException("Official UI Done-stage composition anchor changed.");
        return matches[0];
    }
    private static IEnumerable<CodeInstruction> ClientTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        int beforeDone = DoneAnchor(body);
        var receiver = new CodeInstruction(OpCodes.Ldarg_0);
        receiver.labels.AddRange(body[beforeDone].labels); body[beforeDone].labels.Clear();
        receiver.blocks.AddRange(body[beforeDone].blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
        body[beforeDone].blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
        body.InsertRange(beforeDone, [receiver, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(UiConsumerPatches), nameof(ComposeClient)))]);
        return body;
    }
    private static IEnumerable<CodeInstruction> ClientDepthTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); CheckClientDepth(body);
        foreach (var instruction in body)
            if (instruction.Calls(DepthRange))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(UiConsumerPatches),
                    DepthRange.GetParameters()[0].ParameterType == typeof(float) ? nameof(ScreenDepthRange) : nameof(ScreenDepthRangeDouble));
            }
        return body;
    }
    private static void CheckClientDepth(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(DepthRange)) != 1)
            throw new InvalidOperationException("Official client UI depth-range anchors changed.");
    }
    private static IEnumerable<CodeInstruction> ScreenTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); CheckScreen(body);
        var markers = new Dictionary<int, string>();
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            OpCode opcode = instruction.opcode;
            if (instruction.operand is MethodBase called &&
                (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj))
            {
                int start = index;
                while (start > 0 && body[start - 1].opcode.OpCodeType == OpCodeType.Prefix) start--;
                markers[start] = $"instruction={index}; {called.DeclaringType?.FullName}.{called}";
            }
            string? wrapper = instruction.Calls(ClearDepth) ? nameof(ClearScreenDepth) :
                instruction.Calls(DepthRange) ? DepthRange.GetParameters()[0].ParameterType == typeof(float)
                    ? nameof(ScreenDepthRange) : nameof(ScreenDepthRangeDouble) : null;
            if (wrapper == null) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(typeof(UiConsumerPatches), wrapper);
        }
        var result = new List<CodeInstruction>();
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (markers.TryGetValue(index, out string? call))
            {
                // Existing arguments remain on the stack. The marker consumes
                // only its string; prefixes stay adjacent to their original call.
                var marker = new CodeInstruction(OpCodes.Ldstr, call);
                marker.labels.AddRange(instruction.labels); instruction.labels.Clear();
                marker.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                result.Add(marker);
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UiConsumerPatches), nameof(NoteScreenCall))));
            }
            result.Add(instruction);
        }
        return result;
    }
    private static void ClearScreenDepth(ClearBuffer kind, int slot, ref float depth)
    {
        if (!TryScreen(out var graphics)) { GL.ClearBuffer(kind, slot, ref depth); return; }
        if ((int)kind != 6145 || slot != 0) throw new NotSupportedException("Unexpected screen UI depth clear.");
        graphics.ClearUiOrDefaultDepth(Math.Clamp(depth, 0f, 1f));
    }
    private static void ScreenDepthRange(float near, float far)
    {
        if (!TryScreen(out _)) { GL.DepthRange(near, far); return; }
        RequireDefaultDepthRange(near, far);
    }
    private static void ScreenDepthRangeDouble(double near, double far)
    {
        if (!TryScreen(out _)) { GL.DepthRange(near, far); return; }
        RequireDefaultDepthRange(near, far);
    }
    private static void RequireDefaultDepthRange(double near, double far)
    {
        // Vanilla's 0..20000 values clamp to 0..1 in GL, matching the retained Vulkan viewport.
        if (Math.Clamp(near, 0d, 1d) != 0d || Math.Clamp(far, 0d, 1d) != 1d)
            throw new NotSupportedException("The original UI depth-range profile changed.");
    }
}
