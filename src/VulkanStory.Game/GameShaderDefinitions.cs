using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>Game identities remain here; only owned stage data crosses to rendering.</summary>
internal sealed class GameShaderDefinitions
{
    private readonly ConditionalWeakTable<IShader, ShaderStageDefinition> stages = new();

    /// <summary>Builds one backend stage definition from original game shader source and prefixes.</summary>
    /// <param name="shader">Original stage object; the object remains game-owned.</param>
    /// <returns>Backend-neutral stage source and declaration metadata.</returns>
    internal ShaderStageDefinition Stage(IShader shader)
    {
        var stage = stages.GetValue(shader, _ => new ShaderStageDefinition());
        stage.Type = (int)shader.Type switch
        {
            0x8B31 => ShaderStageType.VertexShader,
            0x8B30 => ShaderStageType.FragmentShader,
            0x8DD9 => ShaderStageType.GeometryShader,
            0x91B9 => ShaderStageType.ComputeShader,
            _ => throw new ArgumentOutOfRangeException(nameof(shader), "Unknown shader stage"),
        };
        stage.Code = shader.Code;
        stage.PrefixCode = shader.PrefixCode;
        return stage;
    }

    internal ShaderProgramDefinition Program(IShaderProgram program) => new(
        program.PassName ?? "", program.VertexShader is { } vertex ? Stage(vertex) : null,
        program.FragmentShader is { } fragment ? Stage(fragment) : null,
        program.GeometryShader is { } geometry ? Stage(geometry) : null,
        program is ShaderProgramBase concrete ? new HashSet<string>(concrete.includes) : null);
}
