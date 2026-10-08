using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using ErrorCallback = OpenTK.Windowing.GraphicsLibraryFramework.GLFWCallbacks.ErrorCallback;

namespace VulkanStory.Game;

/// <summary>Replaces pinned platform construction and first-window ownership seams, leaving renderer creation outside the native loader lock.</summary>
internal static unsafe class StartupWindowRoutingPatches
{
    private const string ConstructOwner = "vulkanstory.routing.platform-construction";
    private const string WindowOwner = "vulkanstory.routing.window-creation";
    private const string ShutdownOwner = "vulkanstory.routing.shutdown";
    private static ProcessRuntime? construction, creation, shutdown;
    private static MethodBase Target(string name) => StartupTargets.Resolve(
        name is "game.client.start" or "game.window.request" ? typeof(ClientProgram) : typeof(ClientPlatformWindows),
        StartupTargets.Profile1227.Single(target => target.Event == name));
    private static readonly MethodInfo ErrorSetter = typeof(GLFW).GetMethod(nameof(GLFW.SetErrorCallback), [typeof(ErrorCallback)])!;
    private static readonly MethodInfo CenterMethod = typeof(NativeWindow).GetMethod(nameof(NativeWindow.CenterWindow), Type.EmptyTypes)!;
    private static readonly MethodInfo SizeGetter = typeof(NativeWindow).GetProperty(nameof(NativeWindow.ClientSize))!.GetMethod!;
    private static readonly MethodInfo PointerGetter = typeof(NativeWindow).GetProperty(nameof(NativeWindow.WindowPtr))!.GetMethod!;
    private static readonly MethodInfo IconifyMethod = typeof(GLFW).GetMethod(nameof(GLFW.IconifyWindow), [typeof(Window).MakePointerType()])!;
    private static readonly MethodInfo DisposeMethod = typeof(NativeWindow).GetMethod(nameof(NativeWindow.Dispose), Type.EmptyTypes)!;

    internal static StartupPatchGroup CreateConstructionGroup(ProcessRuntime runtime)
    {
        var harmony = new Harmony(ConstructOwner); bool attempted = false;
        return new StartupPatchGroup("platform-construction", () => { _ = Target("game.platform.construct"); }, () =>
        {
            if (construction != null) throw new InvalidOperationException("Platform construction routing already has an owner.");
            construction = runtime; attempted = true;
            harmony.Patch(Target("game.platform.construct"), postfix: new HarmonyMethod(typeof(StartupWindowRoutingPatches), nameof(Constructed)));
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(ConstructOwner); if (ReferenceEquals(construction, runtime)) construction = null; attempted = false;
        });
    }
    private static void Constructed(ClientPlatformWindows __instance) => construction?.RememberPlatform(__instance);

