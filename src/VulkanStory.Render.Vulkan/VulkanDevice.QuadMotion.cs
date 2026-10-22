using System;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    internal bool ProgramParticleMotionCompatible(int programId) => _programs.TryGetValue(programId, out var program) && program.ParticleMotionCompatible;
    internal void CopyQuadMotionUniforms(int sourceId, int targetId)
    {
        if (!_programs.TryGetValue(sourceId, out var source) || !_programs.TryGetValue(targetId, out var target)) return;
        Span<byte> values = stackalloc byte[64];
        foreach (var member in target.Interface.Members)
            if (member.ArrayLength == 0 && member.Size <= values.Length && CopyWorldUniform(source, member.Name, member.Type.Name, values[..member.Size]))
                Write(targetId, target.ApiLocationOf(member.Name), values[..member.Size]);
        foreach (var member in target.Interface.PushMembers)
            if (member.ArrayLength == 0 && member.Size <= values.Length && CopyWorldUniform(source, member.Name, member.Type.Name, values[..member.Size]))
                Write(targetId, target.ApiLocationOf(member.Name), values[..member.Size]);
    }
}
