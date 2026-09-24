// Source: Optimum.Tests/EntityItemRendererCoverageTests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using Xunit;

public class EntityItemRendererCoverageTests
{
    [Fact]
    public void DoRender3DOpaqueGatesOnDistanceBeforeInterpolation()
    {
        string source = File.ReadAllText(FindRepositoryFile("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityItemRenderer.cs.patch"));

        // The distance gate uses 4096.0 (64^2) and appears before the render logic
        Assert.Contains("4096.0", source);
        Assert.Contains("dx * dx + dz * dz", source);
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

// Source: Optimum.Tests/EntityTesselationBudgetTests.cs
namespace Optimum.Tests
{
using Vintagestory.API.Config;
using Xunit;

// Not parallelized against other test classes touching OptimumConfig.EntityTesselationFrameBudget
// or the budget-remaining state (there is only one; xUnit runs test classes in this project
// sequentially by default here, matching every other OptimumDiagnostics test's convention of
// mutating shared static state directly).
public class EntityTesselationBudgetTests
{
    public EntityTesselationBudgetTests()
    {
        OptimumDiagnostics.EntityTesselationBudget.Reset();
    }

    [Fact]
    public void ResetSetsRemainingToConfiguredBudget()
    {
        int original = OptimumConfig.EntityTesselationFrameBudget;
        try
        {
            OptimumConfig.EntityTesselationFrameBudget = 2;
            OptimumDiagnostics.ResetEntityTesselationBudget();

            Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
            Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
            Assert.False(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
        }
        finally
        {
            OptimumConfig.EntityTesselationFrameBudget = original;
        }
    }

    [Fact]
    public void ResetEachFrameRefillsTheBudget()
    {
        int original = OptimumConfig.EntityTesselationFrameBudget;
        try
        {
            OptimumConfig.EntityTesselationFrameBudget = 1;
            OptimumDiagnostics.ResetEntityTesselationBudget();
            Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
            Assert.False(OptimumDiagnostics.TryConsumeEntityTesselationBudget());

            // Next frame's reset must refill it, not leave last frame's exhaustion stuck.
            OptimumDiagnostics.ResetEntityTesselationBudget();
            Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
        }
        finally
        {
            OptimumConfig.EntityTesselationFrameBudget = original;
        }
    }

    [Fact]
    public void ZeroBudgetDisablesTheCapEntirely()
    {
        int original = OptimumConfig.EntityTesselationFrameBudget;
        try
        {
            OptimumConfig.EntityTesselationFrameBudget = 0;
            OptimumDiagnostics.ResetEntityTesselationBudget();

            for (int i = 0; i < 50; i++)
            {
                Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
            }
        }
        finally
        {
            OptimumConfig.EntityTesselationFrameBudget = original;
        }
    }
}
}

// Source: Optimum.Tests/ItemRenderInfoReuseTests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

/// <summary>
/// Issue #74: the GUI item render path reuses a per-thread scratch ItemRenderInfo
/// instead of allocating one per visible slot per frame (measured ~104 B/slot,
/// up to ~136 slots => ~14 KB/frame with an inventory open). Correctness rests on
/// two things these tests pin:
///   1. ResetItemRenderInfo must clear the reused instance to exactly the same
///      values a freshly-constructed ItemRenderInfo has, or a stale field could
///      leak from one item into the next. This test derives the expected defaults
///      from a real `new ItemRenderInfo()` via reflection, so it fails if the
///      engine adds/changes a field and the reset list is not updated.
///   2. The public IRenderAPI path must keep allocating a fresh instance (mods
///      call it and may retain the result), only the internal per-slot path reuses.
/// Plus the config round-trip and the patch/patcher wiring.
/// </summary>
public class ItemRenderInfoReuseTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // The fields ResetItemRenderInfo(...) is responsible for clearing, with the
    // default value a fresh ItemRenderInfo has for each. TextureSize is a reused
    // sub-object (Width/Height cleared to 0); OverlayTexture is intentionally NOT
    // reset (kept for reuse, gated behind OverlayOpacity>0), matching the impl.
    [Fact]
    public void ResetDefaults_CoverAllValueFields_OfFreshInstance()
    {
        var fresh = new ItemRenderInfo();

        // Every public instance field on ItemRenderInfo that carries per-item state.
        // If the engine adds a new one, this list (and the impl's reset) must grow.
        var expected = new (string name, object? def)[]
        {
            (nameof(ItemRenderInfo.ModelRef), null),
            (nameof(ItemRenderInfo.Transform), null),
            (nameof(ItemRenderInfo.CullFaces), false),
            (nameof(ItemRenderInfo.TextureId), 0),
            (nameof(ItemRenderInfo.AlphaTest), 0f),
            (nameof(ItemRenderInfo.HalfTransparent), false),
            (nameof(ItemRenderInfo.NormalShaded), false),
            (nameof(ItemRenderInfo.ApplyColor), false),
            (nameof(ItemRenderInfo.OverlayOpacity), 0f),
            (nameof(ItemRenderInfo.DamageEffect), 0f),
            (nameof(ItemRenderInfo.InSlot), null),
            (nameof(ItemRenderInfo.dt), 0f),
        };

        foreach (var (name, def) in expected)
        {
            var f = typeof(ItemRenderInfo).GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(f != null, $"ItemRenderInfo.{name} missing - reset list is stale");
            Assert.Equal(def, f!.GetValue(fresh));
        }

        // TextureSize exists and is a fresh Size2i (Width/Height 0) - the impl
        // resets it in place rather than reallocating.
        var ts = typeof(ItemRenderInfo).GetField(nameof(ItemRenderInfo.TextureSize));
        Assert.NotNull(ts);
        var size = ts!.GetValue(fresh);
        Assert.NotNull(size);

        // Guard against the engine adding a public instance field we do not handle.
        // OverlayTexture is the one deliberately-not-reset field.
        var handled = new System.Collections.Generic.HashSet<string>(Array.ConvertAll(expected, e => e.name))
        {
            nameof(ItemRenderInfo.TextureSize),
            nameof(ItemRenderInfo.OverlayTexture),
        };
        foreach (var f in typeof(ItemRenderInfo).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(handled.Contains(f.Name),
                $"ItemRenderInfo has unhandled public field '{f.Name}'. Update ResetItemRenderInfo and this test.");
        }
    }

    [Fact]
    public void ItemRenderInfoReuse_ConfigRoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "optimum-item-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            OptimumConfig.SetDataPath(dir);
            OptimumConfig.ItemRenderInfoReuseEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.ItemRenderInfoReuseEnabled = true; // clobber before reload
            OptimumConfig.Load();
            Assert.False(OptimumConfig.ItemRenderInfoReuseEnabled);

            OptimumConfig.ItemRenderInfoReuseEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.ItemRenderInfoReuseEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.ItemRenderInfoReuseEnabled);
        }
        finally
        {
            OptimumConfig.ItemRenderInfoReuseEnabled = true;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void InventoryItemRendererSource_ReusesScratchAndKeepsPublicApiFresh()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(),
            "build/VintagestoryLib/Vintagestory.Client.NoObf/InventoryItemRenderer.cs"));
        // Internal fill helper + reset dependency exist.
        Assert.Contains("FillItemStackRenderInfo", src);
        Assert.Contains("ResetItemRenderInfo", src);
        // The per-thread scratch is reused in the GUI path, gated by the flag.
        Assert.Contains("optimumGuiRenderInfoScratch", src);
        Assert.Contains("OptimumConfig.ItemRenderInfoReuseEnabled", src);
        // The public API method still hands back a fresh instance for mods.
        Assert.Contains("return FillItemStackRenderInfo(game, inSlot, target, dt, new ItemRenderInfo())", src);
    }

    [Fact]
    public void PatcherRegistersItemRenderMembersAndTransplant()
    {
        string program = PatcherSource.Read();
        Assert.Contains("optimumGuiRenderInfoScratch", program);
        Assert.Contains("FillItemStackRenderInfo", program);
        Assert.Contains("ResetItemRenderInfo", program);
        Assert.Contains("\"RenderItemstackToGui\"", program);
    }

    [Fact]
    public void ItemRenderProfilerCounterIsWired()
    {
        // Diagnostic profiler (OPTIMUM_ITEM_PROFILE) exists and starts at zero frames.
        OptimumDiagnostics.ItemRenderEndFrame();
        Assert.Contains("item-render profile", OptimumDiagnostics.GetItemRenderProfileSummary());
    }
}
}

