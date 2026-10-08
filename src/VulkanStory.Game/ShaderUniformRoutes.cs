using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Replace only the GL setter; original active-shader guards and location lookups stay intact.</summary>
internal static class ShaderUniformRoutes
{
    private sealed record Route(Type[] Parameters, string GlName, Type[] Values, string Wrapper,
        string TargetName = "Uniform", bool AddReceiver = false);
    private static readonly Route[] Routes =
    [
        new([typeof(float)], "Uniform1", [typeof(float)], nameof(Float1)),
        new([typeof(int)], "Uniform1", [typeof(int)], nameof(Int1)),
        new([typeof(Vec2f)], "Uniform2", [typeof(float), typeof(float)], nameof(Float2)),
        new([typeof(Vec2i)], "Uniform2", [typeof(float), typeof(float)], nameof(Float2)),
        new([typeof(float), typeof(float)], "Uniform2", [typeof(float), typeof(float)], nameof(Float2)),
        new([typeof(Vec3f)], "Uniform3", [typeof(float), typeof(float), typeof(float)], nameof(Float3)),
        new([typeof(float), typeof(float), typeof(float)], "Uniform3", [typeof(float), typeof(float), typeof(float)], nameof(Float3)),
        new([typeof(float), typeof(float), typeof(float), typeof(float)], "Uniform4", [typeof(float), typeof(float), typeof(float), typeof(float)], nameof(Float4)),
        new([typeof(Vec3i)], "Uniform3", [typeof(int), typeof(int), typeof(int)], nameof(Int3)),
        new([typeof(Vec4f)], "Uniform4", [typeof(float), typeof(float), typeof(float), typeof(float)], nameof(Float4)),
        new([typeof(int), typeof(float[])], "Uniform1", [typeof(int), typeof(float[])], nameof(Array1), AddReceiver: true),
        new([typeof(int), typeof(float[])], "Uniform2", [typeof(int), typeof(float[])], nameof(Array2), "Uniforms2", true),
        new([typeof(int), typeof(float[])], "Uniform3", [typeof(int), typeof(float[])], nameof(Array3), "Uniforms3", true),
        new([typeof(int), typeof(float[])], "Uniform4", [typeof(int), typeof(float[])], nameof(Array4), "Uniforms4", true),
        new([typeof(float[])], "UniformMatrix4", [typeof(int), typeof(bool), typeof(float[])], nameof(Matrices4), "UniformMatrix", true),
        new([typeof(Matrix4).MakeByRefType()], "UniformMatrix4", [typeof(bool), typeof(Matrix4).MakeByRefType()], nameof(MatrixRef), "UniformMatrix", true),
        new([typeof(int), typeof(float[])], "UniformMatrix4", [typeof(int), typeof(bool), typeof(float[])], nameof(Matrices4), "UniformMatrices", true),
        new([typeof(int), typeof(float[])], "UniformMatrix4x3", [typeof(int), typeof(bool), typeof(float[])], nameof(Matrices4x3), "UniformMatrices4x3", true),
    ];

    private static MethodInfo Target(Route route) => typeof(ShaderProgramBase).GetMethod(route.TargetName,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
        binder: null, types: [typeof(string), .. route.Parameters], modifiers: null)
        ?? throw new MissingMethodException("Official shader Uniform overload is missing.");

    private static MethodInfo GlSetter(Route route) => typeof(GL).GetMethod(route.GlName,
        [typeof(int), .. route.Values]) ?? throw new MissingMethodException("Official GL setter overload is missing.");

    private static void Check(IEnumerable<CodeInstruction> body, MethodInfo setter)
    {
        if (body.Count(instruction => instruction.Calls(setter)) != 1)
            throw new InvalidOperationException("Shader uniform overload must contain exactly one expected GL setter.");
    }

    /// <summary>Checks exact original uniform overloads and GL call anchors before installation.</summary>
    internal static void ValidateBindings()
    {
        foreach (var route in Routes)
        {
            var target = Target(route);
            if (target.ReturnType != typeof(void) || target.GetMethodBody() is null)
                throw new InvalidOperationException("Official shader Uniform target has changed.");
            Check(PatchProcessor.GetOriginalInstructions(target), GlSetter(route));
        }
    }

