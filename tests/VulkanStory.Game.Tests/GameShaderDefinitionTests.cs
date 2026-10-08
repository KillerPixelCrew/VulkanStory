using Vintagestory.API.Client;
using VulkanStory.Contracts;
using Xunit;

namespace VulkanStory.Game.Tests;

/// <summary>Checks retained shader-stage identity, refreshed source text, and supported stage tokens.</summary>
/// <remarks>Does not compile these fixture shaders or validate rendered output.</remarks>
public sealed class GameShaderDefinitionTests
{
    /// <summary>Mutable game shader stub used to observe identity-preserving text refresh.</summary>
    private sealed class SourceShader(EnumShaderType type) : IShader
    {
        public EnumShaderType Type => type;
        public string Code { get; set; } = "void main() {}";
        public string PrefixCode { get; set; } = "#define INITIAL 1\n";
    }

    [Fact]
    public void ReloadRefreshesTextWithoutChangingStageIdentityOrMergingDistinctObjects()
    {
        var definitions = new GameShaderDefinitions();
        var original = new SourceShader(EnumShaderType.VertexShader);
        ShaderStageDefinition first = definitions.Stage(original);
        Assert.Equal(original.Code, first.Code);
        Assert.Equal(original.PrefixCode, first.PrefixCode);
        original.Code = "void main() { gl_Position = vec4(1.0); }";
        original.PrefixCode = "#define RELOADED 1\n";
        ShaderStageDefinition reload = definitions.Stage(original);
        Assert.Same(first, reload);
        Assert.Equal(original.Code, reload.Code);
        Assert.Equal(original.PrefixCode, reload.PrefixCode);
        var another = new SourceShader(EnumShaderType.VertexShader)
        {
            Code = original.Code, PrefixCode = original.PrefixCode,
        };
        Assert.NotSame(reload, definitions.Stage(another));
    }

    [Theory]
    [InlineData(35633, 35633)]
    [InlineData(35632, 35632)]
    [InlineData(36313, 36313)]
    [InlineData(37305, 37305)]
    public void SupportedStageTokensSurviveTheGameBoundary(int original, int expected)
    {
        var definitions = new GameShaderDefinitions();
        ShaderStageDefinition stage = definitions.Stage(new SourceShader((EnumShaderType)original));
        Assert.Equal((ShaderStageType)expected, stage.Type);
    }
}
