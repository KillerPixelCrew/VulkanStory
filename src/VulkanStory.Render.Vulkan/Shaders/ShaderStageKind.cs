namespace VulkanStory.Render.Vulkan.Shaders;

/// <summary>
/// Shader stage at the renderer boundary. Values are the matching GL stage
/// tokens, which also give the binary cache a stable stage tag.
/// </summary>
public enum ShaderStageKind
{
    VertexShader = 0x8B31,
    FragmentShader = 0x8B30,
    GeometryShader = 0x8DD9,
    ComputeShader = 0x91B9,
    RayGenerationShader = 0x9553,
    AnyHitShader = 0x9555,
    ClosestHitShader = 0x9557,
    MissShader = 0x955A,
}
