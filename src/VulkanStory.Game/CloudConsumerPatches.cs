using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Built-in assembly supplied by the version/profile owner; no forked DLL dependency.
internal static class CloudConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-cloud-volumetric";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Enable = typeof(GL).GetMethod(nameof(GL.Enable), [typeof(EnableCap)])!;
    private static readonly MethodInfo Disable = typeof(GL).GetMethod(nameof(GL.Disable), [typeof(EnableCap)])!;
    private static readonly MethodInfo Mesh = AccessTools.Method(typeof(IRenderAPI), "RenderMesh", [typeof(MeshRef)])!;
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(Enable)) != 2 ||
            body.Count(instruction => instruction.Calls(Disable)) != 1 || body.Count(instruction => instruction.Calls(Mesh)) != 1)
            throw new InvalidOperationException("Original volumetric cloud state/draw anchors changed.");
    }
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner, Assembly essentials)
    {
        Type type = essentials.GetType("FluffyClouds.CloudRendererVolumetric", throwOnError: true)!;
        MethodInfo render = type.GetMethod("OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]) ??
            throw new MissingMethodException(type.FullName, "OnRenderFrame");
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-cloud-volumetric", () =>
        {
            if (render.ReturnType != typeof(void) || render.IsStatic || render.GetMethodBody() == null)
                throw new InvalidOperationException("Original volumetric cloud renderer changed.");
            Check(PatchProcessor.GetOriginalInstructions(render));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Cloud routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(render, transpiler: new HarmonyMethod(typeof(CloudConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
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
            string? name = instruction.Calls(Enable) ? nameof(EnableState) : instruction.Calls(Disable) ? nameof(DisableState) :
                instruction.Calls(Mesh) ? nameof(DrawCloud) : null;
            if (name != null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(CloudConsumerPatches), name); }
        }
        return body;
    }
    private static GameGraphicsAdapter? Adapter()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active cloud routing lost its session.");
        return session.Graphics;
    }
    private static void EnableState(EnableCap cap)
    {
        if (Adapter() is not { } graphics) { GL.Enable(cap); return; }
        var state = graphics.RequireStatedState();
        if ((int)cap == 2929) state.DepthTest = true;
        else if ((int)cap == 3042) state.SetBlendEnabled(true);
        else throw new NotSupportedException("Unexpected cloud enable capability.");
    }
    private static void DisableState(EnableCap cap)
    {
        if (Adapter() is not { } graphics) { GL.Disable(cap); return; }
        if ((int)cap != 2929) throw new NotSupportedException("Unexpected cloud disable capability.");
        graphics.RequireStatedState().DepthTest = false;
    }
    private static void DrawCloud(IRenderAPI render, MeshRef mesh)
    { if (Adapter() is { } graphics) graphics.RenderCloudVolumetric(mesh); else render.RenderMesh(mesh); }
}
