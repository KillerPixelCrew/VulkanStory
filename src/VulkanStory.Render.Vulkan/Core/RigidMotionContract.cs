using System;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>The immutable rigid motion ABI of one linked program; it does not certify arbitrary deformation.</summary>
internal sealed class RigidMotionContract
{
    private static readonly RigidMotionContract Incompatible = new(Array.Empty<int>());
    private readonly int[] _motionOutputs;

    private RigidMotionContract(int[] motionOutputs) => _motionOutputs = motionOutputs;

    internal bool Has(int motionAttachment)
    {
        foreach (int output in _motionOutputs)
            if (output == motionAttachment) return true;
        return false;
    }

    internal static RigidMotionContract Create(MotionContractReflection? motion, ShaderProgramResources program)
    {
        if (motion == null) return Incompatible;
        foreach (BlockBinding block in program.Interface.UniformBlocks)
            if (IsAnimationBlock(block.BlockName)) return Incompatible;
        if (HasAnimationBlock(motion.Vertex) || HasAnimationBlock(motion.Fragment)) return Incompatible;
        return new RigidMotionContract(motion.Outputs);
    }

    private static bool HasAnimationBlock(SpirvModuleReflection stage)
    {
        foreach (SpirvDescriptorBinding binding in stage.Bindings)
            if (IsAnimationBlock(binding.Name) || IsAnimationBlock(binding.Block?.TypeName) ||
                IsAnimationBlock(binding.Block?.InstanceName)) return true;
        return false;
    }

    private static bool IsAnimationBlock(string? name) => name is "Animation" or "AnimationPrev";
}
