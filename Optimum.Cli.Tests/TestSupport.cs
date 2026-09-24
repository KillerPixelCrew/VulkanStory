// Source: Optimum.Cli.Tests/FakeBuildDriver.cs
namespace Optimum.Cli.Tests
{
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;

/// <summary>A scripted <see cref="IBuildDriver"/> so CLI tests never run a real build.</summary>
public sealed class FakeBuildDriver : IBuildDriver
{
    public bool WasRun { get; private set; }

    public Func<IBuildObserver, CancellationToken, BuildResult> Behaviour { get; set; } =
        static (observer, _) =>
        {
            observer.Phase(ProgressPhase.Decompile, 5, "extracting");
            observer.Phase(ProgressPhase.Decompile, 30, "ilspycmd");
            observer.Phase(ProgressPhase.Patch, 50, "applying patches");
            observer.Log(LogLevel.Warn, "innoextract not present; Windows package skipped");
            observer.Phase(ProgressPhase.Assemble, 80, "dotnet build");
            observer.Phase(ProgressPhase.Verify, 98, "package produced");
            return BuildResult.Success("/out/Optimum-v0.3.14-linux-x64");
        };

    public Task<BuildResult> RunAsync(BuildRequest request, IBuildObserver observer, CancellationToken forceful, CancellationToken graceful = default)
    {
        WasRun = true;
        forceful.ThrowIfCancellationRequested();
        return Task.FromResult(Behaviour(observer, forceful));
    }
}
}

// Source: Optimum.Cli.Tests/NdjsonStream.cs
namespace Optimum.Cli.Tests
{
using System.Text.Json;
using Xunit;

/// <summary>
/// Consumes an NDJSON stream the way RiftLauncher's <c>runTrackedWorker</c> does
/// and asserts the contract in INSTALLER-PLAN.md section 4. This is the reusable
/// conformance check the CI step also runs.
/// </summary>
public sealed class NdjsonStream
{
    private static readonly HashSet<string> KnownTypes = ["progress", "log", "result"];
    private static readonly HashSet<string> KnownPhases = ["decompile", "patch", "verify", "assemble"];
    private static readonly HashSet<string> KnownReasons =
    [
        "bad-input", "unsupported-version", "patch-conflict", "decompile-failed",
        "assemble-failed", "verification-failed", "output-exists", "cancelled", "engine-internal",
    ];

    public required IReadOnlyList<JsonElement> Lines { get; init; }

    public JsonElement Terminal => Lines[^1];

    public static NdjsonStream Parse(string stdout)
    {
        var lines = new List<JsonElement>();
        foreach (string raw in stdout.Split('\n'))
        {
            if (raw.Length == 0)
                continue;
            using var doc = JsonDocument.Parse(raw);
            lines.Add(doc.RootElement.Clone());
        }

        Assert.NotEmpty(lines);
        return new NdjsonStream { Lines = lines };
    }

    public void AssertContract()
    {
        int lastProgress = 0;
        int resultCount = 0;

        for (int i = 0; i < Lines.Count; i++)
        {
            JsonElement line = Lines[i];
            Assert.Equal(JsonValueKind.Object, line.ValueKind);
            string type = line.GetProperty("type").GetString()!;
            Assert.True(KnownTypes.Contains(type), $"unknown line type: {type}");

            switch (type)
            {
                case "progress":
                    Assert.Contains(line.GetProperty("phase").GetString()!, KnownPhases);
                    int progress = line.GetProperty("progress").GetInt32();
                    Assert.InRange(progress, lastProgress, 99);
                    lastProgress = progress;
                    break;

                case "log":
                    Assert.Contains(line.GetProperty("level").GetString(), new[] { "info", "warn", "error" });
                    break;

                case "result":
                    resultCount++;
                    Assert.Equal(Lines.Count - 1, i);
                    if (!line.GetProperty("ok").GetBoolean())
                    {
                        Assert.Contains(line.GetProperty("reason").GetString()!, KnownReasons);
                        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("message").GetString()));
                    }
                    else
                    {
                        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("runtimePath").GetString()));
                    }
                    break;
            }
        }

        Assert.Equal(1, resultCount);
    }
}
}
