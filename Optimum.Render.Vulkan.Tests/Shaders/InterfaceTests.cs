// Source: Optimum.Render.Vulkan.Tests/FrameGlobalsTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

/// <summary>
/// The shared frame block: its fixed layout, the rule that decides which of a
/// program's uniforms read it, how the rewriter emits it, and - against the game's
/// own files - that the table names exactly what <c>ShaderProgramBase.Use()</c>
/// writes, with the types the includes declare.
/// </summary>
public class FrameGlobalsTests
{
    private static readonly HashSet<string> AllOwners = new(StringComparer.Ordinal)
    {
        "fogandlight.fsh", "fogandlight.vsh", "shadowcoords.vsh", "vertexwarp.vsh",
        "skycolor.fsh", "colormap.vsh", "underwatereffects.fsh",
    };

    private static ProgramInterfaceLayout LayoutOf(IReadOnlySet<string>? includes, params (EnumShaderType Stage, string Source)[] stages)
    {
        var parsed = stages.Select(s => (s.Stage, GlslParser.Parse(s.Source))).ToList();
        return ProgramInterfaceLayout.Build(parsed, null, includes);
    }

    [Fact]
    public void MembersAreScalarAlignedInOrderAndNeverOverlap()
    {
        int end = 0;
        foreach (UniformMember member in FrameGlobals.Members)
        {
            Assert.True(member.Offset >= end, member.Name + " overlaps the member before it");
            Assert.Equal(0, member.Offset % 4);
            Assert.Equal(member.Type.Size * member.ElementCount, member.Size);
            end = member.Offset + member.Size;
        }
        Assert.Equal(end, FrameGlobals.BlockSize);
        // Small enough that a snapshot per change is nothing next to a frame.
        Assert.True(FrameGlobals.BlockSize < 8192, "frame block is " + FrameGlobals.BlockSize + " bytes");
    }

    [Fact]
    public void AMemberJoinsOnlyWhenTheProgramIncludesItsOwnerAndTheDeclarationFits()
    {
        GlslType vec3 = GetType("vec3");
        GlslType floatType = GetType("float");
        var fog = new HashSet<string>(StringComparer.Ordinal) { "fogandlight.vsh" };

        Assert.True(FrameGlobals.TryPlace("pointLights", vec3, 4, fog, out _));
        Assert.True(FrameGlobals.TryPlace("pointLights", vec3, FrameGlobals.MaxDynamicLights, fog, out _));
        // Longer than the shared capacity, or not an array where the member is one.
        Assert.False(FrameGlobals.TryPlace("pointLights", vec3, FrameGlobals.MaxDynamicLights + 1, fog, out _));
        Assert.False(FrameGlobals.TryPlace("pointLights", vec3, 0, fog, out _));
        // A different type.
        Assert.False(FrameGlobals.TryPlace("pointLights", floatType, 4, fog, out _));
        // The owner is not included: the GUI program's own lightPosition, say.
        Assert.False(FrameGlobals.TryPlace("lightPosition", vec3, 0, fog, out _));
        Assert.False(FrameGlobals.TryPlace("pointLights", vec3, 4, null, out _));
        // frameSize is written outside Use() by the blur passes, so it is never shared.
        Assert.False(FrameGlobals.TryGetMember("frameSize", out _));
    }