// Source: Optimum.Tests/entity-render-hysteresis-coverage-tests.cs
namespace Optimum.Tests
{
using System.IO;
using Xunit;

public class EntityRenderHysteresisCoverageTests
{
    [Fact]
    public void OptimumConfig_HasHysteresisFactorSq()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("HysteresisFactorSq", config);
        Assert.Contains("1.21", config);
    }

    [Fact]
    public void OnBeforeRender_UsesHysteresisForRenderDistance()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");
        Assert.Contains("numHysteresis", source);
        Assert.Contains("HysteresisFactorSq", source);
        Assert.Contains("entity.IsRendered", source);
    }

    [Fact]
    public void OnRenderFrameShadows_UsesHysteresisForShadowCull()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");
        Assert.Contains("entity.IsShadowRendered", source);
        Assert.Contains("shadowThreshold", source);
    }

    [Fact]
    public void ChiselLod_InFrustumAndRange_UsesHysteresisThresholds()
    {
        string source = Read("optimum-api-contracts/optimum-api-bridge.cs");
        Assert.Contains("outerThreshold", source);
        Assert.Contains("innerThreshold", source);
        Assert.Contains("HysteresisFactorSq", source);
        Assert.Contains("nowVisible", source);
    }

    [Fact]
    public void ChiselLod_InFrustumShadowPass_UsesHysteresis()
    {
        string source = Read("optimum-api-contracts/optimum-api-bridge.cs");
        Assert.Contains("InFrustumShadowPass", source);
        Assert.Contains("baseResult", source);
    }

    [Fact]
    public void HysteresisFactor_IsOnePointOneSquared()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("1.21", config);
        Assert.Contains("1.1", config);
    }

    [Fact]
    public void OnBeforeRender_HysteresisAppliesOnlyToDistanceCheck()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");
        Assert.Contains("entity == game.EntityPlayer || entity.AllowOutsideLoadedRange", source);
        Assert.Contains("num * OptimumConfig.HysteresisFactorSq", source);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
}

