using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Focused cases carried from the source renderer's shader translation,
// delivery, and native runtime tests. No shader compiler or GPU is required.
public sealed class ShaderSourceContractsTests
{
    [Fact]
    public void ParserKeepsMainDetectionAndConstantExpressions()
    {
        Assert.True(GlslParser.Parse("#version 330 core\nvoid /* comment */ main() {}").HasMain);
        Assert.False(GlslParser.Parse("#version 330 core\nvoid mainImage() {}").HasMain);
        Assert.True(GlslParser.TryEvaluateConstantInt("(2 + 3) * 4", out int count));
        Assert.Equal(20, count);
        foreach (string invalid in new[] { "MAX_LIGHTS", "3 *", "", "4 / 0" })
            Assert.False(GlslParser.TryEvaluateConstantInt(invalid, out _));
    }

    [Fact]
    public void ReflectionRejectsTruncatedOrInvalidSpirv()
    {
        Assert.Throws<FormatException>(() => SpirvReflection.Reflect(new byte[] { 1, 2, 3 }));
        Assert.Throws<FormatException>(() => SpirvReflection.Reflect(new byte[20]));
    }

    [Fact]
    public void ManifestVariantKeysAreSortedAndRestrictedToDeclaredAxes()
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["TAAMOTION"] = 1, ["GBUFFER"] = 0, ["IGNORED"] = 9
        };
        Assert.Equal("GBUFFER=0,TAAMOTION=1",
            NativeShaderManifest.VariantKey(["TAAMOTION", "GBUFFER"], values));
        Assert.Equal("", NativeShaderManifest.VariantKey([], values));
    }

    [Fact]
    public void DescriptorFloorUsesTheShippedShaderConvention()
    {
        Assert.Equal(SetConvention.TextureArrayCapacityTotal,
            DescriptorIndexingFloor.BindlessSampledImages);
        Assert.Equal("shaders/native/include/bindings.glsl", SetConvention.IncludePath);
    }
}
