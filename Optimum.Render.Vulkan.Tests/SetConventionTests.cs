using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Plan decision 9's set convention exists twice: <c>sources/shaders-vk/include/bindings.glsl</c>
/// for native shaders and <see cref="SetConvention" /> for the renderer. A drift
/// between them writes a texture or buffer into a binding the shader never reads,
/// which renders black or placeholder magenta rather than failing, so they are
/// compared define by define and declaration by declaration.
/// </summary>
public class SetConventionTests
{
    private static readonly Regex Define = new(@"^\s*#define\s+(OPTIMUM_[A-Z0-9_]+)\s+(\d+)\s*$", RegexOptions.Multiline);

    private static readonly Regex Declaration = new(
        @"^\s*layout\(set = (\w+), binding = (\w+)\) uniform (\w+) (\w+)(\[\])?;\s*$", RegexOptions.Multiline);

    [Fact]
    public void EveryDefineInTheIncludeHasTheValueTheRendererUses()
    {
        var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["OPTIMUM_SET_FRAME"] = SetConvention.FrameSet,
            ["OPTIMUM_SET_TEXTURES"] = SetConvention.TextureSet,
            ["OPTIMUM_SET_STORAGE"] = SetConvention.StorageSet,
            ["OPTIMUM_PUSH_CONSTANT_BYTES"] = (int)SetConvention.PushConstantBytes,
            ["OPTIMUM_BINDING_FRAME_GLOBALS"] = SetConvention.FrameGlobalsBinding,
        };
        foreach (SetConvention.Binding binding in SetConvention.FrameTextures) expected[binding.Define] = binding.Value;
        foreach (SetConvention.Binding binding in SetConvention.StorageBuffers) expected[binding.Define] = binding.Value;
        foreach (SetConvention.Binding binding in SetConvention.TextureArrays)
        {
            expected[binding.Define] = binding.Value;
            expected[CapacityDefine(binding)] = (int)binding.Capacity;
        }

        Assert.Equal(expected, Defines());
    }

    [Fact]
    public void EverySamplerDeclarationSitsAtTheSetBindingAndTypeTheRendererWrites()
    {
        Dictionary<string, int> defines = Defines();
        var declared = new List<string>();
        foreach (Match match in Declaration.Matches(ReadInclude()))
        {
            declared.Add(Describe(defines[match.Groups[1].Value], defines[match.Groups[2].Value],
                match.Groups[3].Value, match.Groups[4].Value, match.Groups[5].Success));
        }

        var expected = new List<string>();
        foreach (SetConvention.Binding binding in SetConvention.FrameTextures)
        {
            expected.Add(Describe(SetConvention.FrameSet, binding.Value, binding.GlslType, binding.Name, runtimeArray: false));
        }
        foreach (SetConvention.Binding binding in SetConvention.TextureArrays)
        {
            expected.Add(Describe(SetConvention.TextureSet, binding.Value, binding.GlslType, binding.Name, runtimeArray: true));
        }

        Assert.Equal(expected, declared);
    }

    [Fact]
    public void TheConventionAgreesWithTheFrameBlockAndTheDeviceFloor()
    {
        Assert.Equal(FrameGlobals.Set, SetConvention.FrameSet);
        Assert.Equal(FrameGlobals.Binding, SetConvention.FrameGlobalsBinding);

        Assert.Equal(SumOfCapacities(SetConvention.TextureArrays), SetConvention.TextureArrayCapacityTotal);
        Assert.Equal(SetConvention.TextureArrayCapacityTotal, DescriptorIndexingFloor.BindlessSampledImages);
        Assert.True(SetConvention.FrameTextures.Length <= DescriptorIndexingFloor.FrameTextures,
            "the device floor's frame-texture headroom must cover set 0's textures");
        Assert.Equal(SetConvention.PushConstantBytes, DescriptorIndexingFloor.RequiredPushConstantBytes);
    }

    [Fact]
    public void BindingsAreUniqueWithinEachSet()
    {
        AssertUnique(SetConvention.FrameGlobalsBinding, SetConvention.FrameTextures);
        AssertUnique(null, SetConvention.TextureArrays);
        AssertUnique(null, SetConvention.StorageBuffers);
    }

    /// <summary>
    /// The include is real GLSL: a fragment shader that includes it and samples a
    /// frame texture and a bindless array element compiles to SPIR-V.
    /// </summary>
    [SkippableFact]
    public void TheIncludeCompilesIntoAFragmentShaderThatSamplesBothSets()
    {
        string source = "#version 450\n" + ReadInclude() + """

            layout(location = 0) out vec4 outColor;
            void main()
            {
                float lit = texture(shadowMapFar, vec3(0.5, 0.5, 0.5));
                outColor = texture(optimumTextures2D[0], vec2(0.5)) * lit + texture(sky, vec2(0.5));
            }
            """;

        ShaderCompiler compiler;
        try
        {
            compiler = new ShaderCompiler();
        }
        catch (Exception error) when (error is DllNotFoundException or InvalidOperationException)
        {
            Skip.If(true, "shaderc unavailable: " + error.Message);
            return;
        }

        using (compiler)
        {
            ShaderCompileResult result = compiler.Compile(source, "bindings-probe.frag", EnumShaderType.FragmentShader);
            Assert.True(result.Success, result.Error);
            Assert.NotEmpty(result.Spirv);
        }
    }

    /// <summary>
    /// Native sources use .vert/.frag and includes; a .vsh or .fsh there would be
    /// picked up by the packagers' sources/shaders globs as a vanilla override.
    /// </summary>
    [Fact]
    public void TheNativeShaderTreeHoldsNoGameShaderExtensions()
    {
        string tree = Path.Combine(Root(), "sources", "shaders-vk");
        Assert.True(Directory.Exists(tree), tree + " missing");
        foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            Assert.False(extension is ".vsh" or ".fsh", file + " uses a game shader extension");
        }
    }

    private static string CapacityDefine(SetConvention.Binding binding) =>
        binding.Define.Replace("OPTIMUM_BINDING_", "OPTIMUM_CAPACITY_", StringComparison.Ordinal);

    private static string Describe(int set, int binding, string type, string name, bool runtimeArray) =>
        $"set {set} binding {binding}: {type} {name}{(runtimeArray ? "[]" : "")}";

    private static uint SumOfCapacities(SetConvention.Binding[] bindings)
    {
        uint sum = 0;
        foreach (SetConvention.Binding binding in bindings) sum += binding.Capacity;
        return sum;
    }

    private static void AssertUnique(int? reserved, SetConvention.Binding[] bindings)
    {
        var seen = new HashSet<int>();
        if (reserved != null) seen.Add(reserved.Value);
        foreach (SetConvention.Binding binding in bindings)
        {
            Assert.True(seen.Add(binding.Value), $"{binding.Define} reuses binding {binding.Value}");
        }
    }

    private static Dictionary<string, int> Defines()
    {
        var defines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Define.Matches(ReadInclude()))
        {
            Assert.True(defines.TryAdd(match.Groups[1].Value, int.Parse(match.Groups[2].Value)),
                match.Groups[1].Value + " is defined twice");
        }
        return defines;
    }

    private static string ReadInclude() =>
        File.ReadAllText(Path.Combine(Root(), SetConvention.IncludePath)).Replace("\r\n", "\n");

    private static string Root()
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "Optimum.Patcher"))) root = Directory.GetParent(root)!.FullName;
        return root;
    }
}
