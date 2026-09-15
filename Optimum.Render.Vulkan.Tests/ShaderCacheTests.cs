using System;
using System.IO;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The on-disk caches: compiled SPIR-V and the driver's pipeline cache.
///
/// Both files are read back into the driver on the next launch, so the property that
/// matters is that anything not written whole, by this format, for this compiler or
/// this GPU and driver, reads as a miss - never as data. The failure shapes pinned
/// here are the ones seen in the wild (docs/research/vulkan-caching.md §1).
/// </summary>
public sealed class ShaderCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimum-cache-tests-" + Guid.NewGuid().ToString("N"));

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

    private const string VertexSource = """
        #version 450
        void main() { gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }
        """;

    /// <summary>A minimal SPIR-V header: magic, version, generator, bound, schema.</summary>
    private static byte[] FakeSpirv(uint bound = 7)
    {
        var words = new uint[] { 0x07230203, 0x00010500, 0, bound, 0, 0x00020011, 0x00000001 };
        return words.SelectMany(BitConverter.GetBytes).ToArray();
    }

    // ------------------------------------------------------------ SPIR-V cache

    [Fact]
    public void AStoredModuleReadsBackIdentically()
    {
        var cache = new ShaderBinaryCache(_root);
        string key = ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-a");
        byte[] spirv = FakeSpirv();

        cache.Put(key, spirv);

        Assert.Equal(spirv, cache.TryGet(key));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void TheKeyChangesWithCompilerStageAndSource()
    {
        string baseline = ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-a");

        Assert.NotEqual(baseline, ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-b"));
        Assert.NotEqual(baseline, ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.FragmentShader, "compiler-a"));
        Assert.NotEqual(baseline, ShaderBinaryCache.KeyFor(VertexSource + " ", EnumShaderType.VertexShader, "compiler-a"));
        Assert.Equal(baseline, ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-a"));
    }

    [Fact]
    public void DamagedModuleFilesAreMisses()
    {
        byte[] file = ShaderBinaryCache.Wrap(FakeSpirv());
        Assert.NotNull(ShaderBinaryCache.Unwrap(file));

        // Truncated by an interrupted write.
        Assert.Null(ShaderBinaryCache.Unwrap(file[..^4]));
        // Empty.
        Assert.Null(ShaderBinaryCache.Unwrap(Array.Empty<byte>()));
        // A zero-filled block inside the payload.
        byte[] zeroed = (byte[])file.Clone();
        Array.Clear(zeroed, ShaderBinaryCache.HeaderSize + 8, 8);
        Assert.Null(ShaderBinaryCache.Unwrap(zeroed));
        // A file from another format version.
        byte[] otherVersion = (byte[])file.Clone();
        BitConverter.TryWriteBytes(otherVersion.AsSpan(4), ShaderBinaryCache.FormatVersion + 1);
        Assert.Null(ShaderBinaryCache.Unwrap(otherVersion));
        // Raw SPIR-V dropped in without the header.
        Assert.Null(ShaderBinaryCache.Unwrap(FakeSpirv()));
    }

    [Fact]
    public void ADamagedFileOnDiskIsAMissAndIsReplacedByTheNextPut()
    {
        var cache = new ShaderBinaryCache(_root);
        string key = ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-a");
        cache.Put(key, FakeSpirv());
        string path = Directory.GetFiles(_root, "*.spv", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(path, new byte[File.ReadAllBytes(path).Length]);

        Assert.Null(cache.TryGet(key));

        cache.Put(key, FakeSpirv(bound: 9));
        Assert.Equal(FakeSpirv(bound: 9), cache.TryGet(key));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SomethingThatIsNotSpirvIsNeverStored()
    {
        var cache = new ShaderBinaryCache(_root);
        cache.Put("aa00", new byte[] { 1, 2, 3, 4 });

        Assert.False(Directory.Exists(_root));
    }

    /// <summary>The compiler's cache hook: the second compile of a source is read, not compiled, and is the same module.</summary>
    [Fact]
    public void TheCompilerServesARepeatedSourceFromTheCache()
    {
        using var compiler = new ShaderCompiler { BinaryCache = new ShaderBinaryCache(_root) };

        ShaderCompileResult first = compiler.Compile(VertexSource, "cached.vsh", EnumShaderType.VertexShader);
        ShaderCompileResult second = compiler.Compile(VertexSource, "renamed.vsh", EnumShaderType.VertexShader);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal(first.Spirv, second.Spirv);
        Assert.Equal(1, compiler.BinaryCache.Misses);
        Assert.Equal(1, compiler.BinaryCache.Hits);
    }

    [Fact]
    public void TheCompilerIdentityNamesTheOptionsAndTheShadercBuild()
    {
        using var compiler = new ShaderCompiler();

        Assert.StartsWith(ShaderCompiler.OptionsIdentity + ";", compiler.Identity);
        Assert.Matches("(shaderc-sha256:[0-9a-f]{64}|silk-shaderc-.+)$", compiler.Identity);
    }

    [Fact]
    public void AFailedCompileIsNotCached()
    {
        using var compiler = new ShaderCompiler { BinaryCache = new ShaderBinaryCache(_root) };

        Assert.False(compiler.Compile("#version 450\nvoid main() { oops }", "broken.vsh", EnumShaderType.VertexShader).Success);
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    // ------------------------------------------------------------ pipeline cache file

    private static PipelineCacheIdentity Identity(uint vendor = 0x10de, uint device = 0x2803, uint driver = 0x8c4a4000,
        byte uuidSeed = 1) =>
        new(vendor, device, driver, Enumerable.Range(uuidSeed, 16).Select(i => (byte)i).ToArray());

    /// <summary>A blob whose VkPipelineCacheHeaderVersionOne names <paramref name="identity" />, plus driver data.</summary>
    private static byte[] DriverBlob(PipelineCacheIdentity identity, int extra = 64)
    {
        var blob = new byte[32 + extra];
        BitConverter.TryWriteBytes(blob.AsSpan(0), 32u);
        BitConverter.TryWriteBytes(blob.AsSpan(4), 1u);
        BitConverter.TryWriteBytes(blob.AsSpan(8), identity.VendorId);
        BitConverter.TryWriteBytes(blob.AsSpan(12), identity.DeviceId);
        identity.Uuid.CopyTo(blob, 16);
        for (int i = 32; i < blob.Length; i++) blob[i] = (byte)(i * 7);
        return blob;
    }

    [Fact]
    public void APipelineCacheReadsBackForTheDeviceThatWroteIt()
    {
        PipelineCacheIdentity identity = Identity();
        string path = PipelineCacheFile.PathFor(_root, identity);
        byte[] blob = DriverBlob(identity);

        Assert.True(PipelineCacheFile.Save(path, blob, identity));

        Assert.Equal(blob, PipelineCacheFile.Load(path, identity));
    }

    [Fact]
    public void APipelineCacheFromAnotherGpuOrDriverIsNotLoaded()
    {
        PipelineCacheIdentity identity = Identity();
        byte[] file = PipelineCacheFile.Wrap(DriverBlob(identity), identity);

        Assert.Null(PipelineCacheFile.Unwrap(file, Identity(vendor: 0x1002)));
        Assert.Null(PipelineCacheFile.Unwrap(file, Identity(device: 0x2804)));
        // A driver update that kept its UUID: the case drivers get wrong.
        Assert.Null(PipelineCacheFile.Unwrap(file, Identity(driver: 0x8c4b0000)));
        Assert.Null(PipelineCacheFile.Unwrap(file, Identity(uuidSeed: 2)));
    }

    [Fact]
    public void DamagedPipelineCacheFilesAreNotLoaded()
    {
        PipelineCacheIdentity identity = Identity();
        byte[] file = PipelineCacheFile.Wrap(DriverBlob(identity), identity);
        Assert.NotNull(PipelineCacheFile.Unwrap(file, identity));

        Assert.Null(PipelineCacheFile.Unwrap(file[..^1], identity));
        Assert.Null(PipelineCacheFile.Unwrap(file[..PipelineCacheFile.HeaderSize], identity));
        Assert.Null(PipelineCacheFile.Unwrap(Array.Empty<byte>(), identity));
        Assert.Null(PipelineCacheFile.Unwrap(new byte[file.Length], identity));
        byte[] flipped = (byte[])file.Clone();
        flipped[^1] ^= 0xff;
        Assert.Null(PipelineCacheFile.Unwrap(flipped, identity));
    }

    [Fact]
    public void ABlobWhoseOwnVulkanHeaderNamesAnotherDeviceIsNeitherSavedNorLoaded()
    {
        PipelineCacheIdentity identity = Identity();
        byte[] foreign = DriverBlob(Identity(device: 0x1234));
        string path = PipelineCacheFile.PathFor(_root, identity);

        Assert.False(PipelineCacheFile.Save(path, foreign, identity));
        Assert.Null(PipelineCacheFile.Unwrap(PipelineCacheFile.Wrap(foreign, identity), identity));
        Assert.False(PipelineCacheFile.Save(path, Array.Empty<byte>(), identity));
    }

    [Fact]
    public void EachGpuHasItsOwnPipelineCacheFile()
    {
        Assert.NotEqual(
            PipelineCacheFile.PathFor(_root, Identity()),
            PipelineCacheFile.PathFor(_root, Identity(device: 0x2804)));
    }

    // ------------------------------------------------------------ location

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("C:/cache", null, "C:/cache")]
    [InlineData("C:/cache", "", "C:/cache")]
    [InlineData("C:/cache", "D:/elsewhere", "D:/elsewhere")]
    [InlineData(null, "D:/elsewhere", "D:/elsewhere")]
    [InlineData("C:/cache", "0", null)]
    [InlineData("C:/cache", "off", null)]
    public void TheEnvironmentOverridesOrDisablesTheCacheDirectory(string? configured, string? environment, string? expected)
    {
        Assert.Equal(expected, VulkanDevice.ResolveShaderCacheRoot(configured, environment));
    }
}
