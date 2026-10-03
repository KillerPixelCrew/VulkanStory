using System;
using Vintagestory.API.Client;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Game;

/// <summary>Staged conversion from the official game's shader enum to renderer stages.</summary>
internal static class GameShaderStage
{
    public static ShaderStageKind From(EnumShaderType stage) => stage switch
    {
        EnumShaderType.VertexShader => ShaderStageKind.VertexShader,
        EnumShaderType.FragmentShader => ShaderStageKind.FragmentShader,
        EnumShaderType.GeometryShader => ShaderStageKind.GeometryShader,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "unsupported game shader stage"),
    };
}
