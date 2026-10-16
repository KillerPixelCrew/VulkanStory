using System.Reflection;

namespace VulkanStory.Game;

internal sealed partial class ManagedGlBridge
{
    private object ActiveUniform(MethodInfo method, object?[] a)
    {
        ProgramObject program = Program(I(a[0]));
        if (!program.Linked) Error(0x0502);
        int index = I(a[1]);
        var uniforms = Device.ActiveGlUniforms(program.Backend);
        if ((uint)index >= (uint)uniforms.Length) Error(0x0501);
        var uniform = uniforms[index];
        int type = UniformTypeToken(uniform.Type);
        WriteOutput(method, a, 2, [uniform.Count], false);
        Type outputType = method.GetParameters()[3].ParameterType.GetElementType()!;
        a[3] = outputType.IsEnum ? Enum.ToObject(outputType, type) : type;
        return uniform.Name;
    }
    private static int UniformTypeToken(string type) => type switch
    {
        "float" => 0x1406, "int" => 0x1404, "uint" => 0x1405, "bool" => 0x8B56,
        "vec2" => 0x8B50, "vec3" => 0x8B51, "vec4" => 0x8B52, "ivec2" => 0x8B53, "ivec3" => 0x8B54, "ivec4" => 0x8B55,
        "bvec2" => 0x8B57, "bvec3" => 0x8B58, "bvec4" => 0x8B59, "uvec2" => 0x8DC6, "uvec3" => 0x8DC7, "uvec4" => 0x8DC8,
        "mat2" => 0x8B5A, "mat3" => 0x8B5B, "mat4" => 0x8B5C, "mat2x3" => 0x8B65, "mat2x4" => 0x8B66, "mat3x2" => 0x8B67,
        "mat3x4" => 0x8B68, "mat4x2" => 0x8B69, "mat4x3" => 0x8B6A,
        "sampler2D" => 0x8B5E, "sampler3D" => 0x8B5F, "samplerCube" => 0x8B60, "sampler2DShadow" => 0x8B62,
        "sampler2DArray" => 0x8DC1, "sampler2DArrayShadow" => 0x8DC4, "isampler2D" => 0x8DCA, "usampler2D" => 0x8DD2,
        _ => throw new NotSupportedException("Uniform introspection has no GL type token for " + type),
    };
    private void ReadUniform(MethodInfo method, object?[] a)
    {
        ProgramObject program = Program(I(a[0]));
        double[] values = [];
        if (!program.Linked || !Device.TryReadGlUniform(program.Backend, I(a[1]), out values)) Error(0x0502);
        Type type = method.GetParameters()[2].ParameterType;
        bool integer = type.HasElementType && type.GetElementType() == typeof(int);
        if (integer) WriteOutput(method, a, 2, values.Select(value => (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)).ToArray(), false);
        else WriteFloats(method, a, 2, values.Select(value => (float)value).ToArray());
    }
    private void SetProgramUniform(object?[] a)
    {
        ProgramObject program = Program(I(a[0]));
        if (!program.Linked) Error(0x0502);
        int location = I(a[1]); if (location == -1) return;
        if (!Device.IsUniformLocation(program.Backend, location)) Error(0x0502);
        if (a[2] is float value) session.Graphics.Uniform(program.Backend, location, value);
        else session.Graphics.Uniform(program.Backend, location, I(a[2]));
    }
}
