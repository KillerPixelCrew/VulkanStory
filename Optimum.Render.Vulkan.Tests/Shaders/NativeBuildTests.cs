// Source: Optimum.Render.Vulkan.Tests/NativeShaderBuildTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Optimum.Render.Vulkan.Shaders;
using Xunit;

/// <summary>
/// The offline native shader compiler, driven through the same entry point
/// <c>tools/shader-compiler</c> runs (<see cref="NativeShaderTool.Run" />), on fixture programs in a
/// temporary source tree - never in <c>sources/shaders-vk</c>. The fixtures include the committed
/// <c>bindings.glsl</c>, so the convention the tool checks is the real one.
/// </summary>
public sealed class NativeShaderBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimum-native-shaders-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "src");
    private string Output => Path.Combine(_root, "out");
    private string ShadersVk => Path.Combine(Output, NativeShaderManifest.DirectoryName);
    private string ManifestPath => Path.Combine(ShadersVk, NativeShaderManifest.FileName);

    public NativeShaderBuildTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "include"));
        File.Copy(Path.Combine(ShaderCorpus.RepositoryRoot, SetConvention.IncludePath), Path.Combine(Source, "include", "bindings.glsl"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ------------------------------------------------------------------ fixtures

    private const string OpaqueInterface = """
        layout(push_constant, scalar) uniform OptimumDraw {
            OPTIMUM_SAMPLER_SLOT(sampler2DArray, terrainTex);
            OPTIMUM_SAMPLER_SLOT(sampler2D, tex2);
            vec3 origin;
        } draw;

        layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram {
            float alphaTest;
            vec4 rgbaFogIn;
        } program;
        """;

    private const string OpaqueVertex = """
        #version 450
        #extension GL_GOOGLE_include_directive : require
        #extension GL_EXT_scalar_block_layout : require
        #include "bindings.glsl"
        #include "fixtureopaque.interface.glsl"

        layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_FACE_DATA) readonly buffer FaceData { uint faces[]; } faceDataBuf;

        layout(location = 0) in vec3 vertexPositionIn;
        layout(location = 1) in vec2 uvIn;
        #if GREEDYMESH == 1
        layout(location = 2) in ivec2 greedySize;
        #endif
        layout(location = 0) out vec2 uv;

        void main() {
            uv = uvIn;
            gl_Position = vec4(vertexPositionIn + draw.origin, 1.0);
        }
        """;

    private const string OpaqueFragment = """
        #version 450
        #extension GL_GOOGLE_include_directive : require
        #extension GL_EXT_scalar_block_layout : require
        #include "bindings.glsl"
        #include "fixtureopaque.interface.glsl"
        #include "fogandlight.frag.glsl"

        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 outColor;
        #if GBUFFER == 1
        layout(location = 1) out vec4 outGlow;
        #endif
        #if TAAMOTION == 1
        layout(location = 2 + 2 * GBUFFER) out vec4 outMotion;
        #endif

        layout(constant_id = 1) const int OPTIMUM_BLOOM = 0;

        void main() {
            vec4 color = texture(optimumTextures2DArray[draw.terrainTex], vec3(uv, 0.0));
            color *= texture(optimumTextures2D[draw.tex2], uv);
            if (color.a < program.alphaTest) discard;
            outColor = mix(color, program.rgbaFogIn, fixtureFog(uv));
        #if GBUFFER == 1
            outGlow = vec4(OPTIMUM_BLOOM != 0 ? 1.0 : 0.0);
        #endif
        #if TAAMOTION == 1
            outMotion = vec4(0.0);
        #endif
        }
        """;

    private const string FogInclude = """
        #ifndef FIXTURE_FOGANDLIGHT_FRAG
        #define FIXTURE_FOGANDLIGHT_FRAG
        float fixtureFog(vec2 at) { return texture(shadowMapFar, vec3(at, 0.5)); }
        #endif
        """;

    private const string PostVertex = """
        #version 450
        void main() {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string PostFragment = """
        #version 450
        #extension GL_GOOGLE_include_directive : require
        #extension GL_EXT_scalar_block_layout : require
        #include "bindings.glsl"
        layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram {
            float exposure;
        } program;
        layout(location = 0) out vec4 outColor;
        void main() { outColor = vec4(program.exposure); }
        """;

    private void WriteSource(string name, string text) => File.WriteAllText(Path.Combine(Source, name), text);

    private void WriteFixtures()
    {
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface);
        WriteSource("fixtureopaque.vert", OpaqueVertex);
        WriteSource("fixtureopaque.frag", OpaqueFragment);
        File.WriteAllText(Path.Combine(Source, "include", "fogandlight.frag.glsl"), FogInclude);
        WriteSource("fixturepost.vert", PostVertex);
        WriteSource("fixturepost.frag", PostFragment);
    }

    private (int Exit, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = NativeShaderTool.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private void Build()
    {
        (int exit, _, string error) = Run("--build", Source, Output);
        Assert.True(exit == 0, error);
    }

    // ------------------------------------------------------------------ build

    [Fact]
    public void EveryAxisCombinationOfEveryProgramIsCompiledHashedAndListed()
    {
        WriteFixtures();
        Build();

        NativeShaderManifest manifest = NativeShaderManifest.Load(ManifestPath);
        Assert.Equal(NativeShaderManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        using (var compiler = new ShaderCompiler()) Assert.Equal(compiler.Identity, manifest.Toolchain);
        Assert.Equal(new[] { "fixtureopaque", "fixturepost" }, manifest.Programs.Select(p => p.Name));

        NativeProgram opaque = manifest.Programs[0];
        // GREEDYMESH comes from the vertex stage, GBUFFER and TAAMOTION from the fragment stage.
        Assert.Equal(new[] { "GBUFFER", "GREEDYMESH", "TAAMOTION" }, opaque.Axes);
        Assert.Equal(
            new[]
            {
                "GBUFFER=0,GREEDYMESH=0,TAAMOTION=0", "GBUFFER=0,GREEDYMESH=0,TAAMOTION=1",
                "GBUFFER=0,GREEDYMESH=1,TAAMOTION=0", "GBUFFER=0,GREEDYMESH=1,TAAMOTION=1",
                "GBUFFER=1,GREEDYMESH=0,TAAMOTION=0", "GBUFFER=1,GREEDYMESH=0,TAAMOTION=1",
                "GBUFFER=1,GREEDYMESH=1,TAAMOTION=0", "GBUFFER=1,GREEDYMESH=1,TAAMOTION=1",
            },
            opaque.Variants.Select(v => v.Key));

        NativeProgram post = manifest.Programs[1];
        Assert.Empty(post.Axes);
        NativeVariant postVariant = Assert.Single(post.Variants);
        Assert.Equal("", postVariant.Key);
        Assert.Equal(new[] { "fixturepost.vert.spv", "fixturepost.frag.spv" }, postVariant.Stages.Select(s => s.Spirv));

        NativeStage stage = opaque.Variants[5].Stages[1];
        Assert.Equal(("fragment", "fixtureopaque.frag", "fixtureopaque.GBUFFER1.GREEDYMESH0.TAAMOTION1.frag.spv"), (stage.Stage, stage.Source, stage.Spirv));

        var listed = manifest.Programs.SelectMany(p => p.Variants).SelectMany(v => v.Stages).ToList();
        Assert.Equal(18, listed.Count);
        foreach (NativeStage entry in listed)
        {
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(ShadersVk, entry.Spirv)))), entry.Sha256);
        }
        Assert.Equal(
            listed.Select(s => s.Spirv).Append(NativeShaderManifest.FileName).OrderBy(n => n, StringComparer.Ordinal),
            Directory.GetFiles(ShadersVk).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EachVariantRecordsItsReflectedInterface()
    {
        WriteFixtures();
        Build();
        NativeShaderManifest manifest = NativeShaderManifest.Load(ManifestPath);

        NativeVariant full = manifest.Find("fixtureopaque", "GBUFFER=1,GREEDYMESH=1,TAAMOTION=1")!;
        Assert.Equal("OptimumDraw", full.Push!.TypeName);
        Assert.Equal(20, full.Push.Size);
        Assert.Equal(new[] { "terrainTex uint @0 4", "tex2 uint @4 4", "origin vec3 @8 12" },
            full.Push.Members.Select(m => m.Name + " " + m.Type + " @" + m.Offset + " " + m.Size));
        Assert.Equal(new[] { "alphaTest float @0 4", "rgbaFogIn vec4 @4 16" },
            full.Record!.Members.Select(m => m.Name + " " + m.Type + " @" + m.Offset + " " + m.Size));

        Assert.Equal(
            new[] { "0 terrainTex sampler2DArray optimumTextures2DArray b1 @0", "1 tex2 sampler2D optimumTextures2D b0 @4" },
            full.Samplers.Select(s => s.Order + " " + s.Name + " " + s.GlslType + " " + s.BindlessArray + " b" + s.ArrayBinding + " @" + s.PushOffset));

        // fogandlight.frag.glsl stands for fogandlight.fsh, the owner of these FrameGlobals members.
        Assert.Equal(FrameGlobals.Members.Where(m => FrameGlobals.OwnerOf(m.Name) == "fogandlight.fsh").Select(m => m.Name), full.FrameMembers);
        NativeFrameTexture shadow = Assert.Single(full.FrameTextures);
        Assert.Equal(("shadowMapFar", "sampler2DShadow", 1), (shadow.Name, shadow.GlslType, shadow.Binding));

        NativeStorageBinding faces = Assert.Single(full.StorageBindings);
        Assert.Equal(("faceDataBuf", 2, 0, "storageBuffer", false), (faces.Name, faces.Set, faces.Binding, faces.DescriptorType, faces.Used));

        Assert.Equal(new[] { "0 vertexPositionIn vec3", "1 uvIn vec2", "2 greedySize ivec2" },
            full.VertexInputs.Select(v => v.Location + " " + v.Name + " " + v.Type));
        Assert.Equal(new[] { "0 outColor vec4", "1 outGlow vec4", "4 outMotion vec4" },
            full.FragmentOutputs.Select(v => v.Location + " " + v.Name + " " + v.Type));
        Assert.Equal((1u << 0) | (1u << 1) | (1u << 4), full.WrittenOutputs);

        NativeSpecConstant bloom = Assert.Single(full.SpecializationConstants);
        Assert.Equal((1, "OPTIMUM_BLOOM", "int", 0.0), (bloom.Id, bloom.Name, bloom.Type, bloom.Default));

        NativeVariant bare = manifest.Find("fixtureopaque", "GBUFFER=0,GREEDYMESH=0,TAAMOTION=1")!;
        Assert.Equal(new[] { "0 outColor", "2 outMotion" }, bare.FragmentOutputs.Select(v => v.Location + " " + v.Name));
        Assert.Equal((1u << 0) | (1u << 2), bare.WrittenOutputs);
        Assert.DoesNotContain(bare.VertexInputs, v => v.Name == "greedySize");

        NativeVariant post = manifest.Find("fixturepost", "")!;
        Assert.Null(post.Push);
        Assert.Empty(post.Samplers);
        Assert.Empty(post.FrameMembers);
        Assert.Empty(post.VertexInputs);
        Assert.Equal(1u, post.WrittenOutputs);
    }

    [Fact]
    public void AnEmptySourceTreeYieldsAValidEmptyManifest()
    {
        (int exit, string output, string error) = Run("--build", Source, Output);

        Assert.True(exit == 0, error);
        Assert.Contains("0 program(s)", output);
        NativeShaderManifest manifest = NativeShaderManifest.Load(ManifestPath);
        Assert.Empty(manifest.Programs);
        Assert.Equal(NativeShaderManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(new[] { NativeShaderManifest.FileName }, Directory.GetFiles(ShadersVk).Select(Path.GetFileName));
        Assert.Equal(0, Run("--verify", Source, Output).Exit);
    }

    [Fact]
    public void AnUnchangedRebuildRewritesNothing()
    {
        WriteFixtures();
        Build();
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.GetFiles(ShadersVk)) File.SetLastWriteTimeUtc(file, past);

        Build();

        Assert.All(Directory.GetFiles(ShadersVk), file => Assert.Equal(past, File.GetLastWriteTimeUtc(file)));
    }

    [Fact]
    public void ARebuildDeletesTheSpirvOfARemovedProgram()
    {
        WriteFixtures();
        Build();
        File.Delete(Path.Combine(Source, "fixturepost.vert"));
        File.Delete(Path.Combine(Source, "fixturepost.frag"));

        Build();

        Assert.DoesNotContain(Directory.GetFiles(ShadersVk), f => Path.GetFileName(f).StartsWith("fixturepost", StringComparison.Ordinal));
        Assert.Null(NativeShaderManifest.Load(ManifestPath).FindProgram("fixturepost"));
    }

    // ------------------------------------------------------------------ verify and single

    [Fact]
    public void VerifyPassesOnAFreshBuildAndFailsOnAnyDifference()
    {
        WriteFixtures();
        Build();
        Assert.Equal(0, Run("--verify", Source, Output).Exit);

        string blob = Path.Combine(ShadersVk, "fixturepost.frag.spv");
        byte[] original = File.ReadAllBytes(blob);
        byte[] tampered = (byte[])original.Clone();
        tampered[^1] ^= 0xFF;
        File.WriteAllBytes(blob, tampered);
        (int exit, _, string error) = Run("--verify", Source, Output);
        Assert.Equal(1, exit);
        Assert.Contains("fixturepost.frag.spv differs", error);
        File.WriteAllBytes(blob, original);

        File.WriteAllBytes(Path.Combine(ShadersVk, "leftover.vert.spv"), original);
        (exit, _, error) = Run("--verify", Source, Output);
        Assert.Equal(1, exit);
        Assert.Contains("unexpected leftover.vert.spv", error);
        File.Delete(Path.Combine(ShadersVk, "leftover.vert.spv"));

        WriteSource("fixturepost.frag", PostFragment.Replace("vec4(program.exposure)", "vec4(program.exposure * 2.0)"));
        (exit, _, error) = Run("--verify", Source, Output);
        Assert.Equal(1, exit);
        Assert.Contains(NativeShaderManifest.FileName + " differs", error);

        File.Delete(ManifestPath);
        Assert.Equal(1, Run("--verify", Source, Output).Exit);
    }

    [Fact]
    public void SingleRebuildsOneProgramAndLeavesTheOthers()
    {
        WriteFixtures();
        Build();
        string opaqueBefore = File.ReadAllText(Path.Combine(ShadersVk, "fixtureopaque.GBUFFER0.GREEDYMESH0.TAAMOTION0.frag.spv"));
        WriteSource("fixturepost.frag", PostFragment.Replace("vec4(program.exposure)", "vec4(program.exposure * 2.0)"));

        (int exit, string output, string error) = Run("--single", "fixturepost", Source, Output);

        Assert.True(exit == 0, error);
        Assert.Contains("rebuilt fixturepost", output);
        Assert.Equal(0, Run("--verify", Source, Output).Exit);
        Assert.Equal(opaqueBefore, File.ReadAllText(Path.Combine(ShadersVk, "fixtureopaque.GBUFFER0.GREEDYMESH0.TAAMOTION0.frag.spv")));
        Assert.Equal(new[] { "fixtureopaque", "fixturepost" }, NativeShaderManifest.Load(ManifestPath).Programs.Select(p => p.Name));

        Assert.Equal(1, Run("--single", "nosuchprogram", Source, Output).Exit);
    }

    [Fact]
    public void SingleNeedsAnExistingManifest()
    {
        WriteFixtures();
        (int exit, _, string error) = Run("--single", "fixturepost", Source, Output);
        Assert.Equal(1, exit);
        Assert.Contains("run --build first", error);
    }

    [Fact]
    public void AWrongCommandLineIsAUsageError()
    {
        Assert.Equal(2, Run().Exit);
        Assert.Equal(2, Run("--build", Source).Exit);
        Assert.Equal(2, Run("--single", Source, Output).Exit);
        Assert.Equal(2, Run("--frobnicate", Source, Output).Exit);
    }

    // ------------------------------------------------------------------ what the tool refuses

    private string BuildFails()
    {
        (int exit, _, string error) = Run("--build", Source, Output);
        Assert.Equal(1, exit);
        Assert.False(File.Exists(ManifestPath), "a failed build must write nothing");
        return error;
    }

    [Fact]
    public void ASamplerSlotDeclaredWithTheWrongTypeFails()
    {
        WriteFixtures();
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface.Replace("OPTIMUM_SAMPLER_SLOT(sampler2DArray, terrainTex)", "OPTIMUM_SAMPLER_SLOT(samplerCube, terrainTex)"));
        Assert.Contains("sampler slot 'terrainTex' is declared samplerCube", BuildFails());
    }

    [Fact]
    public void AnUndeclaredSlotIndexingATextureArrayFails()
    {
        WriteFixtures();
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface.Replace("OPTIMUM_SAMPLER_SLOT(sampler2D, tex2)", "uint tex2"));
        Assert.Contains("push member 'tex2' indexes a texture array but is not declared with OPTIMUM_SAMPLER_SLOT", BuildFails());
    }

    [Fact]
    public void ASamplerSlotAfterAnotherPushMemberFails()
    {
        WriteFixtures();
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface
            .Replace("    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2);\n    vec3 origin;", "    vec3 origin;\n    OPTIMUM_SAMPLER_SLOT(sampler2D, tex2);"));
        Assert.Contains("sampler slot 'tex2' follows a non-slot push member", BuildFails());
    }

    [Fact]
    public void APushBlockOverTheLimitFails()
    {
        WriteFixtures();
        WriteSource("fixtureopaque.interface.glsl", OpaqueInterface.Replace("vec3 origin;", "vec3 origin;\n    mat4 a;\n    mat4 b;"));
        Assert.Contains("push block is 148 B, the limit is 128", BuildFails());
    }

    [Fact]
    public void ABindingOutsideTheSetConventionFails()
    {
        WriteFixtures();
        WriteSource("fixturepost.frag", PostFragment.Replace(
            "layout(location = 0) out vec4 outColor;",
            "layout(set = 1, binding = 0) uniform sampler2DArray wrong[];\nlayout(location = 0) out vec4 outColor;")
            .Replace("vec4(program.exposure)", "texture(wrong[0], vec3(0.0)) * program.exposure"));
        string error = BuildFails();
        Assert.Contains("'wrong' at set 1 binding 0 (reflected CombinedImageSampler sampler2DArray[1]) must be sampler2D optimumTextures2D[]", error);
    }

    [Fact]
    public void AnAxisTestedForDefinitionFails()
    {
        WriteFixtures();
        WriteSource("fixturepost.frag", PostFragment.Replace("layout(location = 0) out", "#ifdef TAAMOTION\n#endif\nlayout(location = 0) out"));
        Assert.Contains("#ifdef TAAMOTION", BuildFails());
    }

    [Fact]
    public void ALoneStageAMissingIncludeAndACompileErrorFail()
    {
        WriteFixtures();
        WriteSource("lonely.vert", PostVertex);
        Assert.Contains("lonely.vert has no lonely.frag", BuildFails());
        File.Delete(Path.Combine(Source, "lonely.vert"));

        WriteSource("fixturepost.frag", PostFragment.Replace("#include \"bindings.glsl\"", "#include \"bindings.glsl\"\n#include \"missing.glsl\""));
        Assert.Contains("includes 'missing.glsl'", BuildFails());

        WriteSource("fixturepost.frag", PostFragment.Replace("vec4(program.exposure)", "vec4(undeclaredName)"));
        Assert.Contains("fixturepost.frag", BuildFails());
    }

    [Fact]
    public void TheSamplerSlotMacroIsDeclaredByBindingsGlsl()
    {
        string bindings = File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, SetConvention.IncludePath));
        Assert.Contains("#define " + NativeShaderBuilder.SamplerSlotMacro + "(glslType, name) uint name", bindings);
    }

    [Fact]
    public void OwnerFilesMapToTheirNativeIncludes()
    {
        Assert.Equal(new[] { "fogandlight.vert.glsl", "fogandlight.glsl" }, NativeShaderBuilder.NativeIncludesFor("fogandlight.vsh"));
        Assert.Equal(new[] { "skycolor.frag.glsl", "skycolor.glsl" }, NativeShaderBuilder.NativeIncludesFor("skycolor.fsh"));
    }

    [Fact]
    public void TheAxisListIsTheContractsAndSorted()
    {
        Assert.Equal(
            new[] { "TAAMOTION", "GBUFFER", "USEOIT", "USESSBO", "GREEDYMESH", "ALLOWDEPTHOFFSET", "GLOWSUB", "VEC3SCALE" }.OrderBy(a => a, StringComparer.Ordinal),
            NativeShaderBuilder.VariantAxes);
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/NativeShaderIncludeTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

/// <summary>
/// The shared native includes (<c>sources/shaders-vk/include</c>, docs/vulkan-native-shaders.md
/// sections 1, 3 and 4): each compiles inside a probe program the way a family program will
/// include it, each port accounts for every uniform its game file declared, a verbatim port keeps
/// the game file's code token for token, a transformed one keeps every function signature, and
/// the push block and record forms of section 4 compile with their members as global names.
/// </summary>
public class NativeShaderIncludeTests
{
    public static IEnumerable<object[]> ProbeCases()
    {
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            foreach (EnumShaderType stage in NativeShaderTree.StagesOf(include))
            {
                yield return new object[] { include, stage, true };
                yield return new object[] { include, stage, false };
            }
        }
    }

    /// <summary>
    /// A probe includes bindings.glsl, frame.glsl and specialization.glsl, declares a record with
    /// every name the include (and what it includes) leaves to the program, then includes it.
    ///
    /// With owner names active the probe defines every frame owner macro, as a program whose
    /// stages include every owner would, so every frame member comes from the block. With them
    /// inactive only the includes themselves activate owners, and every other frame-ownable name
    /// is a record member, as in a program that includes the fragment half of fog and light alone.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(ProbeCases))]
    public void TheIncludeCompilesInsideAProbeProgram(string include, EnumShaderType stage, bool ownerNamesActive)
    {
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);

        string probe = BuildProbe(include, stage, ownerNamesActive);
        using (compiler)
        {
            ShaderCompileResult result = NativeShaderTree.Compile(compiler!, probe, stage,
                "probe-" + include.Replace('.', '-') + (ownerNamesActive ? "-owners" : "-alone"));
            Assert.True(result.Success, include + " (" + stage + ", owner names " +
                (ownerNamesActive ? "active" : "inactive") + "):\n" + result.Error + "\n--- probe ---\n" + probe);
            Assert.NotEmpty(result.Spirv);
        }
    }

    internal static string BuildProbe(string include, EnumShaderType stage, bool ownerNamesActive)
    {
        List<string> closure = NativeShaderTree.Closure(include);
        var ports = closure.Select(NativeShaderTree.PortOf).Where(port => port != null).Select(port => port!).ToList();
        var ownersInClosure = ports.Select(port => port.FrameOwner).Where(owner => owner != null).ToHashSet(StringComparer.Ordinal);

        var probe = new StringBuilder();
        probe.Append("#version 450\n#extension GL_EXT_scalar_block_layout : require\n");
        if (ownerNamesActive)
        {
            foreach (string owner in FrameGlobals.Owners) probe.Append("#define ").Append(FrameGlobals.OwnerMacro(owner)).Append('\n');
        }
        // Variant axes a program always stamps; the probe takes the branch that declares the most.
        probe.Append("#define USEOIT 1\n");
        probe.Append("#include \"bindings.glsl\"\n#include \"frame.glsl\"\n#include \"specialization.glsl\"\n");

        var record = new List<string>();
        var symbols = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (NativeShaderTree.Port port in ports)
        {
            foreach (NativeShaderTree.Declaration uniform in port.ProgramUniforms)
            {
                if (!seen.Add(uniform.Name)) continue;
                string? owner = FrameGlobals.OwnerOf(uniform.Name);
                bool fromFrame = owner != null && (ownerNamesActive || ownersInClosure.Contains(owner));
                if (!fromFrame) record.Add("    " + uniform.Text + ";\n");
            }
            foreach (NativeShaderTree.Declaration symbol in port.ProgramSymbols)
            {
                if (seen.Add(symbol.Name)) symbols.Add(symbol.Text + ";\n");
            }
        }
        if (record.Count > 0)
        {
            probe.Append("layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram\n{\n");
            foreach (string member in record) probe.Append(member);
            probe.Append("};\n");
        }
        foreach (string symbol in symbols) probe.Append(symbol);

        probe.Append("#include \"").Append(include).Append("\"\n");
        probe.Append(stage == EnumShaderType.VertexShader
            ? "void main()\n{\n    gl_Position = vec4(0.0);\n}\n"
            : "void main()\n{\n}\n");
        return probe.ToString();
    }

    /// <summary>
    /// Every uniform the game file declares is accounted for by the port: a frame member the file
    /// owns, a frame texture from bindings.glsl, or a program uniform in the header - with the
    /// game file's type. A game update that adds a uniform fails here.
    /// </summary>
    [SkippableFact]
    public void EveryPortAccountsForEveryUniformItsGameFileDeclares()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();
        var uniform = new Regex(@"^\s*uniform\s+(\w+)\s+(\w+)", RegexOptions.Multiline);

        var ported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            NativeShaderTree.Port? port = NativeShaderTree.PortOf(include);
            if (port == null) continue;
            Assert.True(ported.Add(port.GameFile), port.GameFile + " is ported twice");
            Assert.True(includes.TryGetValue(port.GameFile, out string? source), port.GameFile + " is not a game include");

            var declared = uniform.Matches(source!).Select(m => m.Groups[1].Value + " " + m.Groups[2].Value)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();

            var accounted = new List<string>();
            foreach (UniformMember member in FrameGlobals.Members)
            {
                if (FrameGlobals.OwnerOf(member.Name) == port.GameFile && declared.Contains(member.Type.Name + " " + member.Name))
                {
                    accounted.Add(member.Type.Name + " " + member.Name);
                }
            }
            accounted.AddRange(port.FrameTextures.Select(d => d.Type + " " + d.Name));
            accounted.AddRange(port.ProgramUniforms.Select(d => d.Type + " " + d.Name));
            accounted.Sort(StringComparer.Ordinal);

            Assert.True(declared.SequenceEqual(accounted),
                $"{include}: game file declares [{string.Join(", ", declared)}], port accounts for [{string.Join(", ", accounted)}]");

            foreach (NativeShaderTree.Declaration texture in port.FrameTextures)
            {
                Assert.Contains(SetConvention.FrameTextures, binding => binding.Name == texture.Name && binding.GlslType == texture.Type);
            }
            foreach (NativeShaderTree.Declaration programUniform in port.ProgramUniforms)
            {
                Assert.False(FrameGlobals.OwnerOf(programUniform.Name) == port.GameFile,
                    include + " lists " + programUniform.Name + " as a program uniform but owns it");
            }
        }

        var expectedPorts = includes.Keys
            .Where(name => name is not ("default.fsh" or "printvalues.fsh"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(expectedPorts.SetEquals(ported),
            "ported [" + string.Join(", ", ported.OrderBy(s => s)) + "], game includes [" + string.Join(", ", expectedPorts.OrderBy(s => s)) + "]");
    }

    /// <summary>
    /// Every frame owner has exactly one native include, the port of the owner file, and that
    /// include activates its names: it defines the owner macro and includes frame.glsl.
    /// </summary>
    [Fact]
    public void EveryFrameOwnerIsActivatedByItsPortAndNoOther()
    {
        foreach (string owner in FrameGlobals.Owners)
        {
            var claiming = NativeShaderTree.IncludeNames()
                .Where(include => NativeShaderTree.PortOf(include)?.FrameOwner == owner)
                .ToList();
            Assert.True(claiming.Count == 1, owner + " is claimed by [" + string.Join(", ", claiming) + "]");

            NativeShaderTree.Port port = NativeShaderTree.PortOf(claiming[0])!;
            Assert.Equal(owner, port.GameFile);
            string text = NativeShaderTree.Read(claiming[0]);
            Assert.Contains("#define " + FrameGlobals.OwnerMacro(owner) + "\n#include \"frame.glsl\"", text);

            foreach (string other in NativeShaderTree.IncludeNames().Where(include => include != claiming[0]))
            {
                Assert.DoesNotContain("#define " + FrameGlobals.OwnerMacro(owner), NativeShaderTree.Read(other));
            }
        }
    }

    /// <summary>
    /// A verbatim port is the game file's code, token for token, once comments, preprocessor lines
    /// and interface declarations (uniform, in, out, with or without a layout) are set aside - the
    /// only things a verbatim port may change.
    /// </summary>
    [SkippableFact]
    public void VerbatimPortsKeepTheGameFilesCodeTokenForToken()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();

        int checkedPorts = 0;
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            NativeShaderTree.Port? port = NativeShaderTree.PortOf(include);
            if (port == null || port.Kind != "verbatim") continue;
            checkedPorts++;

            List<string> game = CodeTokens(includes[port.GameFile]);
            List<string> native = CodeTokens(NativeShaderTree.Read(include));
            int firstDifference = Enumerable.Range(0, Math.Min(game.Count, native.Count))
                .FirstOrDefault(i => game[i] != native[i], Math.Min(game.Count, native.Count));
            Assert.True(game.SequenceEqual(native),
                $"{include} differs from {port.GameFile} at token {firstDifference}: game '{string.Join(" ", game.Skip(firstDifference).Take(12))}', port '{string.Join(" ", native.Skip(firstDifference).Take(12))}'");
        }
        Assert.True(checkedPorts >= 10, "only " + checkedPorts + " verbatim ports found");
    }

    /// <summary>
    /// A transformed port rewrote preprocessor branches into specialization-constant branches; it
    /// keeps every function the game file defines, with the same return type, name and parameters,
    /// in the same order.
    /// </summary>
    [SkippableFact]
    public void TransformedPortsKeepEveryFunctionSignature()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();

        int checkedPorts = 0;
        foreach (string include in NativeShaderTree.IncludeNames())
        {
            NativeShaderTree.Port? port = NativeShaderTree.PortOf(include);
            if (port == null || port.Kind != "transformed") continue;
            checkedPorts++;
            Assert.Equal(Signatures(includes[port.GameFile]), Signatures(NativeShaderTree.Read(include)));
        }
        Assert.Equal(5, checkedPorts);
    }

    /// <summary>
    /// Section 4's forms: a push block and a program record without instance names compile, and
    /// their members are global names a program's code uses directly. The record is a uniform
    /// block at set 2, binding OPTIMUM_BINDING_PROGRAM_RECORD, the push block a push-constant
    /// block; both are laid out scalar, so a vec3 after a uint sits at offset 4, not 16.
    /// </summary>
    [SkippableTheory]
    [InlineData(EnumShaderType.VertexShader)]
    [InlineData(EnumShaderType.FragmentShader)]
    public void AnonymousPushAndRecordBlocksExposeTheirMembersAsGlobalNames(EnumShaderType stage)
    {
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);
        const string interfaceBlocks = """
            #version 450
            #extension GL_EXT_scalar_block_layout : require
            #include "bindings.glsl"

            layout(push_constant, scalar) uniform OptimumDraw
            {
                uint terrainTex;
                vec3 origin;
                mat4 modelViewMatrix;
            };

            layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar) uniform OptimumProgram
            {
                float alphaTest;
                vec4 rgbaFogIn;
            };

            """;
        string body = stage == EnumShaderType.VertexShader
            ? """
              layout(location = 0) in vec3 xyz;
              void main()
              {
                  gl_Position = modelViewMatrix * vec4(xyz + origin, 1.0) * rgbaFogIn.a * alphaTest + vec4(float(terrainTex));
              }
              """
            : """
              layout(location = 0) out vec4 outColor;
              void main()
              {
                  outColor = texture(optimumTextures2D[terrainTex], vec2(0.5)) * rgbaFogIn;
                  outColor.rgb += (modelViewMatrix * vec4(origin, 1.0)).rgb;
                  if (outColor.a < alphaTest) discard;
              }
              """;

        using (compiler)
        {
            ShaderCompileResult result = NativeShaderTree.Compile(compiler!, interfaceBlocks + body, stage, "interface-blocks-probe");
            Assert.True(result.Success, result.Error);

            SpirvReader spirv = SpirvReader.Parse(result.Spirv);
            uint? record = spirv.BlockAt((uint)SetConvention.StorageSet, (uint)SetConvention.ProgramRecordBinding);
            Assert.True(record.HasValue, "no record block at set 2, binding 3");
            Assert.Equal(new uint[] { 0, 4 }, Enumerable.Range(0, spirv.MemberCount(record!.Value)).Select(i => spirv.MemberOffset(record.Value, i)));

            List<uint> push = spirv.BlocksIn(SpirvReader.StoragePushConstant);
            Assert.Single(push);
            Assert.Equal(new uint[] { 0, 4, 16 }, Enumerable.Range(0, spirv.MemberCount(push[0])).Select(i => spirv.MemberOffset(push[0], i)));
        }
    }

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex LineComment = new(@"//[^\n]*");
    private static readonly Regex InterfaceDeclaration = new(
        @"^\s*(layout\s*\([^)]*\)\s*)?(uniform|in|out)\s+[^;{(]*;", RegexOptions.Multiline);
    private static readonly Regex Token = new(@"\w+|[^\s\w]");

    private static string StripComments(string source) =>
        LineComment.Replace(BlockComment.Replace(source.Replace("\r\n", "\n"), " "), "");

    private static List<string> CodeTokens(string source)
    {
        string text = StripComments(source);
        text = string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith("#", StringComparison.Ordinal)));
        text = InterfaceDeclaration.Replace(text, "");
        return Token.Matches(text).Select(m => m.Value).ToList();
    }

    private static readonly Regex FunctionDefinition = new(
        @"^[ \t]*(\w+)[ \t]+(\w+)[ \t]*\(([^)]*)\)\s*\{", RegexOptions.Multiline);

    private static List<string> Signatures(string source) =>
        FunctionDefinition.Matches(StripComments(source))
            .Where(m => m.Groups[1].Value is not ("if" or "for" or "while" or "switch" or "return" or "else"))
            .Select(m => m.Groups[1].Value + " " + m.Groups[2].Value + "(" +
                Regex.Replace(m.Groups[3].Value.Trim(), @"\s+", " ") + ")")
            .ToList();
}
}

// Source: Optimum.Render.Vulkan.Tests/NativeShaderManifestTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Shaders;
using Xunit;

/// <summary>
/// <c>shaders.manifest.json</c>: every field survives a write and a read, the text is deterministic
/// (the <c>--verify</c> gate compares bytes), and a manifest of another schema version is refused
/// rather than half-read.
/// </summary>
public class NativeShaderManifestTests
{
    private static NativeShaderManifest Sample() => new()
    {
        Toolchain = "glsl;vulkan1.3;spirv1.5;performance;shaderc-sha256:abc",
        Programs =
        {
            new NativeProgram
            {
                Name = "chunkopaque",
                Axes = { "GBUFFER", "TAAMOTION" },
                Variants =
                {
                    new NativeVariant
                    {
                        Key = "GBUFFER=1,TAAMOTION=1",
                        Stages =
                        {
                            new NativeStage { Stage = "vertex", Source = "chunkopaque.vert", Spirv = "chunkopaque.GBUFFER1.TAAMOTION1.vert.spv", Sha256 = "00ff" },
                            new NativeStage { Stage = "fragment", Source = "chunkopaque.frag", Spirv = "chunkopaque.GBUFFER1.TAAMOTION1.frag.spv", Sha256 = "ff00" },
                        },
                        Push = new NativeBlock
                        {
                            TypeName = "OptimumDraw",
                            Size = 20,
                            Members =
                            {
                                new NativeMember { Name = "terrainTex", Type = "uint", Offset = 0, Size = 4 },
                                new NativeMember { Name = "origin", Type = "vec3", Offset = 8, Size = 12 },
                            },
                        },
                        Record = null,
                        FrameMembers = { "zNear", "zFar" },
                        Samplers = { new NativeSampler { Name = "terrainTex", GlslType = "sampler2DArray", BindlessArray = "optimumTextures2DArray", ArrayBinding = 1, PushOffset = 0, Order = 0 } },
                        FrameTextures = { new NativeFrameTexture { Name = "shadowMapFar", GlslType = "sampler2DShadow", Binding = 1 } },
                        StorageBindings = { new NativeStorageBinding { Name = "faceDataBuf", Set = 2, Binding = 0, DescriptorType = "storageBuffer", ArrayLength = 0, RuntimeArray = false, Used = true } },
                        VertexInputs = { new NativeInterfaceVariable { Location = 0, Name = "vertexPositionIn", Type = "vec3" } },
                        FragmentOutputs =
                        {
                            new NativeInterfaceVariable { Location = 0, Name = "outColor", Type = "vec4" },
                            new NativeInterfaceVariable { Location = 4, Name = "outMotion", Type = "vec4", ArrayLength = 0 },
                        },
                        WrittenOutputs = 0b10001,
                        SpecializationConstants =
                        {
                            new NativeSpecConstant { Id = 1, Name = "OPTIMUM_FXAA", Type = "bool", Default = 1 },
                            new NativeSpecConstant { Id = 9, Name = "OPTIMUM_MINBRIGHT", Type = "float", Default = 0.125 },
                        },
                    },
                },
            },
        },
    };

    [Fact]
    public void EveryFieldSurvivesAWriteAndARead()
    {
        NativeShaderManifest original = Sample();
        string json = original.ToJson();

        NativeShaderManifest read = NativeShaderManifest.Parse(json);

        Assert.Equal(json, read.ToJson());
        Assert.Equal(original.Toolchain, read.Toolchain);
        NativeVariant variant = read.Find("chunkopaque", "GBUFFER=1,TAAMOTION=1")!;
        Assert.Equal(new[] { "GBUFFER", "TAAMOTION" }, read.Programs[0].Axes);
        Assert.Equal("chunkopaque.GBUFFER1.TAAMOTION1.frag.spv", variant.Stages[1].Spirv);
        Assert.Equal("ff00", variant.Stages[1].Sha256);
        Assert.Equal(20, variant.Push!.Size);
        Assert.Equal(("origin", "vec3", 8, 12), (variant.Push.Members[1].Name, variant.Push.Members[1].Type, variant.Push.Members[1].Offset, variant.Push.Members[1].Size));
        Assert.Null(variant.Record);
        Assert.Equal(new[] { "zNear", "zFar" }, variant.FrameMembers);
        Assert.Equal(("optimumTextures2DArray", 1, 0, 0), (variant.Samplers[0].BindlessArray, variant.Samplers[0].ArrayBinding, variant.Samplers[0].PushOffset, variant.Samplers[0].Order));
        Assert.Equal("shadowMapFar", variant.FrameTextures[0].Name);
        Assert.True(variant.StorageBindings[0].Used);
        Assert.Equal(4, variant.FragmentOutputs[1].Location);
        Assert.Equal(0b10001u, variant.WrittenOutputs);
        Assert.Equal(1.0, variant.SpecializationConstants[0].Default);
        Assert.Equal(0.125, variant.SpecializationConstants[1].Default);
        Assert.Null(read.Find("chunkopaque", "GBUFFER=0,TAAMOTION=1"));
    }

    [Fact]
    public void TheTextIsDeterministicWithUnixLineEndingsAndBoolDefaults()
    {
        string json = Sample().ToJson();

        Assert.Equal(json, Sample().ToJson());
        Assert.DoesNotContain("\r", json);
        Assert.EndsWith("}\n", json);
        Assert.Contains("\"default\": true", json);
        Assert.Contains("\"record\": null", json);
    }

    [Fact]
    public void AnotherSchemaVersionIsRefused()
    {
        string json = Sample().ToJson().Replace(
            "\"schemaVersion\": " + NativeShaderManifest.CurrentSchemaVersion,
            "\"schemaVersion\": " + (NativeShaderManifest.CurrentSchemaVersion + 1));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => NativeShaderManifest.Parse(json));
        Assert.Contains("schema version " + (NativeShaderManifest.CurrentSchemaVersion + 1), error.Message);
    }

    [Fact]
    public void MalformedOrIncompleteManifestsAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => NativeShaderManifest.Parse("{ not json"));
        Assert.Throws<InvalidDataException>(() => NativeShaderManifest.Parse("{\"schemaVersion\": 1}"));
        Assert.Throws<InvalidDataException>(() => NativeShaderManifest.Parse(Sample().ToJson().Replace("\"writtenOutputs\"", "\"writtenOutputz\"")));
    }

    [Fact]
    public void TheVariantKeyIsTheSortedAxisValueList()
    {
        var values = new Dictionary<string, int> { ["TAAMOTION"] = 1, ["GBUFFER"] = 0, ["USEOIT"] = 1 };

        Assert.Equal("GBUFFER=0,TAAMOTION=1", NativeShaderManifest.VariantKey(new[] { "TAAMOTION", "GBUFFER" }, values));
        Assert.Equal("", NativeShaderManifest.VariantKey(new string[0], values));
        Assert.Equal("GREEDYMESH=0", NativeShaderManifest.VariantKey(new[] { "GREEDYMESH" }, values));
    }
}
}
