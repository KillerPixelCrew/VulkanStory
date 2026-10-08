using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Translates selected direct scene GL state calls and restores water state through Harmony finalizers.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class SceneRawStateConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-scene-raw-state";
    private static ProcessRuntime? runtime;
    private static MethodInfo? waterTarget;
    private static readonly MethodInfo DrawBuffers = typeof(GL).GetMethod(nameof(GL.DrawBuffers), [typeof(int), typeof(DrawBuffersEnum[])])!;
    private static readonly MethodInfo Parameter = typeof(GL).GetMethod(nameof(GL.TexParameter),
        [typeof(TextureTarget), typeof(TextureParameterName), typeof(int)])!;
    private static readonly Type DebugSystem = typeof(ClientMain).Assembly.GetType(
        "Vintagestory.Client.NoObf.SystemRenderFrameBufferDebug", throwOnError: true)!;
    private static readonly MethodInfo Debug = AccessTools.Method(DebugSystem, "OnRenderFrame2DOverlay", [typeof(float)])!;
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <param name="survival">Original Survival assembly containing the pinned built-in consumers.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly survival)
    {
        Type water = survival.GetType("Vintagestory.GameContent.EntityBehaviorHideWaterSurface", true)!;
        MethodInfo target = AccessTools.Method(water, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]) ??
            throw new MissingMethodException(water.FullName, "OnRenderFrame");
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-raw-state", () =>
        {
            if (!typeof(ClientSystem).IsAssignableFrom(DebugSystem))
                throw new InvalidOperationException("Original framebuffer debugger no longer derives from ClientSystem.");
            foreach (var method in new[] { target, Debug })
                if (method == null || method.ReturnType != typeof(void) || method.GetMethodBody() == null)
                    throw new InvalidOperationException("Original scene raw-state target changed.");
            Check(PatchProcessor.GetOriginalInstructions(target), DrawBuffers, 2);
            Check(PatchProcessor.GetOriginalInstructions(Debug), Parameter, 6);
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Scene raw-state routing already has an owner.");
            runtime = owner; waterTarget = target; attempted = true;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(SceneRawStateConsumerPatches), nameof(SaveWaterState)),
                transpiler: new HarmonyMethod(typeof(SceneRawStateConsumerPatches), nameof(Transpiler)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(SceneRawStateConsumerPatches), nameof(WaterFailure)));
            harmony.Patch(Debug, transpiler: new HarmonyMethod(typeof(SceneRawStateConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(runtime, owner)) { runtime = null; waterTarget = null; }
            attempted = false;
        });
    }
    private static void Check(IEnumerable<CodeInstruction> instructions, MethodInfo call, int count)
    {
        if (instructions.Count(instruction => instruction.Calls(call)) != count)
            throw new InvalidOperationException("Original/incoming scene raw-state anchors changed: " + call.Name);
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var body = instructions.ToList(); bool water = Equals(__originalMethod, waterTarget);
        MethodInfo call = water ? DrawBuffers : Parameter;
        Check(body, call, water ? 2 : 6);
        foreach (var instruction in body)
            if (instruction.Calls(call))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SceneRawStateConsumerPatches), water ? nameof(SetDrawBuffers) : nameof(SetDebugParameter));
            }
        return body;
    }
    private static GameGraphicsAdapter? Adapter()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active scene raw-state routing lost its session.");
        return session.Graphics;
    }
    private static void SetDrawBuffers(int count, DrawBuffersEnum[] buffers)
    {
        if (Adapter() is not { } graphics) { GL.DrawBuffers(count, buffers); return; }
        if (count < 0 || count > buffers.Length || count > 32) throw new NotSupportedException("Unexpected water mask draw-buffer count.");
        uint mask = 0;
        for (int index = 0; index < count; index++)
        {
            int slot = (int)buffers[index] - (int)DrawBuffersEnum.ColorAttachment0;
            if (slot != index) throw new NotSupportedException("Water mask requires identity attachment routing.");
            mask |= 1u << slot;
        }
        graphics.RequireStatedState().SetDrawBuffers(graphics.CurrentTargetId, mask);
    }
    private static void SetDebugParameter(TextureTarget target, TextureParameterName parameter, int value)
    {
        if (Adapter() is not { } graphics) { GL.TexParameter(target, parameter, value); return; }
        if ((int)target != 3553 || (int)parameter != 34892 || value is not (0 or 34894))
            throw new NotSupportedException("Unexpected framebuffer debug texture state.");
        graphics.SetDebugDepthComparison(value);
    }
    private sealed record WaterState(GameGraphicsAdapter Graphics, int Target, uint Mask, bool DepthTest, bool DepthWrite);
    private static void SaveWaterState(out WaterState? __state)
    {
        __state = null;
        if (Adapter() is not { } graphics) return;
        var stated = graphics.RequireStatedState();
        __state = new WaterState(graphics, graphics.CurrentTargetId, stated.DrawBuffers(graphics.CurrentTargetId), stated.DepthTest, stated.DepthWrite);
    }
    private static Exception? WaterFailure(Exception? __exception, WaterState? __state)
    {
        if (__exception != null && __state != null)
        {
            var stated = __state.Graphics.RequireStatedState();
            stated.SetDrawBuffers(__state.Target, __state.Mask);
            stated.DepthTest = __state.DepthTest; stated.DepthWrite = __state.DepthWrite;
        }
        return __exception;
    }
}