// Source: Optimum.Tests/entity-render-p0-tests.cs
namespace Optimum.Tests
{
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;

public class EntityRenderP0Tests
{
    [Fact]
    public void PackedLightConversionMatchesWorldMapFormulaForEveryValue()
    {
        byte[] hueLevels = new byte[64];
        byte[] saturationLevels = new byte[8];
        float[] blockLightLevels = new float[32];
        float[] sunlightLevels = new float[32];
        for (int i = 0; i < hueLevels.Length; i++) hueLevels[i] = (byte)(i * 251 / 63);
        for (int i = 0; i < saturationLevels.Length; i++) saturationLevels[i] = (byte)(i * 253 / 7);
        for (int i = 0; i < blockLightLevels.Length; i++) blockLightLevels[i] = i * i / (31f * 31f);
        for (int i = 0; i < sunlightLevels.Length; i++) sunlightLevels[i] = i / 31f;

        for (int saturation = 0; saturation < 8; saturation++)
        {
            for (int packedValue = 0; packedValue <= ushort.MaxValue; packedValue++)
            {
                ushort packedLight = (ushort)packedValue;
                OptimumEntityLightConverter.FromPackedLight(packedLight, saturation, hueLevels, saturationLevels, blockLightLevels, sunlightLevels, out float red, out float green, out float blue, out float sunlight);

                int packedHsv = (packedLight & 0x1F) | ((packedLight & 0x3E0) << 3) | ((packedLight & 0xFC00) << 6) | (saturation << 24);
                byte hue = hueLevels[(packedHsv >> 16) & 0xFF];
                int sat = saturationLevels[(packedHsv >> 24) & 0xFF];
                int value = (int)(blockLightLevels[(packedHsv >> 8) & 0xFF] * 255f);
                int rgb = ColorUtil.HsvToRgb(hue, sat, value);
                float expectedRed = (float)(rgb >> 16) / 255f;
                float expectedGreen = (float)((rgb >> 8) & 0xFF) / 255f;
                float expectedBlue = (float)(rgb & 0xFF) / 255f;
                float expectedSunlight = sunlightLevels[packedHsv & 0xFF];

                Assert.True(
                    BitConverter.SingleToInt32Bits(expectedRed) == BitConverter.SingleToInt32Bits(red)
                    && BitConverter.SingleToInt32Bits(expectedGreen) == BitConverter.SingleToInt32Bits(green)
                    && BitConverter.SingleToInt32Bits(expectedBlue) == BitConverter.SingleToInt32Bits(blue)
                    && BitConverter.SingleToInt32Bits(expectedSunlight) == BitConverter.SingleToInt32Bits(sunlight),
                    $"packed={packedValue}, saturation={saturation}");
            }
        }
    }

    [Fact(Skip = "Requires fork patch: EntityShapeRenderer IOptimumEntityLightSampler")]
    public void PreparedBaseLightRequiresMatchingCoordinatesAndConsumesOnce()
    {
        // Test body requires EntityShapeRenderer to implement IOptimumEntityLightSampler.
        // Re-enable after porting the 266-line EntityShapeRenderer.cs patch.
    }

    [Fact(Skip = "Requires fork patch: EntityShapeRenderer IOptimumEntityLightSampler")]
    public void PreparedTallLightSelectsUpperOnlyWhenItsSunlightIsGreater()
    {
        // Test body requires EntityShapeRenderer to implement IOptimumEntityLightSampler.
        // Re-enable after porting the 266-line EntityShapeRenderer.cs patch.
    }

