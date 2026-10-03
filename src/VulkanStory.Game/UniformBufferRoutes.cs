using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class UniformBufferRoutes
{
    private static MethodInfo CreateTarget => Target(typeof(ClientPlatformWindows), "CreateUBO", typeof(UBORef),
        [typeof(int), typeof(int), typeof(string), typeof(int)]);
    private static MethodInfo BindTarget => Target(typeof(UBO), "Bind", typeof(void), []);
    private static MethodInfo UnbindTarget => Target(typeof(UBO), "Unbind", typeof(void), []);
    private static MethodInfo UpdateTarget => Target(typeof(UBO), "Update", typeof(void), [typeof(object), typeof(int), typeof(int)]);
    private static MethodInfo DisposeTarget => Target(typeof(UBO), "Dispose", typeof(void), []);
    private static readonly MethodInfo GlDelete = typeof(GL).GetMethod(nameof(GL.DeleteBuffers), [typeof(int), typeof(int).MakeByRefType()])!;

    private static MethodInfo Target(Type type, string name, Type result, Type[] parameters)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: parameters, modifiers: null);
        if (method is null || method.ReturnType != result || method.ContainsGenericParameters || method.GetMethodBody() is null)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }
    private static void Check(IEnumerable<CodeInstruction> instructions)
    {
        if (instructions.Count(instruction => instruction.Calls(GlDelete)) != 1)
            throw new InvalidOperationException("UBO disposal must contain exactly one expected GL.DeleteBuffers call.");
    }
    internal static void ValidateBindings()
    {
        _ = CreateTarget; _ = BindTarget; _ = UnbindTarget; _ = UpdateTarget;
        Check(PatchProcessor.GetOriginalInstructions(DisposeTarget));
        UniformBufferUploads.ValidateBindings();
    }
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(CreateTarget, prefix: new HarmonyMethod(typeof(UniformBufferRoutes), nameof(Create)));
        harmony.Patch(BindTarget, prefix: new HarmonyMethod(typeof(UniformBufferRoutes), nameof(Bind)));
        harmony.Patch(UnbindTarget, prefix: new HarmonyMethod(typeof(UniformBufferRoutes), nameof(Unbind)));
        harmony.Patch(UpdateTarget, prefix: new HarmonyMethod(typeof(UniformBufferRoutes), nameof(Update)));
        harmony.Patch(DisposeTarget, transpiler: new HarmonyMethod(typeof(UniformBufferRoutes), nameof(DisposeTranspiler)));
        UniformBufferUploads.Install(harmony);
    }
    private static bool Create(ClientPlatformWindows __instance, int __0, int __1, string __2, int __3, ref UBORef __result)
    {
        if (!ShaderConsumerPatches.TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.CreateUniformBuffer(__0, __1, __2, __3);
        return false;
    }
    private static bool Bind(UBO __instance)
    {
        if (!ShaderConsumerPatches.GraphicsRoutingEnabled) return true;
        GameGraphicsAdapter.UniformBufferOwner(__instance).BindUniformBuffer(__instance);
        UniformBufferUploads.NoteBound(__instance);
        return false;
    }
    private static bool Unbind(UBO __instance)
    {
        if (!ShaderConsumerPatches.GraphicsRoutingEnabled) return true;
        GameGraphicsAdapter.UniformBufferOwner(__instance).UnbindUniformBuffer(__instance);
        UniformBufferUploads.Clear();
        return false;
    }
    private static bool Update(UBO __instance, object __0, int __1, int __2)
    {
        if (!ShaderConsumerPatches.GraphicsRoutingEnabled) return true;
        GameGraphicsAdapter.UniformBufferOwner(__instance).UpdateUniformBuffer(__instance, __0, __1, __2);
        return false;
    }
    private static IEnumerable<CodeInstruction> DisposeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        Check(body);
        var wrapper = typeof(UniformBufferRoutes).GetMethod(nameof(Delete), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var instruction in body)
        {
            if (instruction.Calls(GlDelete))
            {
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                receiver.labels.AddRange(instruction.labels);
                receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.labels.Clear();
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return receiver;
                instruction.opcode = OpCodes.Call;
                instruction.operand = wrapper;
            }
            yield return instruction;
        }
    }
    private static void Delete(int count, ref int handle, UBO buffer)
    {
        if (!ShaderConsumerPatches.GraphicsRoutingEnabled) { GL.DeleteBuffers(count, ref handle); return; }
        if (count != 1) throw new InvalidOperationException("Official UBO disposal must delete one buffer.");
        GameGraphicsAdapter.UniformBufferOwner(buffer).DeleteUniformBuffer(buffer);
        UniformBufferUploads.NoteDeleted(buffer);
    }
}
