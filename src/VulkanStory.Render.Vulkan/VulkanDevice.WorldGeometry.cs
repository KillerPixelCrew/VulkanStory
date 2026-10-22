using System;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan;

// Copies the source particle program's uniforms for the quad motion pass.
public sealed unsafe partial class VulkanDevice
{
    private bool CopyWorldUniform(ShaderProgramResources program, string name, string type, Span<byte> target)
    {
        UniformMember? member;
        byte[]? shadow;
        if (program.Interface.FrameMemberDeclaredLengths.TryGetValue(name, out int length))
        {
            if (length != 0 || !FrameGlobals.TryGetMember(name, out member!)) return false;
            shadow = _frameGlobals;
        }
        else if (program.Interface.PushMembersByName.TryGetValue(name, out member)) shadow = program.PushShadow;
        else { program.Interface.MembersByName.TryGetValue(name, out member); shadow = program.UniformShadow; }
        if (member == null || member.Type.Name != type || member.ArrayLength != 0 || shadow == null ||
            member.Size < target.Length || member.Offset < 0 || member.Offset + target.Length > shadow.Length) return false;
        shadow.AsSpan(member.Offset, target.Length).CopyTo(target);
        return true;
    }

}