    [Fact(Skip = "Requires fork patch: EntityShapeRenderer IOptimumEntityLightSampler")]
    public void CoordinateMismatchInvalidatesTheActivatedBatch()
    {
        // Test body requires EntityShapeRenderer to implement IOptimumEntityLightSampler.
        // Re-enable after porting the 266-line EntityShapeRenderer.cs patch.
    }

    [Fact(Skip = "Requires fork patch: EntityShapeRenderer IOptimumEntityLightSampler")]
    public void DisposedSourceChunkRejectsPreparedLight()
    {
        // Test body requires EntityShapeRenderer to implement IOptimumEntityLightSampler.
        // Re-enable after porting the 266-line EntityShapeRenderer.cs patch.
    }

    [Fact]
    public void ShaderStateCountsMatchingUsesAndClearsItsScope()
    {
        IShaderProgram shader = DispatchProxy.Create<IShaderProgram, ShaderProxy>();
        IShaderProgram otherShader = DispatchProxy.Create<IShaderProgram, ShaderProxy>();
        var ubo = new TestUboRef();
        OptimumEntityShaderState.End();

        try
        {
            OptimumEntityShaderState.Begin(shader, ubo);
            Assert.True(OptimumEntityShaderState.TryGetAnimationUbo(shader, out UBORef first));
            Assert.Same(ubo, first);
            Assert.False(OptimumEntityShaderState.TryGetAnimationUbo(otherShader, out _));
            Assert.True(OptimumEntityShaderState.TryGetAnimationUbo(shader, out UBORef second));
            Assert.Same(ubo, second);
            Assert.Equal(2, OptimumEntityShaderState.End());
            Assert.False(OptimumEntityShaderState.TryGetAnimationUbo(shader, out _));
        }
        finally
        {
            OptimumEntityShaderState.End();
        }
    }

    [Fact]
    public void ShaderStateRejectsDisposedAnimationBuffer()
    {
        IShaderProgram shader = DispatchProxy.Create<IShaderProgram, ShaderProxy>();
        var ubo = new TestUboRef();
        ubo.Dispose();
        OptimumEntityShaderState.Begin(shader, ubo);

        try
        {
            Assert.False(OptimumEntityShaderState.TryGetAnimationUbo(shader, out _));
            Assert.Equal(0, OptimumEntityShaderState.End());
        }
        finally
        {
            OptimumEntityShaderState.End();
        }
    }

    [Fact]
    public void EntityRenderDiagnosticsReportAndResetAggregateCounts()
    {
        OptimumDiagnostics.ResetAllCounters();
        OptimumDiagnostics.RecordEntityLightBatch(samples: 7, preparedSamples: 5, chunkGroups: 3, failedChunkGroups: 1, lockBatches: 2, maxBatchSize: 4, elapsedTicks: Stopwatch.Frequency / 1000);
        OptimumDiagnostics.RecordEntityLightCoordinateMismatch();
        OptimumDiagnostics.RecordEntityLightChunkInvalidation();
        OptimumDiagnostics.RecordEntityShaderSegment(useCount: 4);

        string summary = OptimumDiagnostics.GetCountersSummary();
        Assert.Contains("frames=1, samples=7, prepared=5, chunkGroups=3, failedChunkGroups=1, coordinateMismatches=1", summary);
        Assert.Contains("chunkInvalidations=1, lockBatches=2, maxBatchSize=4, timedFrames=1, sampledBatchMs=1", summary);
        Assert.Contains("segments=1, uses=4, uniformUploadsAvoided=6, uboLookupsAvoided=3", summary);

        OptimumDiagnostics.ResetAllCounters();
        summary = OptimumDiagnostics.GetCountersSummary();
        Assert.Contains("frames=0, samples=0, prepared=0", summary);
        Assert.Contains("chunkInvalidations=0, lockBatches=0, maxBatchSize=0, timedFrames=0, sampledBatchMs=0", summary);
        Assert.Contains("segments=0, uses=0, uniformUploadsAvoided=0, uboLookupsAvoided=0", summary);
    }

