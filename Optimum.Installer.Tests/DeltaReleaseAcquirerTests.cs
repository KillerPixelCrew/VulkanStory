using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optimum.Installer.Services;
using Xunit;

namespace Optimum.Installer.Tests;

public sealed class DeltaReleaseAcquirerTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task DownloadsExactAssetAndExtractsOnlyAfterDigestMatches()
    {
        byte[] archive = Archive();
        var handler = Server(archive);
        using var client = new HttpClient(handler);
        string cache = Temp();
        try
        {
            string result = await new DeltaReleaseAcquirer(client, cache).AcquireAsync(
                "1.22.7", "0.3.17", "win-x64", CancellationToken.None);
            Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(result, "runtime-payload", "Optimum.dll")));
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("api.github.com", handler.Requests[0].Host);
            Assert.Equal("github.com", handler.Requests[1].Host);

            // The downloaded archive is content-addressed. A second acquisition verifies
            // its cached bytes and only asks GitHub for fresh release metadata.
            string second = await new DeltaReleaseAcquirer(client, cache).AcquireAsync(
                "1.22.7", "0.3.17", "win-x64", CancellationToken.None);
            Assert.True(Directory.Exists(second));
            Assert.Equal(3, handler.Requests.Count);

            string cachedArchive = Assert.Single(Directory.EnumerateFiles(cache, "*.zip"));
            await File.WriteAllTextAsync(cachedArchive, "damaged cache");
            string third = await new DeltaReleaseAcquirer(client, cache).AcquireAsync(
                "1.22.7", "0.3.17", "win-x64", CancellationToken.None);
            Assert.True(Directory.Exists(third));
            Assert.Equal(5, handler.Requests.Count);
        }
        finally { Directory.Delete(cache, recursive: true); }
    }

    [Fact]
    public async Task RejectsDigestMismatchBeforeExtracting()
    {
        byte[] archive = Archive();
        var handler = Server(archive, new string('0', 64));
        using var client = new HttpClient(handler);
        string cache = Temp();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DeltaReleaseAcquirer(client, cache).AcquireAsync(
                    "1.22.7", "0.3.17", "win-x64", CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
        }
        finally { Directory.Delete(cache, recursive: true); }
    }

    [Fact]
    public async Task RejectsTraversalEvenWhenTheArchiveDigestMatches()
    {
        byte[] archive = Archive("runtime-payload/../../escaped.txt");
        var handler = Server(archive);
        using var client = new HttpClient(handler);
        string cache = Temp();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DeltaReleaseAcquirer(client, cache).AcquireAsync(
                    "1.22.7", "0.3.17", "win-x64", CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(cache, "escaped.txt")));
            Assert.Empty(Directory.EnumerateDirectories(cache));
        }
        finally { Directory.Delete(cache, recursive: true); }
    }

    [Fact]
    public async Task RejectsWindowsDeviceNamesEvenWhenTheArchiveDigestMatches()
    {
        byte[] archive = Archive("runtime-payload/CON.dll");
        var handler = Server(archive);
        using var client = new HttpClient(handler);
        string cache = Temp();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DeltaReleaseAcquirer(client, cache).AcquireAsync(
                    "1.22.7", "0.3.17", "win-x64", CancellationToken.None));
            Assert.Empty(Directory.EnumerateDirectories(cache));
        }
        finally { Directory.Delete(cache, recursive: true); }
    }

    [Fact]
    public async Task RequiresPublishedAssetWithDigest()
    {
        byte[] archive = Archive();
        var handler = Server(archive, digest: null);
        using var client = new HttpClient(handler);
        string cache = Temp();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DeltaReleaseAcquirer(client, cache).AcquireAsync(
                    "1.22.7", "0.3.17", "win-x64", CancellationToken.None));
            Assert.Single(handler.Requests);
        }
        finally { Directory.Delete(cache, recursive: true); }
    }

    private static Handler Server(byte[] archive, string? digest = "auto")
    {
        string name = "Optimum-v0.3.17-VS1.22.7-win-x64-Delta.zip";
        string? hash = digest == "auto" ? Convert.ToHexString(SHA256.HashData(archive)) : digest;
        string metadata = JsonSerializer.Serialize(new
        {
            tag_name = "v0.3.17", draft = false, prerelease = false,
            assets = new[] { new { name, state = "uploaded", size = archive.Length,
                digest = hash is null ? null : "sha256:" + hash } },
        });
        return new Handler(request => request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
    }

    private static byte[] Archive(string? extra = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put("delta-release.json", JsonSerializer.Serialize(new
            {
                gameVersion = "1.22.7", optimumVersion = "0.3.17", rid = "win-x64",
                payloadFiles = new object[] { },
            }));
            Put("delta-pack/manifest.json", JsonSerializer.Serialize(new
            {
                format = "optimum-vcdiff-1", gameVersion = "1.22.7",
                optimumVersion = "0.3.17", rid = "win-x64", files = new object[] { },
            }));
            Put("runtime-payload/Optimum.dll", "content");
            Put("delta-decoder/decoder.json", "{}");
            Put("delta-uninstaller.exe", "content");
            if (extra is not null) Put(extra, "escape");

            void Put(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8, 1024,
                    leaveOpen: false);
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    private static string Temp()
    {
        string path = Path.Combine(Path.GetTempPath(), "optimum-acquirer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