    [Fact]
    public void AProgramThatIncludesTheOwnerReadsTheSharedBlockAndKeepsTheRestToItself()
    {
        const string vertex = """
            #version 330 core
            uniform vec3 pointLights[4];
            uniform float viewDistance;
            uniform float tint;
            void main() {}
            """;
        const string fragment = """
            #version 330 core
            uniform float viewDistance;
            out vec4 outColor;
            void main() { outColor = vec4(viewDistance); }
            """;
        var includes = new HashSet<string>(StringComparer.Ordinal) { "fogandlight.vsh" };

        ProgramInterfaceLayout layout = LayoutOf(includes,
            (EnumShaderType.VertexShader, vertex), (EnumShaderType.FragmentShader, fragment));

        Assert.True(layout.UsesFrameBlock);
        Assert.Equal(4, layout.FrameMemberDeclaredLengths["pointLights"]);
        Assert.Equal(0, layout.FrameMemberDeclaredLengths["viewDistance"]);
        Assert.Contains("viewDistance", layout.FrameMembersByStage[EnumShaderType.FragmentShader]);
        Assert.DoesNotContain("pointLights", layout.FrameMembersByStage[EnumShaderType.FragmentShader]);
        // Only the program's own uniform is left in its block.
        Assert.Equal(new[] { "tint" }, layout.Members.Select(m => m.Name));

        string code = ShaderRewriter.Rewrite(GlslParser.Parse(vertex), layout, EnumShaderType.VertexShader, emitDepthRemap: true).Code;
        FrameGlobals.TryGetMember("pointLights", out UniformMember lights);
        FrameGlobals.TryGetMember("viewDistance", out UniformMember distance);
        Assert.Contains("layout(scalar, set = 0, binding = 0) uniform OptimumFrameGlobals", code);
        Assert.Contains($"layout(offset = {lights.Offset}) vec3 pointLights[4];", code);
        Assert.Contains($"layout(offset = {distance.Offset}) float viewDistance;", code);
        Assert.Contains("layout(scalar, set = 2, binding = 3) uniform OptimumUniforms", code);
        Assert.DoesNotContain("uniform vec3 pointLights[4];", code);
    }

    [Fact]
    public void WithoutIncludesEveryUniformStaysTheProgramsOwn()
    {
        ProgramInterfaceLayout layout = LayoutOf(null, (EnumShaderType.VertexShader, """
            #version 330 core
            uniform float zNear;
            void main() {}
            """));

        Assert.False(layout.UsesFrameBlock);
        Assert.Contains("zNear", layout.MembersByName.Keys);
    }

    [Fact]
    public void TheSharedShadowStartsWithTheDeclaredDefaults()
    {
        byte[] shadow = FrameGlobals.CreateShadow();
        Assert.Equal(FrameGlobals.BlockSize, shadow.Length);
        Assert.Equal(0.3f, ReadFloat(shadow, "zNear"));
        Assert.Equal(1500f, ReadFloat(shadow, "zFar"));
        Assert.Equal(1f, ReadFloat(shadow, "windWaveIntensity"));
        FrameGlobals.TryGetMember("perceptionEffectId", out UniformMember id);
        Assert.Equal(1, BitConverter.ToInt32(shadow, id.Offset));
    }

    /// <summary>
    /// Every member is declared by its owning include with the table's type, and
    /// its array fits the shared capacity. A game update that changes one of these
    /// declarations fails here rather than reading the wrong bytes on screen.
    /// </summary>
    [SkippableFact]
    public void TheOwningIncludesDeclareEveryMemberWithTheSameType()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();