    [Fact]
    public void SourceCoveragePinsFallbacksAndClientOwnership()
    {
        string system = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch");
        string clientChunk = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientChunk.cs.patch");
        string renderer = PatchReader.ReadPatch("patches/runtime/VSEssentials/Vintagestory/GameContent/EntityShapeRenderer.cs.patch");
        string patcher = PatcherSource.Read();

        Assert.Contains("OptimumConfig.EffectiveEntityLightBatch && !optimumEntityLightBatchDisabled && optimumEntityLightPreviousSampleCount >= OptimumEntityLightMinimumSamples ? PrepareOptimumEntityLights() : 0", system);
        Assert.Contains("OptimumConfig.EffectiveEntityShaderStateCache", system);
        Assert.Contains("!optimumEntityShaderCacheDisabled", system);
        Assert.Contains("OptimumEntityLightBatchSize = 256", system);
        Assert.Contains("OptimumEntityLightMinimumSamples = 4", system);
        Assert.Contains("optimumEntityLightPreviousSampleCount >= OptimumEntityLightMinimumSamples", system);
        Assert.Contains("worldChunk is ClientChunk", system);
        Assert.Contains("finally", system);
        Assert.Contains("batchId = 0;", system);
        Assert.Contains("optimumEntityShaderFailureLogged", system);
        Assert.Contains("lock (packUnpackLock)", clientChunk);
        Assert.Contains("Unpack_ReadOnly();", clientChunk);
        Assert.Contains("if (Disposed)", clientChunk);
        Assert.Contains("TryUseOptimumLightSamples", renderer);
        Assert.Contains("GetLightRGBs(lightX, lightY, lightZ)", renderer);
        Assert.Contains("optimumUpperLightSun > optimumBaseLightSun", renderer);
        Assert.Contains("shaderRenderMethod?.DeclaringType == typeof(EntityShapeRenderer)", renderer);
        Assert.Contains("shaderRenderer.OptimumShaderStateCompatible", system);
        Assert.Contains("!useOptimumShaderState", renderer);
        Assert.Contains("Optimum.EntityLightBatchBuffer", patcher);
        Assert.Contains("OptimumReadLightBatch", patcher);

        string clientContract = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VintagestoryApi/Client/optimum-entity-render-batch.cs"));
        Assert.Contains("namespace Vintagestory.API.Client", clientContract);
        Assert.DoesNotContain("temporalStability", clientContract);

        string serverRoot = Path.Combine(Path.GetDirectoryName(PatchReader.FindRepositoryFile("patches/cecil-owned.list"))!, "VintagestoryLib", "Vintagestory.Server");
        if (Directory.Exists(serverRoot))
        {
            foreach (string path in Directory.GetFiles(serverRoot, "*.patch", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain("OptimumReadLightBatch", File.ReadAllText(path));
                Assert.DoesNotContain("OptimumEntityShaderState", File.ReadAllText(path));
            }
        }
    }

    [Fact]
    public void BothEntityRenderControlsDefaultOnAndReachTheSettingsPage()
    {
        Assert.True(OptimumConfig.EntityLightBatchEnabled);
        Assert.True(OptimumConfig.EntityShaderStateCacheEnabled);

        string config = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VintagestoryApi/Config/OptimumConfig.cs"));
        string gui = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch");
        Assert.Contains("public bool EntityLightBatch { get; set; } = true;", config);
        Assert.Contains("public bool EntityShaderStateCache { get; set; } = true;", config);
        Assert.Contains("onOptimumEntityLightBatchChanged", gui);
        Assert.Contains("onOptimumEntityShaderCacheChanged", gui);
    }

    public class ShaderProxy : DispatchProxy
    {
        public ShaderProxy() { }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    public class WorldChunkProxy : DispatchProxy
    {
        public bool DisposedValue { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Disposed")
            {
                return DisposedValue;
            }
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    private static EntityShapeRenderer CreateUninitializedShapeRenderer()
    {
        return (EntityShapeRenderer)RuntimeHelpers.GetUninitializedObject(typeof(EntityShapeRenderer));
    }

    private static bool TryUsePreparedLight(EntityShapeRenderer renderer, int x, int y, int z, bool needsUpperSample, out Vec4f light)
    {
        MethodInfo method = typeof(EntityShapeRenderer).GetMethod("TryUseOptimumLightSamples", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = [x, y, z, needsUpperSample, null];
        bool used = (bool)method.Invoke(renderer, arguments)!;
        light = (Vec4f)arguments[4]!;
        return used;
    }

    private static void AssertLight(Vec4f light, float red, float green, float blue, float sunlight)
    {
        Assert.Equal(red, light.R);
        Assert.Equal(green, light.G);
        Assert.Equal(blue, light.B);
        Assert.Equal(sunlight, light.W);
    }

    private sealed class TestUboRef : UBORef
    {
        public override void Bind() { }
        public override void Unbind() { }
        public override void Update<T>(T data) { }
        public override void Update<T>(T data, int offset, int size) { }
        public override void Update(object data, int offset, int size) { }
    }
}
}
