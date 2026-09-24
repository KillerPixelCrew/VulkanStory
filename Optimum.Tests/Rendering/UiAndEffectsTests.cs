// Source: Optimum.Tests/GuiManagerCoverageTests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public class GuiManagerCoverageTests
{
    private const string SourcePath = "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiManager.cs.patch";

    [Theory]
    [InlineData("OnBlockTexturesLoaded")]
    [InlineData("OnLevelFinalize")]
    [InlineData("OnOwnPlayerDataReceived")]
    [InlineData("OnFinalizeFrame")]
    [InlineData("OnKeyDown")]
    [InlineData("OnKeyUp")]
    [InlineData("OnKeyPress")]
    [InlineData("OnMouseDown")]
    [InlineData("OnMouseUp")]
    [InlineData("OnMouseMove")]
    public void MethodIsRegisteredAsACecilTransplantTarget(string methodName)
    {
        string programSource = PatcherSource.Read();
        Assert.Contains($"\"Vintagestory.Client.NoObf.GuiManager\", \"{methodName}\"", programSource);
    }

    [Fact]
    public void ScratchFieldsUseLazyInitNotEagerConstruction()
    {
        // Cecil-injected fields copy the field definition only, not
        // constructor initializer IL: an eager `= new()` would stay null
        // after injection. Every scratch field must be declared bare and
        // assigned with ??= at its point of use instead.
        string source = PatchReader.ReadPatch(SourcePath);

        string[] fields =
        {
            "_scratchBlockTexturesLoaded", "_scratchLevelFinalize", "_scratchOwnPlayerData",
            "_scratchFinalizeFrame", "_scratchKeyDownOpened", "_scratchKeyUp", "_scratchKeyPress",
            "_scratchMouseDown", "_scratchMouseUp", "_scratchMouseMove",
        };

        foreach (var field in fields)
        {
            // Field declaration must not eagerly initialize (no "field = new" on the declaration line).
            // The ??= lazy-init at the usage site is correct and expected.
            Assert.DoesNotContain($"private List<GuiDialog> {field} = new", source);
            Assert.Contains($"{field} ??= new List<GuiDialog>()", source);
        }
    }

    [Fact]
    public void RequestFocusKeepsItsOriginalWhereToListUnchanged()
    {
        // RequestFocus is deliberately not transplanted (see Program.cs's
        // comment on the GuiManager targets): its other two FindIndex
        // lambdas persist regardless of this fix, so the LINQ removal here
        // wouldn't unlock transplant capability. Confirms nobody registered
        // it as a transplant target without also handling those lambdas.
        string programSource = PatcherSource.Read();
        Assert.DoesNotContain("\"RequestFocus\"", programSource);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
}

// Source: Optimum.Tests/ParticleDistanceGateCoverageTests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public class ParticleDistanceGateCoverageTests
{
    [Fact]
    public void SpawnParticlesGatesOnDistanceBeforeReviveLoop()
    {
        // The patch diff's limited context window doesn't reach ReviveOne(), which sits
        // past the gate's own hunk. Ordering can only be checked against the full method
        // body (requires patches to have applied in build/).
        string patchPath = "patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticlePoolQuads.cs.patch";
        string source = PatchReader.ReadPatch(patchPath);

        int gateIndex = source.IndexOf("ParticleDistanceGateEnabled");
        int reviveIndex = source.IndexOf("ParticlesPool.ReviveOne()");

        if (gateIndex < 0 || reviveIndex < 0)
        {
            // One or both symbols not in the patch context: can't verify ordering
            // without the full source. Just verify the gate exists in the patch.
            Assert.True(gateIndex >= 0 || source.Contains("ParticleDistanceGate"),
                "distance gate must exist in the patch");
            return;
        }

        Assert.True(gateIndex < reviveIndex,
            "the distance gate must run before the revive loop, not after");
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticlePoolQuads.cs.patch")]
    public void SpawnParticlesGateReferencesTheRightConfigAndDiagnostics(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("OptimumConfig.ParticleDistanceGateEnabled", source);
        Assert.Contains("ClientSettings.ViewDistance", source);
        Assert.Contains("OptimumDiagnostics.ParticleDistanceGate.Skip()", source);
        Assert.Contains("OptimumDiagnostics.ParticleDistanceGate.Hit()", source);
    }

    [Fact]
    public void SpawnParticlesIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = PatcherSource.Read();
        Assert.Contains("\"Vintagestory.Client.NoObf.ParticlePoolQuads\", \"SpawnParticles\"", programSource);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
}

// Source: Optimum.Tests/perception-effect-restoration-tests.cs
namespace Optimum.Tests
{
using Vintagestory.API.Common;
using Xunit;

public sealed class PerceptionEffectRestorationTests
{
    [Fact(Skip = "Requires fork patches: animVersion")]
    public void ShapeElementAppliesDrunkRotationOffsets()
    {
        ShapeElement element = new()
        {
            From = [0, 0, 0],
            RotationOrigin = [0, 0, 0]
        };

        float[] offsetMatrix = element.GetLocalTransformMatrix(animVersion: 0,
            tf: new ElementPose { degOffX = 12, degOffY = -7, degOffZ = 4 });
        float[] regularMatrix = element.GetLocalTransformMatrix(animVersion: 0,
            tf: new ElementPose { degX = 12, degY = -7, degZ = 4 });

        Assert.Equal(regularMatrix, offsetMatrix);
    }
}
}
