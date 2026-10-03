using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Checked substitutions inside original framebuffer/post methods.</summary>
internal static class FramebufferDrawBufferRoutes
{
    private sealed record Route(string Name, Type Result, Type[] Parameters, int Single, int Multiple, int ColorClears = 0);
    private static readonly Route[] Profile1227 =
    [
        new("CreateFramebuffer", typeof(FrameBufferRef), [typeof(FramebufferAttrs)], 0, 1),
        new("SetupDefaultFrameBuffers", typeof(List<FrameBufferRef>), [], 13, 3),
        new("ClearFrameBuffer", typeof(void), [typeof(EnumFrameBuffer)], 1, 0, 7),
        new("LoadFrameBuffer", typeof(void), [typeof(EnumFrameBuffer)], 2, 1),
        new("UnloadFrameBuffer", typeof(void), [typeof(EnumFrameBuffer)], 1, 0),
        new("MergeTransparentRenderPass", typeof(void), [], 1, 0),
        new("RenderFinalComposition", typeof(void), [], 0, 3),
        new("RenderPostprocessingEffects", typeof(void), [typeof(float[])], 0, 0, 1),
    ];
    private static readonly MethodInfo GlSingle = Gl(nameof(GL.DrawBuffer), [typeof(DrawBufferMode)]);
    private static readonly MethodInfo GlMultiple = Gl(nameof(GL.DrawBuffers), [typeof(int), typeof(DrawBuffersEnum[])]);
    private static readonly MethodInfo GlColorClear = Gl(nameof(GL.ClearBuffer), [typeof(ClearBuffer), typeof(int), typeof(float[])]);

    private static MethodInfo Gl(string name, Type[] parameters) => typeof(GL).GetMethod(name, parameters) ??
        throw new MissingMethodException("Original framebuffer GL overload is missing: " + name);

    private static MethodInfo Target(Route route)
    {
        var method = typeof(ClientPlatformWindows).GetMethod(route.Name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null, types: route.Parameters, modifiers: null);
        if (method is null || method.ReturnType != route.Result || method.GetMethodBody() is null)
            throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, route.Name);
        return method;
    }

    private static void Check(Route route, IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(GlSingle)) != route.Single ||
            body.Count(instruction => instruction.Calls(GlMultiple)) != route.Multiple ||
            body.Count(instruction => instruction.Calls(GlColorClear)) != route.ColorClears)
            throw new InvalidOperationException("Official framebuffer selection/clear anchors changed: " + route.Name);
    }

    internal static void ValidateBindings()
    {
        foreach (var route in Profile1227) Check(route, PatchProcessor.GetOriginalInstructions(Target(route)));
    }

    internal static void Install(Harmony harmony)
    {
        foreach (var route in Profile1227)
            harmony.Patch(Target(route), transpiler: new HarmonyMethod(typeof(FramebufferDrawBufferRoutes),
                nameof(Transpiler)) { priority = Priority.First });
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        Route route = Profile1227.Single(candidate => Target(candidate).Equals(__originalMethod));
        var body = instructions.ToList();
        Check(route, body); // Incoming Harmony IL must still match, including other owners.
        foreach (var instruction in body)
        {
            string? wrapper = instruction.Calls(GlSingle) ? nameof(SelectSingle) :
                instruction.Calls(GlMultiple) ? nameof(SelectMultiple) :
                instruction.Calls(GlColorClear) ? nameof(ClearColorBuffer) : null;
            if (wrapper != null)
            {
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                receiver.labels.AddRange(instruction.labels);
                receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.labels.Clear();
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return receiver;
                instruction.opcode = OpCodes.Call;
                instruction.operand = typeof(FramebufferDrawBufferRoutes).GetMethod(wrapper,
                    BindingFlags.Static | BindingFlags.NonPublic)!;
            }
            yield return instruction;
        }
    }

    internal static void SelectSingle(DrawBufferMode selector, ClientPlatformWindows platform)
    {
        if (!FramebufferConsumerPatches.TryAdapter(platform, out var adapter)) { GL.DrawBuffer(selector); return; }
        adapter.SelectDrawBuffer(selector);
    }

    internal static void SelectMultiple(int count, DrawBuffersEnum[] selectors, ClientPlatformWindows platform)
    {
        if (!FramebufferConsumerPatches.TryAdapter(platform, out var adapter)) { GL.DrawBuffers(count, selectors); return; }
        adapter.SelectDrawBuffers(count, selectors);
    }

    internal static void ClearColorBuffer(ClearBuffer kind, int slot, float[] color, ClientPlatformWindows platform)
    {
        if (!FramebufferConsumerPatches.TryAdapter(platform, out var adapter)) { GL.ClearBuffer(kind, slot, color); return; }
        if ((int)kind != 6144) throw new NotSupportedException("This framebuffer clear route only accepts color buffers.");
        adapter.ClearCurrentColor(slot, color);
    }
}