    /// <summary>Installs program-aware uniform call substitutions while startup routing remains dormant.</summary>
    /// <param name="harmony">Startup Harmony owner responsible for patch removal.</param>
    internal static void Install(Harmony harmony)
    {
        foreach (var route in Routes)
            harmony.Patch(Target(route), transpiler: new HarmonyMethod(typeof(ShaderUniformRoutes),
                nameof(Transpiler)) { priority = Priority.First });
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        Route route = Routes.Single(candidate => Target(candidate).Equals(__originalMethod));
        MethodInfo setter = GlSetter(route);
        MethodInfo wrapper = typeof(ShaderUniformRoutes).GetMethod(route.Wrapper,
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var body = instructions.ToList();
        Check(body, setter);
        foreach (var instruction in body)
        {
            if (instruction.Calls(setter))
            {
                if (route.AddReceiver)
                {
                    var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                    receiver.labels.AddRange(instruction.labels);
                    receiver.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                    instruction.labels.Clear();
                    instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                    yield return receiver;
                }
                instruction.opcode = OpCodes.Call;
                instruction.operand = wrapper;
            }
            yield return instruction;
        }
    }

    private static int Program => ShaderProgramBase.CurrentShaderProgram?.ProgramId ??
        throw new InvalidOperationException("Active uniform dispatch has no current shader.");

    private static void Float1(int location, float value)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform1(location, value); return; }
        adapter.Uniform(Program, location, value);
    }
    private static void Int1(int location, int value)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform1(location, value); return; }
        adapter.Uniform(Program, location, value);
    }
    private static void Float2(int location, float x, float y)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform2(location, x, y); return; }
        adapter.Uniform(Program, location, x, y);
    }
    private static void Float3(int location, float x, float y, float z)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform3(location, x, y, z); return; }
        adapter.Uniform(Program, location, x, y, z);
    }
    private static void Float4(int location, float x, float y, float z, float w)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform4(location, x, y, z, w); return; }
        adapter.Uniform(Program, location, x, y, z, w);
    }
    private static void Int3(int location, int x, int y, int z)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform3(location, x, y, z); return; }
        adapter.Uniform(Program, location, x, y, z);
    }
    private static void Array1(int location, int count, float[] values, ShaderProgramBase program)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform1(location, count, values); return; }
        adapter.UniformArray(program.ProgramId, location, count, values, 1);
    }
    private static void Array2(int location, int count, float[] values, ShaderProgramBase program)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform2(location, count, values); return; }
        adapter.UniformArray(program.ProgramId, location, count, values, 2);
    }
    private static void Array3(int location, int count, float[] values, ShaderProgramBase program)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform3(location, count, values); return; }
        adapter.UniformArray(program.ProgramId, location, count, values, 3);
    }
    private static void Array4(int location, int count, float[] values, ShaderProgramBase program)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.Uniform4(location, count, values); return; }
        adapter.UniformArray(program.ProgramId, location, count, values, 4);
    }
    private static void Matrices4(int location, int count, bool transpose, float[] values, ShaderProgramBase program)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.UniformMatrix4(location, count, transpose, values); return; }
        if (transpose) throw new InvalidOperationException("Official matrix uniform route requires transpose=false.");
        adapter.UniformMatrices(program.ProgramId, location, count, values);
    }
    private static void Matrices4x3(int location, int count, bool transpose, float[] values, ShaderProgramBase program)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.UniformMatrix4x3(location, count, transpose, values); return; }
        if (transpose) throw new InvalidOperationException("Official matrix uniform route requires transpose=false.");
        adapter.UniformMatrices4x3(program.ProgramId, location, count, values);
    }
    private static void MatrixRef(int location, bool transpose, ref Matrix4 value, ShaderProgramBase program)
    {
        if (!ShaderConsumerPatches.TryActiveShaderAdapter(out var adapter)) { GL.UniformMatrix4(location, transpose, ref value); return; }
        if (transpose) throw new InvalidOperationException("Official matrix uniform route requires transpose=false.");
        adapter.UniformMatrix(program.ProgramId, location, ref value);
    }
}