        foreach (UniformMember member in FrameGlobals.Members)
        {
            string owner = FrameGlobals.OwnerOf(member.Name)!;
            Assert.True(includes.TryGetValue(owner, out string? source), owner + " is missing");
            // Declarations can sit in a file the owner includes (fogSpheres lives in
            // fogspheres.ash); the registry records every nested include too.
            source = ShaderCorpus.ExpandIncludes(source!, includes);
            Match declaration = Regex.Match(source,
                @"uniform\s+(\w+)\s+" + Regex.Escape(member.Name) + @"\b\s*(?:\[([^\]]*)\])?");
            Assert.True(declaration.Success, owner + " does not declare " + member.Name);
            Assert.Equal(member.Type.Name, declaration.Groups[1].Value);

            string size = declaration.Groups[2].Value.Trim();
            if (member.ArrayLength == 0)
            {
                Assert.True(size.Length == 0, member.Name + " is declared as an array in " + owner);
                continue;
            }
            int declared = size == "DYNLIGHTS"
                ? FrameGlobals.MaxDynamicLights
                : size.Split('*').Select(part => int.Parse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture))
                    .Aggregate(1, (a, b) => a * b);
            Assert.True(declared <= member.ArrayLength, member.Name + " is declared longer than the shared capacity");
        }
    }

    /// <summary>
    /// Every member is written by <c>Use()</c> inside its owner's include block. A
    /// member written anywhere else would be clobbered by other programs sharing it.
    /// </summary>
    [SkippableFact]
    public void UseWritesEveryMemberInsideItsOwnersBlock()
    {
        string path = Path.Combine(ShaderCorpus.RepositoryRoot, "build", "VintagestoryLib",
            "Vintagestory.Client.NoObf", "ShaderProgramBase.cs");
        Skip.IfNot(File.Exists(path), "No bootstrapped build tree.");
        string source = File.ReadAllText(path);
        int use = source.IndexOf("public void Use()", StringComparison.Ordinal);
        Assert.True(use > 0);
        string body = source[use..source.IndexOf("public void Stop()", use, StringComparison.Ordinal)];

        foreach (UniformMember member in FrameGlobals.Members)
        {
            string owner = FrameGlobals.OwnerOf(member.Name)!;
            Assert.Contains(owner, AllOwners);
            int block = body.IndexOf("includes.Contains(\"" + owner + "\")", StringComparison.Ordinal);
            Assert.True(block > 0, "Use() has no block for " + owner);
            int next = body.IndexOf("includes.Contains(", block + 1, StringComparison.Ordinal);
            int end = next > 0 ? next : body.Length;
            Assert.True(body.IndexOf("\"" + member.Name + "\"", block, end - block, StringComparison.Ordinal) > 0,
                "Use() does not write " + member.Name + " in the " + owner + " block");
        }
    }

    /// <summary>
    /// <c>sources/shaders-vk/include/frame.glsl</c> is generated from the table. Set
    /// OPTIMUM_REGENERATE_NATIVE_INCLUDES=1 to rewrite it after changing the table; without it
    /// any difference fails, so a table change cannot ship with a stale native block.
    /// </summary>
    [Fact]
    public void TheCommittedNativeIncludeIsWhatTheTableGenerates()
    {
        string path = Path.Combine(ShaderCorpus.RepositoryRoot, FrameGlobals.IncludePath);
        string generated = FrameGlobals.GenerateInclude();
        if (Environment.GetEnvironmentVariable("OPTIMUM_REGENERATE_NATIVE_INCLUDES") == "1")
        {
            File.WriteAllText(path, generated);
        }

        Assert.True(File.Exists(path), path + " is missing; run with OPTIMUM_REGENERATE_NATIVE_INCLUDES=1");
        Assert.Equal(generated, File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The compiled block puts every member at the offset the renderer writes: the SPIR-V
    /// <c>Offset</c> decorations of the block at set 0, binding 0 are the table's offsets, member
    /// by member, and the arrays stride by their element size (scalar layout, no std140 padding).
    /// </summary>
    [SkippableFact]
    public void TheCompiledNativeBlockHasTheTablesOffsets()
    {
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);
        const string probe = """
            #version 450
            #include "frame.glsl"
            layout(location = 0) out vec4 outColor;
            void main()
            {
                outColor = vec4(optimumFrame.zNear, optimumFrame.pointLights[99].x, 0.0, 1.0);
            }
            """;

        using (compiler)
        {
            ShaderCompileResult result = NativeShaderTree.Compile(compiler!, probe, EnumShaderType.FragmentShader, "frame-offsets-probe");
            Assert.True(result.Success, result.Error);

            SpirvReader spirv = SpirvReader.Parse(result.Spirv);
            uint? block = spirv.BlockAt((uint)FrameGlobals.Set, (uint)FrameGlobals.Binding);
            Assert.True(block.HasValue, "no block at set 0, binding 0");
            Assert.Equal(FrameGlobals.Members.Count, spirv.MemberCount(block!.Value));

            for (int i = 0; i < FrameGlobals.Members.Count; i++)
            {
                UniformMember member = FrameGlobals.Members[i];
                Assert.True(member.Offset == spirv.MemberOffset(block.Value, i),
                    $"{member.Name}: table offset {member.Offset}, SPIR-V offset {spirv.MemberOffset(block.Value, i)}");
                string? name = spirv.MemberName(block.Value, i);
                if (name != null) Assert.Equal(member.Name, name);
                if (member.ArrayLength > 0)
                {
                    Assert.Equal((uint?)member.Type.Size, spirv.ArrayStride(block.Value, i));
                }
            }
        }
    }

    private static float ReadFloat(byte[] shadow, string name)
    {
        Assert.True(FrameGlobals.TryGetMember(name, out UniformMember member));
        return BitConverter.ToSingle(shadow, member.Offset);
    }

    private static GlslType GetType(string name)
    {
        Assert.True(GlslType.TryParse(name, out GlslType type));
        return type;
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/SetConventionTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;

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
            ["OPTIMUM_BINDING_PROGRAM_RECORD"] = SetConvention.ProgramRecordBinding,
            ["OPTIMUM_BINDING_NAMED_BLOCK_FIRST"] = SetConvention.NamedBlockFirstBinding,
            ["OPTIMUM_BINDING_NAMED_BLOCK_LAST"] = SetConvention.NamedBlockLastBinding,
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
        AssertUnique(SetConvention.ProgramRecordBinding, SetConvention.StorageBuffers);
        foreach (SetConvention.Binding buffer in SetConvention.StorageBuffers)
        {
            Assert.False(buffer.Value is >= SetConvention.NamedBlockFirstBinding and <= SetConvention.NamedBlockLastBinding,
                buffer.Define + " sits in the named-block range");
        }
        Assert.False(SetConvention.ProgramRecordBinding is >= SetConvention.NamedBlockFirstBinding and <= SetConvention.NamedBlockLastBinding);
        Assert.Equal(SetConvention.NamedBlockLastBinding + 1, SetConvention.StorageSetBindingCount);
        Assert.Equal(SetConvention.StorageSetBindingCount, SharedPipelineLayout.StorageBindings().Length);
    }

    /// <summary>
    /// The rewriter is the other place the convention's numbers are written down: a
    /// mod-shader program's compiled SPIR-V must name exactly the sets and bindings the
    /// shared layout declares for what it reads - frame block and frame texture in set 0,
    /// the sampler's array in set 1, FaceData, Animation, the record and a named block in
    /// set 2 - and its push block must fit the layout's range.
    /// </summary>
    [SkippableFact]
    public void TheRewritersSetsAndBindingsAreTheConventions()
    {
        const string vertex = """
            #version 330 core
            layout(binding = 3, std430) readonly buffer faceDataBuf { vec4 faces[]; };
            layout(std140) uniform Animation { mat4 values[2]; };
            layout(std140) uniform Extra { vec4 extra; };
            uniform float viewDistance;
            void main() { gl_Position = faces[0] * values[1] * extra * viewDistance; }
            """;
        const string fragment = """
            #version 330 core
            uniform sampler2DShadow shadowMapFar;
            uniform sampler2DArray terrainTex;
            uniform float alphaTest;
            out vec4 outColor;
            void main() { outColor = texture(terrainTex, vec3(alphaTest)) * texture(shadowMapFar, vec3(0.5)); }
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
            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource { Stage = EnumShaderType.VertexShader, Code = vertex, Filename = "convention.vsh" },
                new ShaderStageSource { Stage = EnumShaderType.FragmentShader, Code = fragment, Filename = "convention.fsh" },
            }, compiler, includes: new HashSet<string>(StringComparer.Ordinal) { "fogandlight.vsh" });
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            var declared = new HashSet<(int Set, int Binding, SpirvDescriptorKind Kind)>();
            foreach (DescriptorSetLayoutBinding binding in SharedPipelineLayout.FrameBindings())
                declared.Add((SetConvention.FrameSet, (int)binding.Binding, KindOf(binding.DescriptorType)));
            foreach (SetConvention.Binding array in SetConvention.TextureArrays)
                declared.Add((SetConvention.TextureSet, array.Value, SpirvDescriptorKind.CombinedImageSampler));
            foreach (DescriptorSetLayoutBinding binding in SharedPipelineLayout.StorageBindings())
                declared.Add((SetConvention.StorageSet, (int)binding.Binding, KindOf(binding.DescriptorType)));

            var used = new HashSet<(int, int, SpirvDescriptorKind)>();
            foreach (byte[] spirv in translated.Spirv.Values)
            {
                SpirvModuleReflection reflection = SpirvReflection.Reflect(spirv);
                foreach (SpirvDescriptorBinding binding in reflection.Bindings)
                {
                    var key = (binding.Set, binding.Binding, binding.Kind);
                    Assert.True(declared.Contains(key), $"{binding.Name} at set {binding.Set} binding {binding.Binding} ({binding.Kind}) is not in the shared layout");
                    used.Add(key);
                }
            }

            Assert.Contains((SetConvention.FrameSet, SetConvention.FrameGlobalsBinding, SpirvDescriptorKind.UniformBuffer), used);
            Assert.Contains((SetConvention.FrameSet, SetConvention.FrameTextures[0].Value, SpirvDescriptorKind.CombinedImageSampler), used);
            Assert.Contains((SetConvention.TextureSet, SetConvention.TextureArrays[1].Value, SpirvDescriptorKind.CombinedImageSampler), used);
            Assert.Contains((SetConvention.StorageSet, SetConvention.FaceDataBinding, SpirvDescriptorKind.StorageBuffer), used);
            Assert.Contains((SetConvention.StorageSet, SetConvention.AnimationBinding, SpirvDescriptorKind.StorageBuffer), used);
            Assert.Contains((SetConvention.StorageSet, SetConvention.ProgramRecordBinding, SpirvDescriptorKind.UniformBuffer), used);
            Assert.Contains((SetConvention.StorageSet, SetConvention.NamedBlockFirstBinding, SpirvDescriptorKind.StorageBuffer), used);
            Assert.True(translated.Layout.PushConstantSize <= SetConvention.PushConstantBytes);
        }
    }

    private static SpirvDescriptorKind KindOf(DescriptorType type) => type switch
    {
        DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => SpirvDescriptorKind.UniformBuffer,
        DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => SpirvDescriptorKind.StorageBuffer,
        _ => SpirvDescriptorKind.CombinedImageSampler,
    };

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
}

// Source: Optimum.Render.Vulkan.Tests/SpecializationConventionTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

/// <summary>
/// The specialization constants exist twice, like the set convention:
/// <c>sources/shaders-vk/include/specialization.glsl</c> for native shaders and
/// <see cref="SpecializationConvention" /> for the pipeline key. A drift specializes the wrong
/// constant - shadows switched by the bloom setting - which renders wrong without failing, so the
/// two are compared constant by constant, and each constant against the define it replaces.
/// </summary>
public class SpecializationConventionTests
{
    private static readonly Regex Declaration = new(
        @"^layout\(constant_id = (\d+)\) const (\w+) (\w+) = ([^;]+);\s*$", RegexOptions.Multiline);

    /// <summary>The defines that stay variant axes or are fixed (docs/vulkan.md section 5).</summary>
    private static readonly string[] NotConstants =
        { "TAAMOTION", "TAAMOTIONLOCATION", "USEOIT", "USESSBO", "GREEDYMESH", "MAXANIMATEDELEMENTS" };

    [Fact]
    public void EveryDeclarationInTheIncludeIsTheConstantTheRendererUses()
    {
        var declared = Declaration.Matches(ReadInclude())
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value} = {m.Groups[4].Value}")
            .ToList();
        var expected = SpecializationConvention.Constants
            .Select(c => $"{c.Id} {c.GlslType} {c.Name} = {c.Default}")
            .ToList();
        Assert.Equal(expected, declared);
    }

    [Fact]
    public void IdsAreDenseAndNamesFollowTheDefine()
    {
        for (int i = 0; i < SpecializationConvention.Constants.Length; i++)
        {
            SpecializationConvention.Constant constant = SpecializationConvention.Constants[i];
            Assert.Equal((uint)i, constant.Id);
            Assert.Equal("OPTIMUM_" + constant.Define, constant.Name);
            Assert.True(constant.GlslType is "int" or "float", constant.Name + " has type " + constant.GlslType);
        }
    }

    /// <summary>
    /// Every define <c>registerDefaultShaderCodePrefixes</c> stamps is either a constant or one of
    /// the contract's variant axes, never both, so a new quality define cannot slip past both lists.
    /// </summary>
    [SkippableFact]
    public void EveryPrefixDefineIsExactlyOneOfConstantOrVariantAxis()
    {
        string path = Path.Combine(ShaderCorpus.RepositoryRoot, "build", "VintagestoryLib",
            "Vintagestory.Client.NoObf", "ShaderRegistry.cs");
        Skip.IfNot(File.Exists(path), "No bootstrapped build tree.");
        string source = File.ReadAllText(path);
        int start = source.IndexOf("private static void registerDefaultShaderCodePrefixes", StringComparison.Ordinal);
        Assert.True(start > 0, "registerDefaultShaderCodePrefixes not found");
        int end = source.IndexOf("private static string HandleIncludes", start, StringComparison.Ordinal);
        string body = source[start..end];

        var stamped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(body, @"#define (\w+) ")) stamped.Add(match.Groups[1].Value);

        var constants = SpecializationConvention.Constants.Select(c => c.Define).ToHashSet(StringComparer.Ordinal);
        foreach (string define in constants)
        {
            Assert.True(stamped.Contains(define), define + " is not stamped by registerDefaultShaderCodePrefixes");
        }
        foreach (string define in stamped)
        {
            bool isConstant = constants.Contains(define);
            bool isAxis = NotConstants.Contains(define);
            Assert.True(isConstant ^ isAxis, define + (isConstant ? " is both a constant and an axis" : " is neither a constant nor an axis"));
        }
    }

    /// <summary>No native include keeps a preprocessor branch on a define a constant replaced.</summary>
    [Fact]
    public void NoNativeIncludeBranchesOnAReplacedDefineWithThePreprocessor()
    {
        var defines = SpecializationConvention.Constants.Select(c => c.Define).ToList();
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            foreach (string line in NativeShaderTree.Read(include).Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("#if", StringComparison.Ordinal) && !trimmed.StartsWith("#elif", StringComparison.Ordinal)) continue;
                foreach (string define in defines)
                {
                    Assert.False(Regex.IsMatch(trimmed, @"\b" + define + @"\b"),
                        include + " branches on " + define + " with the preprocessor: " + trimmed);
                }
            }
        }
    }

    /// <summary>The compiled module carries every constant under its id with its type.</summary>
    [SkippableFact]
    public void TheCompiledConstantsCarryTheirIdsAndTypes()
    {
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);
        var probe = new StringBuilder("#version 450\n#include \"specialization.glsl\"\nlayout(location = 0) out vec4 outColor;\nvoid main()\n{\n    float sum = 0.0;\n");
        foreach (SpecializationConvention.Constant constant in SpecializationConvention.Constants)
        {
            probe.Append("    sum += float(").Append(constant.Name).Append(");\n");
        }
        probe.Append("    outColor = vec4(sum);\n}\n");

        using (compiler)
        {
            ShaderCompileResult result = NativeShaderTree.Compile(compiler!, probe.ToString(), EnumShaderType.FragmentShader, "specialization-probe");
            Assert.True(result.Success, result.Error);

            Dictionary<uint, string> ids = SpirvReader.Parse(result.Spirv).SpecIds();
            var expected = SpecializationConvention.Constants.ToDictionary(c => c.Id, c => c.GlslType);
            Assert.Equal(expected.OrderBy(p => p.Key), ids.OrderBy(p => p.Key));
        }
    }

    private static string ReadInclude() =>
        File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, SpecializationConvention.IncludePath)).Replace("\r\n", "\n");
}
}
