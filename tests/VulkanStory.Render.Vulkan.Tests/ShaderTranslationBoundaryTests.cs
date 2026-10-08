using System;
using VulkanStory.Render.Vulkan.Shaders;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Focused expectation carried from the source ShaderTranslationTests.
/// <summary>Checks bindless GLSL rewriting through native shader compilation and damaged binary-cache rejection.</summary>
/// <remarks>Compilation produces SPIR-V; no Vulkan pipeline or rendered image is validated.</remarks>
public sealed class ShaderTranslationBoundaryTests
{
    [Fact]
    public void IndexedBindlessSamplingCompilesThroughTheNeutralStageBoundary()
    {
        const string source = "#version 330 core\nuniform sampler2D tex; in vec2 uv; out vec4 color; " +
                              "void main(){ color = texture(tex, uv); }";
        ProgramInterfaceLayout layout = ProgramInterfaceLayout.Build(
            new[] { (ShaderStageKind.FragmentShader, GlslParser.Parse(source)) });
        Assert.Empty(layout.Errors);

        RewrittenShader rewritten = ShaderRewriter.Rewrite(
            GlslParser.Parse(source), layout, ShaderStageKind.FragmentShader, false);
        Assert.Empty(rewritten.Errors);
        Assert.Contains("texture(optimumTextures2D[tex], uv)", rewritten.Code);

        using var compiler = new ShaderCompiler();
        ShaderCompileResult result = compiler.Compile(rewritten.Code, "sampling.frag", ShaderStageKind.FragmentShader);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Spirv.Length >= 20);
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(result.Spirv, 0));
    }

    [Fact]
    public void ShaderCacheRejectsDamagedPayloads()
    {
        var spirv = new byte[20];
        BitConverter.TryWriteBytes(spirv, 0x07230203u);
        byte[] wrapped = ShaderBinaryCache.Wrap(spirv);
        Assert.Equal(spirv, ShaderBinaryCache.Unwrap(wrapped));

        wrapped[^1] ^= 1;
        Assert.Null(ShaderBinaryCache.Unwrap(wrapped));
    }
}
