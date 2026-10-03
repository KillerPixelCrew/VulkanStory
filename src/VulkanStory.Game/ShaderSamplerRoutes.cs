using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class ShaderSamplerRoutes
{
    private static MethodInfo PlatformSampler => Target(typeof(ClientPlatformWindows), "GenSampler", typeof(int), [typeof(bool)]);
    private static MethodInfo Texture2D => Target(typeof(ShaderProgramBase), "BindTexture2D", typeof(void), [typeof(string), typeof(int), typeof(int)]);
    private static MethodInfo TextureCube => Target(typeof(ShaderProgramBase), "BindTextureCube", typeof(void), [typeof(string), typeof(int), typeof(int)]);
    private static MethodInfo Stop => Target(typeof(ShaderProgramBase), "Stop", typeof(void), []);
    private static readonly MethodInfo GlBind = typeof(GL).GetMethod(nameof(GL.BindSampler), [typeof(int), typeof(int)])!;

    private static MethodInfo Target(Type type, string name, Type result, Type[] parameters)
    {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null, types: parameters, modifiers: null);
        if (method is null || method.ReturnType != result || method.GetMethodBody() is null)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static void Check(IEnumerable<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(GlBind)) != 1)
            throw new InvalidOperationException("Official shader Stop must contain exactly one GL.BindSampler(int,int) call.");
    }

    internal static void ValidateBindings()
    {
        _ = PlatformSampler; _ = Texture2D; _ = TextureCube;
        Check(PatchProcessor.GetOriginalInstructions(Stop));
    }
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(PlatformSampler, prefix: new HarmonyMethod(typeof(ShaderSamplerRoutes), nameof(Create)));
        harmony.Patch(Texture2D, prefix: new HarmonyMethod(typeof(ShaderSamplerRoutes), nameof(Bind2D)));
        harmony.Patch(TextureCube, prefix: new HarmonyMethod(typeof(ShaderSamplerRoutes), nameof(BindCube)));
        harmony.Patch(Stop, transpiler: new HarmonyMethod(typeof(ShaderSamplerRoutes), nameof(StopTranspiler)));
    }
    private static bool Create(ClientPlatformWindows __instance, bool __0, ref int __result)
    {
        if (!ShaderConsumerPatches.TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.GenSampler(__0);
        return false;
    }
    private static bool Bind2D(ShaderProgramBase __instance, string __0, int __1, int __2)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) return true;
        adapter.BindProgramTexture(__instance, __0, __1, __2, cube: false);
        return false;
    }
    private static bool BindCube(ShaderProgramBase __instance, string __0, int __1, int __2)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) return true;
        adapter.BindProgramTexture(__instance, __0, __1, __2, cube: true);
        return false;
    }
    private static IEnumerable<CodeInstruction> StopTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        Check(body);
        var routed = typeof(ShaderSamplerRoutes).GetMethod(nameof(Unbind), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var instruction in body)
            if (instruction.Calls(GlBind)) { instruction.opcode = OpCodes.Call; instruction.operand = routed; }
        return body;
    }
    private static void Unbind(int unit, int sampler)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.BindSampler(unit, sampler); return; }
        adapter.BindSampler(unit, sampler);
    }
}
