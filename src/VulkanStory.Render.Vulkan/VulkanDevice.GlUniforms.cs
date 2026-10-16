using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan;

internal readonly record struct GlUniformInfo(string Name, string Type, int Count);

public sealed unsafe partial class VulkanDevice
{
    internal GlUniformInfo[] ActiveGlUniforms(int id)
    {
        if (!_programs.TryGetValue(id, out ShaderProgramResources? program)) return [];
        var result = new Dictionary<string, GlUniformInfo>(StringComparer.Ordinal);
        void Add(UniformMember member, int? declared = null)
        {
            int count = declared ?? member.ArrayLength;
            string name = count > 0 ? member.Name + "[0]" : member.Name;
            result[name] = new(name, member.Type.Name, Math.Max(1, count));
        }
        foreach (UniformMember member in program.Interface.Members) Add(member);
        foreach (UniformMember member in program.Interface.PushMembersByName.Values) Add(member);
        foreach (var declaration in program.Interface.FrameMemberDeclaredLengths)
            if (FrameGlobals.TryGetMember(declaration.Key, out UniformMember member)) Add(member, declaration.Value);
        foreach (SamplerBinding sampler in program.Interface.Samplers) result[sampler.Name] = new(sampler.Name, sampler.TypeName, 1);
        return result.Values.OrderBy(uniform => uniform.Name, StringComparer.Ordinal).ToArray();
    }

    internal bool TryReadGlUniform(int id, int location, out double[] values)
    {
        values = [];
        if (!_programs.TryGetValue(id, out ShaderProgramResources? program)) return false;
        if (!program.TryStorageLocation(location, out location)) return false;
        if (ShaderProgramResources.IsSamplerLocation(location))
        {
            int index = -2 - location;
            if ((uint)index >= (uint)program.SamplerUnitsByIndex.Length) return false;
            values = [program.SamplerUnitsByIndex[index]];
            return true;
        }
        int offset = location;
        byte[]? shadow;
        IEnumerable<UniformMember> members;
        if (ShaderProgramResources.IsFrameLocation(location))
        {
            offset -= ShaderProgramResources.FrameLocationBase;
            shadow = _frameGlobals;
            members = program.Interface.FrameMemberDeclaredLengths.Keys.Select(name => FrameGlobals.TryGetMember(name, out UniformMember member) ? member : null).OfType<UniformMember>();
        }
        else if (ShaderProgramResources.IsPushLocation(location))
        { offset -= ShaderProgramResources.PushLocationBase; shadow = program.PushShadow; members = program.Interface.PushMembersByName.Values; }
        else { shadow = program.UniformShadow; members = program.Interface.Members; }
        UniformMember? found = members.FirstOrDefault(member => offset >= member.Offset && offset < member.Offset + member.Size);
        if (found is null || shadow is null) return false;
        int bytes = found.Size / found.ElementCount;
        if (bytes <= 0 || (offset - found.Offset) % bytes != 0 || offset + bytes > shadow.Length || bytes % 4 != 0) return false;
        values = new double[bytes / 4];
        bool signed = found.Type.Name == "int" || found.Type.Name == "bool" || found.Type.Name.StartsWith("ivec", StringComparison.Ordinal) || found.Type.Name.StartsWith("bvec", StringComparison.Ordinal);
        bool unsigned = found.Type.Name == "uint" || found.Type.Name.StartsWith("uvec", StringComparison.Ordinal);
        for (int i = 0; i < values.Length; i++)
        {
            if (signed) values[i] = BitConverter.ToInt32(shadow, offset + i * 4);
            else if (unsigned) values[i] = BitConverter.ToUInt32(shadow, offset + i * 4);
            else values[i] = BitConverter.ToSingle(shadow, offset + i * 4);
        }
        return true;
    }
}
