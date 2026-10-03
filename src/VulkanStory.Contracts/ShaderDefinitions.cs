namespace VulkanStory.Contracts;

public enum ShaderStageType
{
    VertexShader = 0x8B31, FragmentShader = 0x8B30,
    GeometryShader = 0x8DD9, ComputeShader = 0x91B9,
}

/// <summary>Stable renderer-owned stage identity; source refreshed by the game adapter.</summary>
public sealed class ShaderStageDefinition
{
    public ShaderStageType Type { get; set; }
    public string? Code { get; set; }
    public string? PrefixCode { get; set; }
}

public sealed record ShaderProgramDefinition(
    string PassName, ShaderStageDefinition? VertexShader, ShaderStageDefinition? FragmentShader,
    ShaderStageDefinition? GeometryShader, IReadOnlySet<string>? Includes);
