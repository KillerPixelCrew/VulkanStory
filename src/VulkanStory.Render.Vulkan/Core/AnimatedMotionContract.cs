using System;
using System.Collections.Generic;
using VulkanStory.Render.Vulkan.Shaders;
using EnumShaderType = VulkanStory.Render.Vulkan.Shaders.ShaderStageKind;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>The immutable animation/motion ABI of one linked program; it does not certify arbitrary deformation.</summary>
internal sealed class AnimatedMotionContract
{
    private static readonly AnimatedMotionContract Incompatible = new(0, Array.Empty<int>());
    private readonly int _maximumJointMatrices;
    private readonly int[] _motionOutputs;

    private AnimatedMotionContract(int maximumJointMatrices, int[] motionOutputs)
    {
        _maximumJointMatrices = maximumJointMatrices;
        _motionOutputs = motionOutputs;
    }

    internal bool TryGet(int motionAttachment, out int maximumJointMatrices)
    {
        maximumJointMatrices = 0;
        foreach (int output in _motionOutputs)
        {
            if (output != motionAttachment) continue;
            maximumJointMatrices = _maximumJointMatrices;
            return true;
        }
        return false;
    }

    internal static AnimatedMotionContract Create(TranslatedProgram translated, ShaderProgramResources program)
    {
        if (translated.Spirv.ContainsKey(EnumShaderType.GeometryShader) ||
            !translated.Spirv.TryGetValue(EnumShaderType.VertexShader, out byte[]? vertexCode) ||
            !translated.Spirv.TryGetValue(EnumShaderType.FragmentShader, out byte[]? fragmentCode))
            return Incompatible;

        foreach (string name in MatrixUniforms)
            if (!MatchesUniform(program, name, "mat4")) return Incompatible;
        if (!MatchesUniform(program, "cameraPosDelta", "vec3") ||
            !MatchesUniform(program, "taaRenderSize", "vec2") ||
            !MatchesUniform(program, "taaJitterPx", "vec2") ||
            !MatchesUniform(program, "taaHistoryValid", "int") ||
            !MatchesUniform(program, "taaReactive", "float")) return Incompatible;

        // Current overrides and previous-warp values must accept the scalar float writes/snapshots.
        foreach (string name in OptionalWarpFloats)
            if (program.LocationOf(name) != -1 && !MatchesUniform(program, name, "float")) return Incompatible;
        if ((program.LocationOf("prevPerceptionEffectId") != -1 && !MatchesUniform(program, "prevPerceptionEffectId", "int")) ||
            (program.LocationOf("prevPlayerpos") != -1 && !MatchesUniform(program, "prevPlayerpos", "vec3")))
            return Incompatible;

        BlockBinding? currentBlock = FindBlock(program.Interface, "Animation");
        BlockBinding? previousBlock = FindBlock(program.Interface, "AnimationPrev");
        if (currentBlock == null || previousBlock == null ||
            (currentBlock.Set == previousBlock.Set && currentBlock.Binding == previousBlock.Binding)) return Incompatible;

        try
        {
            SpirvModuleReflection vertex = SpirvReflection.Reflect(vertexCode);
            SpirvModuleReflection fragment = SpirvReflection.Reflect(fragmentCode);
            if (vertex.ExecutionModel != SpirvReflection.ExecutionModelVertex ||
                fragment.ExecutionModel != SpirvReflection.ExecutionModelFragment) return Incompatible;

            SpirvBlockMember? current = JointArray(vertex, currentBlock);
            SpirvBlockMember? previous = JointArray(vertex, previousBlock);
            if (current == null || previous == null || current.ArrayLength != previous.ArrayLength ||
                current.RuntimeArray != previous.RuntimeArray) return Incompatible;

            var outputs = new List<int>();
            foreach (SpirvInterfaceVariable output in fragment.Outputs)
            {
                if (output.Location >= 0 && output.GlslType == "vec4" && output.ScalarWidth == 32 && output.ArrayDimensions == 0 &&
                    output.ArrayLength == 0 && fragment.WrittenOutputLocations.Contains(output.Location) &&
                    program.Interface.WrittenFragmentOutputs.Contains(output.Location)) outputs.Add(output.Location);
            }
            return outputs.Count == 0 ? Incompatible :
                new AnimatedMotionContract(current.RuntimeArray ? 0 : current.ArrayLength, outputs.ToArray());
        }
        catch (Exception error) when (error is FormatException or KeyNotFoundException or
            IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // An uncertain reflection result must never enable an animation history writer.
            return Incompatible;
        }
    }

    private static bool MatchesUniform(ShaderProgramResources program, string name, string type)
    {
        if (program.LocationOf(name) < 0) return false;
        ProgramInterfaceLayout layout = program.Interface;
        if (layout.FrameMemberDeclaredLengths.TryGetValue(name, out int length))
            return length == 0 && FrameGlobals.TryGetMember(name, out UniformMember frame) &&
                frame.ArrayLength == 0 && frame.Type.Name == type;
        if (layout.PushMembersByName.TryGetValue(name, out UniformMember? push))
            return push.ArrayLength == 0 && push.Type.Name == type;
        return layout.MembersByName.TryGetValue(name, out UniformMember? member) &&
            member.ArrayLength == 0 && member.Type.Name == type;
    }

    private static BlockBinding? FindBlock(ProgramInterfaceLayout layout, string name)
    {
        BlockBinding? found = null;
        foreach (BlockBinding block in layout.UniformBlocks)
        {
            if (block.BlockName != name) continue;
            if (found != null) return null;
            found = block;
        }
        return found;
    }

    private static SpirvBlockMember? JointArray(SpirvModuleReflection vertex, BlockBinding block)
    {
        SpirvDescriptorBinding? found = null;
        foreach (SpirvDescriptorBinding binding in vertex.Bindings)
        {
            if (binding.Set != block.Set || binding.Binding != block.Binding) continue;
            if (found != null) return null;
            found = binding;
        }
        if (found == null || found.Kind != SpirvDescriptorKind.StorageBuffer ||
            found.ArrayDimensions != 0 || found.ArrayLength != 0 || found.RuntimeArray ||
            found.Block == null || found.Block.Members.Count != 1) return null;

        SpirvBlockMember member = found.Block.Members[0];
        return member.GlslType == "mat4" && member.ScalarWidth == 32 && member.HasExplicitOffset && member.Offset == 0 &&
            member.ArrayDimensions == 1 && member.ArrayStride == 64 && member.MatrixStride == 16 &&
            member.ColumnMajor && !member.RowMajor && !member.HasConflictingLayoutDecorations &&
            (member.RuntimeArray ? member.ArrayLength == -1 : member.ArrayLength > 0) ? member : null;
    }

    private static readonly string[] MatrixUniforms =
    ["modelMatrix", "viewMatrix", "projectionMatrix", "prevModelMatrix", "prevViewMatrix", "prevProjectionMatrix"];

    private static readonly string[] OptionalWarpFloats =
    ["windWaveIntensity", "waterWaveCounter", "prevTimeCounter", "prevWindWaveCounter", "prevWindWaveCounterHighFreq", "prevWaterWaveCounter", "prevWindSpeed",
     "prevGlobalWarpIntensity", "prevGlitchWaviness", "prevWindWaveIntensity", "prevWaterWaveIntensity", "prevPerceptionEffectIntensity"];
}
