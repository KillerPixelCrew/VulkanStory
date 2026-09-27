using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class BinaryDeltaPackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "optimum-delta-tests-" + Guid.NewGuid().ToString("N"));
    private string Original => Path.Combine(root, "original");
    private string Pack => Path.Combine(root, "pack");
    private string Output => Path.Combine(root, "output");
    private BinaryDeltaManifest manifest;

    public BinaryDeltaPackTests()
    {
        var files = new List<BinaryDeltaFile>();
        foreach (string relative in new[] { "VintagestoryLib.dll", "VintagestoryAPI.dll", "Mods/VSEssentials.dll", "Mods/VSSurvivalMod.dll" })
        {
            string input = Path.Combine(Original, relative), delta = Path.Combine(Pack, relative + ".vcdiff");
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            Directory.CreateDirectory(Path.GetDirectoryName(delta)!);
            File.WriteAllText(input, "original");
            File.WriteAllText(delta, "patched");
            files.Add(new(relative, relative + ".vcdiff", 8, Hash(input), 7, Hash(delta), 7, Hash(delta)));
        }
        manifest = new(BinaryDeltaPack.Format, "1.22.7", "0.3.17", "linux-x64", files);
        WriteManifest();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private void WriteManifest() => File.WriteAllText(Path.Combine(Pack, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    private Task<BinaryDeltaManifest> Apply(IBinaryDeltaDecoder decoder, CancellationToken token = default) =>
        new BinaryDeltaPack(decoder).ApplyAsync(Original, Pack, Output, "1.22.7", "0.3.17", "linux-x64", token);
    private void AssertNoOutput()
    {
        Assert.False(Directory.Exists(Output));
        Assert.Empty(Directory.GetDirectories(root, ".optimum-delta-*"));
    }

    private sealed class Decoder(Action<int, string, string, string>? action = null) : IBinaryDeltaDecoder
    {
        public int Calls { get; private set; }
        public Task DecodeAsync(string original, string delta, string output, CancellationToken token)
        {
            Calls++;
            if (action is not null) action(Calls, original, delta, output);
            else File.Copy(delta, output);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PublishesOnlyVerifiedCopiesAndRetainsOriginals()
    {
        var decoder = new Decoder((_, input, delta, output) =>
        {
            Assert.False(Directory.Exists(Output));
            Assert.DoesNotContain(Original, input);
            File.Copy(delta, output);
        });
        await Apply(decoder);
        Assert.Equal(4, decoder.Calls);
        Assert.True(File.Exists(Path.Combine(Output, "delta-manifest.json")));
        foreach (var file in manifest.Files)
        {
            Assert.Equal("original", File.ReadAllText(Path.Combine(Original, file.Path)));
            Assert.Equal("patched", File.ReadAllText(Path.Combine(Output, file.Path)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsWrongInputOrDeltaBeforeDecoding(bool input)
    {
        File.WriteAllText(Path.Combine(input ? Original : Pack, input ? manifest.Files[3].Path : manifest.Files[3].Delta), "invalid");
        var decoder = new Decoder();
        await Assert.ThrowsAsync<InvalidDataException>(() => Apply(decoder));
        Assert.Equal(0, decoder.Calls);
        AssertNoOutput();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("rid")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    [InlineData("size")]
    [InlineData("null")]
    public async Task RejectsInvalidManifest(string kind)
    {
        var files = manifest.Files.ToArray();
        manifest = kind switch
        {
            "version" => manifest with { GameVersion = "1.22.6" },
            "rid" => manifest with { Rid = "win-x64" },
            "null" => manifest with { Files = null! },
            _ => manifest,
        };
        if (kind == "duplicate") files[0] = files[1];
        if (kind == "traversal") files[0] = files[0] with { Delta = "../outside" };
        if (kind == "size") files[0] = files[0] with { OutputSize = long.MaxValue };
        if (kind is "duplicate" or "traversal" or "size") manifest = manifest with { Files = files };
        WriteManifest();
        var decoder = new Decoder();
        await Assert.ThrowsAsync<InvalidDataException>(() => Apply(decoder));
        Assert.Equal(0, decoder.Calls);
        AssertNoOutput();
    }

    [Fact]
    public async Task LastFileFailureDiscardsEntireStagingDirectory()
    {
        var decoder = new Decoder((call, _, delta, output) =>
        {
            if (call == 4) File.WriteAllText(output, "corrupt");
            else File.Copy(delta, output);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Apply(decoder));
        AssertNoOutput();
    }

    [Fact]
    public async Task CancellationDuringDecodingDiscardsStaging()
    {
        using var source = new CancellationTokenSource();
        var decoder = new Decoder((_, _, delta, output) => { File.Copy(delta, output); source.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Apply(decoder, source.Token));
        AssertNoOutput();
    }

    [Fact]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(Output, "keep"), "keep");
        await Assert.ThrowsAsync<IOException>(() => Apply(new Decoder()));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Output, "keep")));
    }

    [Fact]
    public async Task RejectsOutputInsideOriginal()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new BinaryDeltaPack(new Decoder()).ApplyAsync(
            Original, Pack, Path.Combine(Original, "cache"), "1.22.7", "0.3.17", "linux-x64"));
        Assert.False(Directory.Exists(Path.Combine(Original, "cache")));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string CreatePayload()
    {
        File.WriteAllText(Path.Combine(Original, "Vintagestory.dll"), "original client");
        Directory.CreateDirectory(Path.Combine(Original, "assets"));
        File.WriteAllText(Path.Combine(Original, "assets", "fixture.txt"), "original asset");
        File.WriteAllText(Path.Combine(Original, "datapath.cfg"), "must not inherit");
        string payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "Optimum"), "launcher apphost fixture");
        File.WriteAllText(Path.Combine(payload, "Optimum.dll"), "launcher fixture");
        return payload;
    }

    private async Task<(string Payload, string Data)> CreateInstalledRuntime()
    {
        string payload = CreatePayload();
        await new DeltaRuntimeInstaller(new Decoder()).InstallAsync(Original, Pack, payload, Output,
            "1.22.7", "0.3.17", "linux-x64");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var installed = new InstallManifest
        {
            OptimumVersion = "0.3.17", InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = Output, DataPath = data,
            Launcher = Path.Combine(Output, "Optimum"),
            Shortcuts = [Path.Combine(root, "menu-entry")],
            Entries = Directory.EnumerateFileSystemEntries(Output)
                .Select(Path.GetFileName).Where(name => name is not null and not ".optimum")
                .Select(name => name!).ToArray(),
        };
        File.WriteAllText(Path.Combine(Output, InstallManifest.RelativePath), installed.Serialize());
        return (payload, data);
    }

    [Fact]
    public async Task RepairRebuildsDamagedRuntimeAndRetainsPreviousCopy()
    {
        (string payload, string data) = await CreateInstalledRuntime();
        File.WriteAllText(Path.Combine(Output, "VintagestoryLib.dll"), "damaged");
        Assert.True(DeltaRuntimeGuard.IsSeparateRuntime(Output));

        string backup = await new DeltaRuntimeRepairer(new Decoder()).RepairAsync(Output, Pack, payload,
            "1.22.7", "0.3.17", "linux-x64");

        Assert.Equal("patched", File.ReadAllText(Path.Combine(Output, "VintagestoryLib.dll")));
        Assert.Equal("damaged", File.ReadAllText(Path.Combine(backup, "VintagestoryLib.dll")));
        Assert.Equal(data, File.ReadAllText(Path.Combine(Output, "datapath.cfg")));
        InstallManifest repaired = InstallManifest.Deserialize(File.ReadAllText(
            Path.Combine(Output, InstallManifest.RelativePath)))!;
        Assert.Equal(new[] { Path.Combine(root, "menu-entry") }, repaired.Shortcuts);
        Assert.True(DeltaRuntimeGuard.IsSeparateRuntime(Output));
        await DeltaRuntimeInstaller.VerifyAsync(Output, "0.3.17", "linux-x64");
        Assert.Equal("original", File.ReadAllText(Path.Combine(Original, "VintagestoryLib.dll")));
    }

    [Fact]
    public async Task FailedRepairLeavesInstalledRuntimeUntouched()
    {
        (string payload, _) = await CreateInstalledRuntime();
        File.WriteAllText(Path.Combine(Output, "VintagestoryLib.dll"), "damaged");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DeltaRuntimeRepairer(new Decoder()).RepairAsync(Output, Pack, payload,
                "1.22.7", "0.3.17", "linux-x64",
                validateRuntime: (_, _) => throw new InvalidDataException("Validation failed")));
        Assert.Equal("damaged", File.ReadAllText(Path.Combine(Output, "VintagestoryLib.dll")));
        Assert.Empty(Directory.GetDirectories(root, ".output-repair-*"));
        Assert.Empty(Directory.GetDirectories(root, "output.optimum-backup-*"));
    }

    [Fact]
    public async Task RepairRejectsAnotherReleaseWithoutMovingRuntime()
    {
        (string payload, _) = await CreateInstalledRuntime();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DeltaRuntimeRepairer(new Decoder()).RepairAsync(Output, Pack, payload,
                "1.22.8", "0.3.17", "linux-x64"));
        Assert.True(DeltaRuntimeGuard.IsSeparateRuntime(Output));
        Assert.Empty(Directory.GetDirectories(root, "output.optimum-backup-*"));
    }

    [Fact]
    public async Task RuntimeCopiesAssetsAndVerifiesOwnPatchedFiles()
    {
        string payload = CreatePayload();
        await new DeltaRuntimeInstaller(new Decoder()).InstallAsync(Original, Pack, payload, Output,
            "1.22.7", "0.3.17", "linux-x64");
        await DeltaRuntimeInstaller.VerifyAsync(Output, "0.3.17", "linux-x64");
        Assert.Equal("original asset", File.ReadAllText(Path.Combine(Output, "assets", "fixture.txt")));
        Assert.False(File.Exists(Path.Combine(Output, "datapath.cfg")));
        Assert.False(Directory.Exists(Path.Combine(Original, ".optimum")));
        foreach (var file in manifest.Files)
        {
            Assert.Equal("original", File.ReadAllText(Path.Combine(Original, file.Path)));
            Assert.Equal("patched", File.ReadAllText(Path.Combine(Output, file.Path)));
        }
        File.WriteAllText(Path.Combine(Original, manifest.Files[0].Path), "upstream update");
        await Assert.ThrowsAsync<InvalidDataException>(() => DeltaRuntimeInstaller.VerifyAsync(Output, "0.3.17", "linux-x64"));
    }

    [Fact]
    public async Task RuntimeRejectsPayloadOverridingDeltaTargetsAndCleansUp()
    {
        string payload = CreatePayload();
        File.WriteAllText(Path.Combine(payload, "VintagestoryAPI.dll"), "must reject");
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeltaRuntimeInstaller(new Decoder()).InstallAsync(
            Original, Pack, payload, Output, "1.22.7", "0.3.17", "linux-x64"));
        Assert.False(Directory.Exists(Output));
        Assert.Empty(Directory.GetDirectories(root, ".optimum-runtime-*"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Original, "VintagestoryAPI.dll")));
    }

    [Fact]
    public async Task RuntimeRejectsCorruptedInstalledAssembly()
    {
        await new DeltaRuntimeInstaller(new Decoder()).InstallAsync(Original, Pack, CreatePayload(), Output,
            "1.22.7", "0.3.17", "linux-x64");
        File.WriteAllText(Path.Combine(Output, "Mods", "VSEssentials.dll"), "damaged");
        await Assert.ThrowsAsync<InvalidDataException>(() => DeltaRuntimeInstaller.VerifyAsync(Output, "0.3.17", "linux-x64"));
    }

    [Fact]
    public async Task RuntimeValidationFailureDoesNotActivateInstall()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeltaRuntimeInstaller(new Decoder()).InstallAsync(
            Original, Pack, CreatePayload(), Output, "1.22.7", "0.3.17", "linux-x64",
            validateRuntime: (_, _) => throw new InvalidDataException("Startup validation failed")));
        Assert.False(Directory.Exists(Output));
        Assert.Empty(Directory.GetDirectories(root, ".optimum-runtime-*"));
    }

    [Fact]
    public async Task AddedShaderUsesExactOriginalShaderAsItsSource()
    {
        string reference = Path.Combine(Original, "assets", "game", "shaders", "standard.vsh");
        string delta = Path.Combine(Pack, "assets", "game", "shaders", "taa-resolve.vsh.vcdiff");
        Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
        Directory.CreateDirectory(Path.GetDirectoryName(delta)!);
        File.WriteAllText(reference, "official shader");
        File.WriteAllText(delta, "patched shader");
        string target = "assets/game/shaders/taa-resolve.vsh";
        manifest = manifest with { Files = [.. manifest.Files, new BinaryDeltaFile(
            target, target + ".vcdiff", new FileInfo(reference).Length, Hash(reference),
            new FileInfo(delta).Length, Hash(delta), new FileInfo(delta).Length, Hash(delta),
            "assets/game/shaders/standard.vsh")] };
        WriteManifest();
        await new DeltaRuntimeInstaller(new Decoder()).InstallAsync(Original, Pack, CreatePayload(), Output,
            "1.22.7", "0.3.17", "linux-x64");
        Assert.Equal("official shader", File.ReadAllText(reference));
        Assert.Equal("patched shader", File.ReadAllText(Path.Combine(Output, target)));
        await DeltaRuntimeInstaller.VerifyAsync(Output, "0.3.17", "linux-x64");
        File.AppendAllText(reference, "changed upstream");
        await Assert.ThrowsAsync<InvalidDataException>(() => DeltaRuntimeInstaller.VerifyAsync(Output, "0.3.17", "linux-x64"));
    }

    [Fact]
    public async Task RejectsShaderSourceTraversalBeforeInvokingDecoder()
    {
        string target = "assets/game/shaders/taa-resolve.vsh";
        manifest = manifest with { Files = [.. manifest.Files, new BinaryDeltaFile(target,
            target + ".vcdiff", 8, new string('0', 64), 7, new string('0', 64),
            7, new string('0', 64), "assets/game/shaders/../private.vsh")] };
        WriteManifest();
        var decoder = new Decoder();
        await Assert.ThrowsAsync<InvalidDataException>(() => Apply(decoder));
        Assert.Equal(0, decoder.Calls);
        AssertNoOutput();
    }
}
