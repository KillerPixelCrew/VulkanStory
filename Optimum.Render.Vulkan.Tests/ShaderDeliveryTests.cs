using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class ShaderDeliveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "optimum-shader-delivery-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "src");
    private string Output => Path.Combine(root, "out");
    private string Package => Path.Combine(Output, NativeShaderManifest.DirectoryName);

    public ShaderDeliveryTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "include"));
        File.Copy(Path.Combine(ShaderCorpus.RepositoryRoot, SetConvention.IncludePath), Path.Combine(Source, "include", "bindings.glsl"));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private const string Program = """
        #version 450
        #extension GL_EXT_scalar_block_layout : require
        #include "bindings.glsl"
        layout(push_constant, scalar) uniform Draw { vec4 tint; } draw;
        layout(set = OPTIMUM_SET_STORAGE, binding = OPTIMUM_BINDING_PROGRAM_RECORD, scalar)
            uniform Record { float gain; } record;
        #if defined(OPTIMUM_VERTEX)
        layout(location = 0) out vec2 uv;
        void main() { uv = draw.tint.xy; gl_Position = vec4(uv, 0, 1); }
        #elif defined(OPTIMUM_FRAGMENT)
        #include "color.glsl"
        layout(location = 0) in vec2 uv;
        layout(location = 0) out vec4 color;
        #if TAAMOTION == 1
        layout(location = 1) out vec4 motion;
        #endif
        void main() {
            color = fixtureColor() * draw.tint * record.gain + vec4(uv, 0, 0);
        #if TAAMOTION == 1
            motion = vec4(uv, 0, 1);
        #endif
        }
        #endif
        """;

    private void Fixtures()
    {
        File.WriteAllText(Path.Combine(Source, "fixture.glsl"), Program);
        File.WriteAllText(Path.Combine(Source, "include", "color.glsl"), "vec4 fixtureColor() { return vec4(0.2, 0.3, 0.4, 1); }");
    }

    private static NativeShaderBuildResult Build(ShaderCompiler compiler, string source)
    {
        var result = new NativeShaderBuilder(compiler).Build(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result;
    }

    [Fact]
    public void SingleSourceStagesProduceVariantsWithTheDeclaredAbi()
    {
        Fixtures();
        using var compiler = new ShaderCompiler();
        var build = Build(compiler, Source);
        var program = Assert.Single(build.Manifest.Programs);
        Assert.Equal(new[] { "TAAMOTION=0", "TAAMOTION=1" }, program.Variants.Select(v => v.Key));
        foreach (var variant in program.Variants)
        {
            Assert.Equal(16, variant.Push!.Size);
            Assert.Equal(4, variant.Record!.Size);
            Assert.Equal(new[] { 0 }, variant.Push.Members.Select(m => m.Offset));
            Assert.Equal(variant.Key.EndsWith("1") ? 2 : 1, variant.FragmentOutputs.Count);
            Assert.Equal(new[] { "vertex", "fragment" }.Order(), variant.Stages.Select(s => s.Stage).Order());
            foreach (var stage in variant.Stages)
            {
                Assert.Equal("fixture.glsl", stage.Source);
                Assert.Equal(stage.Sha256, Convert.ToHexStringLower(SHA256.HashData(build.Files[stage.Spirv])));
                Assert.Equal(0x07230203u, BitConverter.ToUInt32(build.Files[stage.Spirv]));
            }
        }
    }

    [Fact]
    public void IncludeEditsInvalidateOutputsAndRemovedProgramsLeaveNoStaleBinaries()
    {
        Fixtures();
        using var compiler = new ShaderCompiler();
        var initial = Build(compiler, Source);
        NativeShaderBuilder.Write(initial, Output);
        var times = Directory.GetFiles(Package).ToDictionary(Path.GetFileName, File.GetLastWriteTimeUtc);
        var unchanged = Build(compiler, Source);
        NativeShaderBuilder.Write(unchanged, Output);
        Assert.Empty(NativeShaderBuilder.Compare(unchanged, Output));
        Assert.All(Directory.GetFiles(Package), path => Assert.Equal(times[Path.GetFileName(path)], File.GetLastWriteTimeUtc(path)));

        File.WriteAllText(Path.Combine(Source, "include", "color.glsl"), "vec4 fixtureColor() { return vec4(0.8, 0.7, 0.6, 1); }");
        var changed = Build(compiler, Source);
        Assert.NotEmpty(NativeShaderBuilder.Compare(changed, Output));
        foreach (var variant in changed.Manifest.Programs[0].Variants)
        {
            var fragment = variant.Stages.Single(s => s.Stage == "fragment");
            Assert.False(initial.Files[fragment.Spirv].SequenceEqual(changed.Files[fragment.Spirv]));
        }
        NativeShaderBuilder.Write(changed, Output);
        File.Delete(Path.Combine(Source, "fixture.glsl"));
        NativeShaderBuilder.Write(Build(compiler, Source), Output);
        Assert.Empty(Directory.GetFiles(Package, "*.spv"));
        Assert.Empty(NativeShaderManifest.Load(Path.Combine(Package, NativeShaderManifest.FileName)).Programs);
    }

    [Fact]
    public void ShippedProgramsCompilePackageAndLoadEveryDeclaredStage()
    {
        using var compiler = new ShaderCompiler();
        string source = Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk");
        var build = Build(compiler, source);
        string[] names = Directory.GetFiles(source, "*.glsl").Select(Path.GetFileNameWithoutExtension).Order().ToArray()!;
        Assert.NotEmpty(names);
        Assert.Equal(names, build.Manifest.Programs.Select(p => p.Name).Order());
        NativeShaderBuilder.Write(build, Output);
        var library = NativeShaderLibrary.Load(Package, compiler.Identity, out string reason);
        Assert.True(library != null, reason);
        foreach (var program in build.Manifest.Programs)
            foreach (var variant in program.Variants)
                foreach (var stage in variant.Stages)
                {
                    Assert.True(library!.TryGetSpirv(stage, out byte[] bytes, out string error), error);
                    Assert.Equal(build.Files[stage.Spirv], bytes);
                    SpirvReflection.Reflect(bytes);
                }
        Assert.Empty(NativeShaderBuilder.Compare(build, Output));
    }

    [Fact]
    public void CorruptPackagedModulesAreRejectedBeforeLinking()
    {
        Fixtures();
        using var compiler = new ShaderCompiler();
        var build = Build(compiler, Source);
        NativeShaderBuilder.Write(build, Output);
        var stage = build.Manifest.Programs[0].Variants[0].Stages[0];
        File.WriteAllBytes(Path.Combine(Package, stage.Spirv), new byte[] { 1, 2, 3 });
        var library = NativeShaderLibrary.Load(Package, compiler.Identity, out _);
        Assert.NotNull(library);
        Assert.False(library!.TryGetSpirv(stage, out _, out string reason));
        Assert.NotEmpty(reason);
        Assert.NotEmpty(NativeShaderBuilder.Compare(build, Output));
    }

    [Fact]
    public void InvalidSourcesFailWithoutReplacingTheLastGoodPackage()
    {
        Fixtures();
        using var compiler = new ShaderCompiler();
        var good = Build(compiler, Source);
        NativeShaderBuilder.Write(good, Output);
        File.WriteAllText(Path.Combine(Source, "fixture.glsl"), Program.Replace("void main()", "void broken main()"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.NotEqual(0, NativeShaderTool.Run(new[] { "--build", Source, Output }, output, error));
        Assert.NotEmpty(error.ToString());
        Assert.Empty(NativeShaderBuilder.Compare(good, Output));
    }

    [Fact]
    public void BinaryCacheReusesCompiledModulesAndRejectsCorruption()
    {
        var cache = new ShaderBinaryCache(Path.Combine(root, "cache"));
        using var compiler = new ShaderCompiler { BinaryCache = cache };
        const string source = "#version 450\nvoid main() { gl_Position = vec4(0, 0, 0, 1); }";
        var first = compiler.Compile(source, "fixture.vert", EnumShaderType.VertexShader);
        Assert.True(first.Success, first.Error);
        var second = compiler.Compile(source, "fixture.vert", EnumShaderType.VertexShader);
        Assert.True(second.Success, second.Error);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(first.Spirv, second.Spirv);
        byte[] stored = ShaderBinaryCache.Wrap(first.Spirv);
        stored[^1] ^= 1;
        Assert.Null(ShaderBinaryCache.Unwrap(stored));
        string key = ShaderBinaryCache.KeyFor(source, EnumShaderType.VertexShader, compiler.Identity);
        Assert.NotEqual(key, ShaderBinaryCache.KeyFor(source, EnumShaderType.FragmentShader, compiler.Identity));
        Assert.NotEqual(key, ShaderBinaryCache.KeyFor(source, EnumShaderType.VertexShader, "different-compiler"));
    }

    [Fact]
    public void DriverCacheRequiresMatchingHardwareDriverAndIntactPayload()
    {
        var id = new PipelineCacheIdentity(0x8086, 0x4688, 1017088, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var blob = new byte[64];
        BitConverter.TryWriteBytes(blob.AsSpan(0), 32u);
        BitConverter.TryWriteBytes(blob.AsSpan(4), 1u);
        BitConverter.TryWriteBytes(blob.AsSpan(8), id.VendorId);
        BitConverter.TryWriteBytes(blob.AsSpan(12), id.DeviceId);
        id.Uuid.CopyTo(blob, 16);
        string path = PipelineCacheFile.PathFor(root, id);
        Assert.True(PipelineCacheFile.Save(path, blob, id));
        Assert.Equal(blob, PipelineCacheFile.Load(path, id));
        foreach (var other in new[]
        {
            new PipelineCacheIdentity(0x10de, id.DeviceId, id.DriverVersion, id.Uuid),
            new PipelineCacheIdentity(id.VendorId, id.DeviceId + 1, id.DriverVersion, id.Uuid),
            new PipelineCacheIdentity(id.VendorId, id.DeviceId, id.DriverVersion + 1, id.Uuid),
            new PipelineCacheIdentity(id.VendorId, id.DeviceId, id.DriverVersion, new byte[16]),
        }) Assert.Null(PipelineCacheFile.Load(path, other));
        byte[] stored = File.ReadAllBytes(path);
        File.WriteAllBytes(path, stored[..^1]);
        Assert.Null(PipelineCacheFile.Load(path, id));
        stored[^1] ^= 1;
        File.WriteAllBytes(path, stored);
        Assert.Null(PipelineCacheFile.Load(path, id));
    }

    [Fact]
    public void FailedAtomicCacheReplacementPreservesTheLastGoodFile()
    {
        string path = Path.Combine(root, "blocked-cache.bin");
        File.WriteAllBytes(path, new byte[] { 9, 8, 7 });
        Assert.False(CacheFileWriter.WriteAtomically(path, new byte[] { 1 },
            (_, _) => throw new IOException("sharing violation"), _ => { }));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        Assert.True(CacheFileWriter.WriteAtomically(path, new byte[] { 2 }));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void PipelineWarmupRecordsStaySeparatedBySettingsAndProgram()
    {
        var log = new PipelineKeyLog();
        PipelineKeyLogEntry Entry(ulong settings, ulong shader) => new()
        {
            SettingsHash = settings, ProgramHash = new UInt128(0, shader),
            Bindings = Array.Empty<VertexBinding>(), Attributes = Array.Empty<VertexAttribute>(),
            DepthFormat = Silk.NET.Vulkan.Format.Undefined,
            PolygonMode = Silk.NET.Vulkan.PolygonMode.Fill, Topology = Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
            ColorFormats = new[] { Silk.NET.Vulkan.Format.R8G8B8A8Unorm },
            Blend = new[] { AttachmentBlend.Default },
        };
        log.Record(Entry(1, 20), 100);
        log.Record(Entry(2, 20), 200);
        log.Record(Entry(1, 21), 300);
        log.Record(Entry(1, 20), 400);
        string path = Path.Combine(root, "warmup.keys");
        Assert.True(log.Save(path));
        var loaded = PipelineKeyLog.Load(path);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(400, Assert.Single(loaded.Matching(1, new UInt128(0, 20))).LastSeenUnixMs);
        Assert.Equal(200, Assert.Single(loaded.Matching(2, new UInt128(0, 20))).LastSeenUnixMs);
        Assert.Empty(loaded.Matching(2, new UInt128(0, 21)));
        File.WriteAllText(path, "invalid partial write");
        Assert.Equal(0, PipelineKeyLog.Load(path).Count);
    }
}
