using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Substitutes pinned GL cleanup calls from original shader disposal with the adapter resource lifecycle.</summary>
internal static class ShaderDisposalRoutes
{
    private sealed record Route(string GlName, Type[] Parameters, int Count, string Wrapper);
    private static readonly Route[] Routes =
    [
        new("DetachShader", [typeof(int), typeof(int)], 3, nameof(Detach)),
        new("DeleteShader", [typeof(int)], 3, nameof(DeleteShader)),
        new("DeleteSampler", [typeof(int)], 1, nameof(DeleteSampler)),
        new("DeleteProgram", [typeof(int)], 1, nameof(DeleteProgram)),
    ];
    private static MethodInfo Target => typeof(ShaderProgramBase).GetMethod(nameof(ShaderProgramBase.Dispose),
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, binder: null,
        types: Type.EmptyTypes, modifiers: null) ?? throw new MissingMethodException("Original shader disposal is missing.");
    private static MethodInfo Gl(Route route) => typeof(GL).GetMethod(route.GlName, route.Parameters) ??
        throw new MissingMethodException("Original shader disposal GL overload is missing.");
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        foreach (var route in Routes)
            if (body.Count(instruction => instruction.Calls(Gl(route))) != route.Count)
                throw new InvalidOperationException("Original shader disposal has an unexpected " + route.GlName + " count.");
    }
    /// <summary>Checks the original shader-disposal calls and signatures before installation.</summary>
    internal static void ValidateBindings()
    {
        if (Target.ReturnType != typeof(void) || Target.GetMethodBody() is null)
            throw new InvalidOperationException("Original shader disposal signature changed.");
        Check(PatchProcessor.GetOriginalInstructions(Target));
    }
    /// <summary>Installs the disposal transpiler without enabling graphics routing.</summary>
    /// <param name="harmony">Startup Harmony owner responsible for removing the patch.</param>
    internal static void Install(Harmony harmony) => harmony.Patch(Target,
        transpiler: new HarmonyMethod(typeof(ShaderDisposalRoutes), nameof(Transpiler)));
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        Check(body);
        foreach (var instruction in body)
            foreach (var route in Routes)
                if (instruction.Calls(Gl(route)))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = typeof(ShaderDisposalRoutes).GetMethod(route.Wrapper,
                        BindingFlags.Static | BindingFlags.NonPublic)!;
                    break;
                }
        return body;
    }
    private static void Detach(int program, int shader)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.DetachShader(program, shader); return; }
        adapter.DetachShader(program, shader);
    }
    private static void DeleteShader(int shader)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.DeleteShader(shader); return; }
        adapter.DeleteShader(shader);
    }
    private static void DeleteSampler(int sampler)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.DeleteSampler(sampler); return; }
        adapter.DeleteSampler(sampler);
    }
    private static void DeleteProgram(int program)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.DeleteProgram(program); return; }
        adapter.DeleteProgram(program);
    }
}
