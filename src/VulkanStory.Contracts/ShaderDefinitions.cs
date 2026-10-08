namespace VulkanStory.Contracts;

/// <summary>Retained OpenGL stage tokens used only as shader identities at the adapter boundary.</summary>
public enum ShaderStageType
{
    VertexShader = 0x8B31, FragmentShader = 0x8B30,
    GeometryShader = 0x8DD9, ComputeShader = 0x91B9,
}

/// <summary>Stable renderer-owned stage identity; source refreshed by the game adapter.</summary>
public sealed class ShaderStageDefinition
{
    /// <summary>Stage for which this source is translated or compiled.</summary>
    public ShaderStageType Type { get; set; }
    /// <summary>Stage source supplied by the game adapter, or null while unavailable.</summary>
    public string? Code { get; set; }
    /// <summary>Adapter-supplied preprocessor/source prefix, separate from the main stage body.</summary>
    public string? PrefixCode { get; set; }
}

/// <summary>Named program stages and include identities supplied to the renderer by the game adapter.</summary>
/// <param name="PassName">Game render-pass identity used for program selection.</param>
/// <param name="VertexShader">Vertex stage, when present.</param>
/// <param name="FragmentShader">Fragment stage, when present.</param>
/// <param name="GeometryShader">Geometry stage, when present.</param>
/// <param name="Includes">Include names associated with this program, when known.</param>
public sealed record ShaderProgramDefinition(
    string PassName, ShaderStageDefinition? VertexShader, ShaderStageDefinition? FragmentShader,
    ShaderStageDefinition? GeometryShader, IReadOnlySet<string>? Includes);