    private static void Check(IEnumerable<CodeInstruction> instructions, bool closing)
    {
        var body = instructions.ToList();
        (MethodInfo Method, int Count)[] expected = closing
            ? [(PointerGetter, 1), (IconifyMethod, 1), (DisposeMethod, 1)]
            : [(ErrorSetter, 1), (CenterMethod, 1), (SizeGetter, 2)];
        foreach (var (method, count) in expected)
            if (body.Count(instruction => instruction.Calls(method)) != count)
                throw new InvalidOperationException("Original startup window anchor changed: " + method.Name);
        if (!closing && ErrorSetter.ReturnType != typeof(void))
        {
            int index = body.FindIndex(instruction => instruction.Calls(ErrorSetter));
            if (index + 1 >= body.Count || body[index + 1].opcode != OpCodes.Pop ||
                body[index + 1].labels.Count != 0 || body[index + 1].blocks.Count != 0)
                throw new InvalidOperationException("Original GLFW error callback result must be discarded immediately.");
        }
    }
    internal static StartupPatchGroup CreateWindowGroup(ProcessRuntime runtime)
    {
        var harmony = new Harmony(WindowOwner); bool attempted = false;
        return new StartupPatchGroup("window-creation", () =>
        { _ = Target("game.window.request"); Check(PatchProcessor.GetOriginalInstructions(Target("game.client.start")), false); }, () =>
        {
            if (creation != null) throw new InvalidOperationException("Window creation routing already has an owner.");
            creation = runtime; attempted = true;
            harmony.Patch(Target("game.window.request"), prefix: new HarmonyMethod(typeof(StartupWindowRoutingPatches), nameof(CreateWindow)) { priority = Priority.First });
            harmony.Patch(Target("game.client.start"), transpiler: new HarmonyMethod(typeof(StartupWindowRoutingPatches), nameof(WindowTranspiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(WindowOwner); if (ReferenceEquals(creation, runtime)) creation = null; attempted = false;
        });
    }
    private static bool CreateWindow(NativeWindowSettings __1, ref GameWindowNative __result)
    {
        var runtime = creation;
        if (runtime?.CanCreateWindow != true) return true;
        try
        {
            if (!runtime.SelectWindowServices()) return true;
            runtime.CreateWindow(__1); __result = null!; return false;
        }
        catch (Exception error) when (!HeadlessHarnessOptions.Enabled && !runtime.HasCommitted && error is not AggregateException)
        {
            runtime.Notify("runtime.initialization.failed", error.Message + "; partial session released, continuing original startup.");
            return true;
        }
    }
    private static IEnumerable<CodeInstruction> WindowTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body, false);
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            string? wrapper = instruction.Calls(ErrorSetter) ? nameof(SetErrorCallback) :
                instruction.Calls(CenterMethod) ? nameof(Center) : instruction.Calls(SizeGetter) ? nameof(Size) : null;
            if (wrapper != null)
            {
                bool discard = instruction.Calls(ErrorSetter) && ErrorSetter.ReturnType != typeof(void);
                instruction.opcode = OpCodes.Call;
                instruction.operand = typeof(StartupWindowRoutingPatches).GetMethod(wrapper, BindingFlags.Static | BindingFlags.NonPublic)!;
                yield return instruction;
                if (discard) index++;
            }
            else yield return instruction;
        }
    }
    private static void SetErrorCallback(ErrorCallback callback)
    { if (creation?.CanCreateWindow != true && creation?.HasCommitted != true) GLFW.SetErrorCallback(callback); }
    private static void Center(NativeWindow window)
    { if (creation?.HasCommitted != true) { window.CenterWindow(); return; } creation.Session.Window.Center(); }
    private static Vector2i Size(NativeWindow window)
    {
        if (creation?.HasCommitted != true) return window.ClientSize;
        var pixels = creation.Session.Window.PixelSize; return new Vector2i(pixels.Width, pixels.Height);
    }
    internal static StartupPatchGroup CreateShutdownGroup(ProcessRuntime runtime)
    {
        var harmony = new Harmony(ShutdownOwner); bool attempted = false;
        return new StartupPatchGroup("shutdown", () => Check(PatchProcessor.GetOriginalInstructions(Target("game.client.start")), true), () =>
        {
            if (shutdown != null) throw new InvalidOperationException("Shutdown routing already has an owner.");
            shutdown = runtime; attempted = true;
            harmony.Patch(Target("game.client.start"), transpiler: new HarmonyMethod(typeof(StartupWindowRoutingPatches), nameof(ShutdownTranspiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(ShutdownOwner); if (ReferenceEquals(shutdown, runtime)) shutdown = null; attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> ShutdownTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body, true);
        foreach (var instruction in body)
        {
            string? wrapper = instruction.Calls(PointerGetter) ? nameof(Pointer) :
                instruction.Calls(IconifyMethod) ? nameof(Iconify) : instruction.Calls(DisposeMethod) ? nameof(Dispose) : null;
            if (wrapper != null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = typeof(StartupWindowRoutingPatches).GetMethod(wrapper, BindingFlags.Static | BindingFlags.NonPublic)!;
            }
        }
        return body;
    }
    private static Window* Pointer(NativeWindow window) => shutdown?.HasCommitted == true ? null : window.WindowPtr;
    private static void Iconify(Window* window)
    { if (shutdown?.HasCommitted != true) { GLFW.IconifyWindow(window); return; } shutdown.Session.Window.Minimize(); }
    private static void Dispose(NativeWindow window)
    { if (shutdown?.HasCommitted != true) { window.Dispose(); return; } shutdown.Shutdown(); }
}
