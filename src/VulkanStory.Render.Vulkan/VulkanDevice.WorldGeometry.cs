using System;
using System.IO;
using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan;

// Warp-state readers remain shared by particle geometry and terrain meshing.
public sealed unsafe partial class VulkanDevice
{
    private static readonly string[] WorldWarpNames = ["timeCounter", "windWaveCounter", "windWaveCounterHighFreq", "waterWaveCounter",
        "windSpeed", "globalWarpIntensity", "glitchWaviness", "windWaveIntensity", "waterWaveIntensity", "perceptionEffectIntensity"];
    private static readonly string[] PreviousWorldWarpNames = Array.ConvertAll(WorldWarpNames,
        name => "prev" + char.ToUpperInvariant(name[0]) + name[1..]);

    private bool ReadGeometryWarpState(ShaderProgramResources program, bool previous, Span<byte> output)
    {
        output.Clear();
        string[] names = previous ? PreviousWorldWarpNames : WorldWarpNames;
        for (int i = 0; i < names.Length; i++)
        {
            if (!CopyWorldUniform(program, names[i], "float", output.Slice(i * 4, 4))) return false;
        }
        if (!CopyWorldUniform(program, previous ? "prevPlayerpos" : "playerpos", "vec3", output.Slice(48, 12)) ||
            !CopyWorldUniform(program, previous ? "prevPerceptionEffectId" : "perceptionEffectId", "int", output.Slice(60, 4))) return false;
        // Nonfinite state must keep the original shader path, never poison a retained buffer.
        var floats = MemoryMarshal.Cast<byte, float>(output[..60]);
        foreach (float value in floats) if (!float.IsFinite(value)) return false;
        return true;
    }

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

    private static string ReadWorldShader(string name)
    {
        using Stream stream = typeof(VulkanDevice).Assembly.GetManifestResourceStream("shaders-vk/" + name)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }

}
