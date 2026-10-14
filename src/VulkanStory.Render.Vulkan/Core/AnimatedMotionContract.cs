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

    internal static AnimatedMotionContract Create(MotionContractReflection? motion, ShaderProgramResources program)
    {
        if (motion == null) return Incompatible;

        BlockBinding? currentBlock = FindBlock(program.Interface, "Animation");
        BlockBinding? previousBlock = FindBlock(program.Interface, "AnimationPrev");
        if (currentBlock == null || previousBlock == null ||
            (currentBlock.Set == previousBlock.Set && currentBlock.Binding == previousBlock.Binding)) return Incompatible;

        SpirvBlockMember? current = JointArray(motion.Vertex, currentBlock);
        SpirvBlockMember? previous = JointArray(motion.Vertex, previousBlock);
        if (current == null || previous == null || current.ArrayLength != previous.ArrayLength ||
            current.RuntimeArray != previous.RuntimeArray) return Incompatible;

        return new AnimatedMotionContract(current.RuntimeArray ? 0 : current.ArrayLength, motion.Outputs);
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

}

/// <summary>Common linked motion ABI, reflected once before selecting rigid or animated history.</summary>
internal sealed class MotionContractReflection
{
    internal SpirvModuleReflection Vertex { get; }
    internal SpirvModuleReflection Fragment { get; }
    internal int[] Outputs { get; }

    private MotionContractReflection(SpirvModuleReflection vertex, SpirvModuleReflection fragment, int[] outputs)
    {
        Vertex = vertex;
        Fragment = fragment;
        Outputs = outputs;
    }

    internal static MotionContractReflection? Create(TranslatedProgram translated, ShaderProgramResources program)
    {
        if (translated.Spirv.ContainsKey(EnumShaderType.GeometryShader) ||
            !translated.Spirv.TryGetValue(EnumShaderType.VertexShader, out byte[]? vertexCode) ||
            !translated.Spirv.TryGetValue(EnumShaderType.FragmentShader, out byte[]? fragmentCode)) return null;

        foreach (string name in MatrixUniforms)
            if (!MatchesUniform(program, name, "mat4")) return null;
        if (!MatchesUniform(program, "cameraPosDelta", "vec3") ||
            !MatchesUniform(program, "taaRenderSize", "vec2") ||
            !MatchesUniform(program, "taaJitterPx", "vec2") ||
            !MatchesUniform(program, "taaHistoryValid", "int") ||
            !MatchesUniform(program, "taaReactive", "float")) return null;

        // Current overrides and previous-warp values must accept the scalar float writes/snapshots.
        foreach (string name in OptionalWarpFloats)
            if (program.LocationOf(name) != -1 && !MatchesUniform(program, name, "float")) return null;
        if ((program.LocationOf("prevPerceptionEffectId") != -1 && !MatchesUniform(program, "prevPerceptionEffectId", "int")) ||
            (program.LocationOf("prevPlayerpos") != -1 && !MatchesUniform(program, "prevPlayerpos", "vec3"))) return null;

        try
        {
            SpirvModuleReflection vertex = SpirvReflection.Reflect(vertexCode);
            SpirvModuleReflection fragment = SpirvReflection.Reflect(fragmentCode);
            if (vertex.ExecutionModel != SpirvReflection.ExecutionModelVertex ||
                fragment.ExecutionModel != SpirvReflection.ExecutionModelFragment) return null;

            var outputs = new List<int>();
            foreach (SpirvInterfaceVariable output in fragment.Outputs)
            {
                if (output.Location >= 0 && output.GlslType == "vec4" && output.ScalarWidth == 32 && output.ArrayDimensions == 0 &&
                    output.ArrayLength == 0 && fragment.WrittenOutputLocations.Contains(output.Location) &&
                    program.Interface.WrittenFragmentOutputs.Contains(output.Location)) outputs.Add(output.Location);
            }
            return outputs.Count == 0 ? null : new MotionContractReflection(vertex, fragment, outputs.ToArray());
        }
        catch (Exception error) when (error is FormatException or KeyNotFoundException or
            IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // An uncertain reflection result must never enable an object history writer.
            return null;
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

    private static readonly string[] MatrixUniforms =
    ["modelMatrix", "viewMatrix", "projectionMatrix", "prevModelMatrix", "prevViewMatrix", "prevProjectionMatrix"];

    private static readonly string[] OptionalWarpFloats =
    ["windWaveIntensity", "waterWaveCounter", "prevTimeCounter", "prevWindWaveCounter", "prevWindWaveCounterHighFreq", "prevWaterWaveCounter", "prevWindSpeed",
     "prevGlobalWarpIntensity", "prevGlitchWaviness", "prevWindWaveIntensity", "prevWaterWaveIntensity", "prevPerceptionEffectIntensity"];
}
