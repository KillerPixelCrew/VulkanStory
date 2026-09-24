using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System;
using Xunit.Abstractions;
using Xunit.Sdk;
using Xunit;

[assembly: TestCollectionOrderer(
    "Optimum.Render.Vulkan.Tests.LedgerRunsLastOrderer", "Optimum.Render.Vulkan.Tests")]

// Source: Optimum.Render.Vulkan.Tests/GpuCheckpointTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The device-loss diagnostic. The marker is the only part with logic of its
/// own: the driver stores whatever value it is handed and returns it verbatim,
/// so what matters is that the value decodes to what was encoded.
/// </summary>
public class GpuCheckpointTests
{
    private readonly ITestOutputHelper _output;

    public GpuCheckpointTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ADrawMarkerRoundTripsItsKindAndPayload()
    {
        nint marker = CheckpointMarker.Draw(CheckpointKind.DrawMulti, program: 0xBEEF, target: 7, mesh: 123456);

        Assert.NotEqual((nint)0, marker);
        Assert.Equal(CheckpointKind.DrawMulti, CheckpointMarker.KindOf(marker));
        Assert.Equal(123456u, CheckpointMarker.BOf(marker));
        Assert.Equal(
            "multi-draw with program 48879 'chunkopaque' mesh 123456 into framebuffer 7",
            CheckpointMarker.Describe(marker, id => id == 0xBEEF ? "chunkopaque" : null));
    }

    [Fact]
    public void EveryKindIsNonZeroAndReadsBackDistinctly()
    {
        nint frame = CheckpointMarker.FrameBegin(42);
        nint upload = CheckpointMarker.Upload(9, 4096, 2048);
        nint mips = CheckpointMarker.Mipmaps(9, 13);
        nint blit = CheckpointMarker.PresentBlit(2, 42);
        nint fullscreen = CheckpointMarker.Draw(CheckpointKind.Fullscreen, 3, 1, 0);

        Assert.All(new[] { frame, upload, mips, blit, fullscreen }, m => Assert.NotEqual((nint)0, m));

        Assert.Equal("frame 42 begins", CheckpointMarker.Describe(frame));
        Assert.Equal("upload of 4096x2048 texels into texture 9", CheckpointMarker.Describe(upload));
        Assert.Equal("mipmap generation for texture 9 (13 levels)", CheckpointMarker.Describe(mips));
        Assert.Equal("blit of frame 42 into swapchain image 2", CheckpointMarker.Describe(blit));
        Assert.Equal("fullscreen draw with program 3 into framebuffer 1", CheckpointMarker.Describe(fullscreen));
    }

    /// <summary>
    /// On a driver that offers checkpoints, reading them from a healthy queue
    /// must at least not fail: the crash path calls this with nothing to lose,
    /// and a diagnostic that throws is worse than none.
    /// </summary>
    [SkippableFact]
    public void CheckpointsCanBeReadFromAHealthyQueue()
    {
        var options = GpuTest.ContextOptions();
        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason),
            "No usable Vulkan device: " + reason);

        using (context)
        {
            _output.WriteLine("checkpoints: " + context!.CheckpointsAvailable +
                ", device fault: " + context.DeviceFaultAvailable);
            Skip.IfNot(context.CheckpointsAvailable, "Driver has no VK_NV_device_diagnostic_checkpoints.");

            var checkpoints = context.ReadQueueCheckpoints();
            Assert.NotNull(checkpoints);
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/SyncHazardLedgerTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

/// <summary>
/// xunit's default collection order, with the ledger collection moved to the
/// end so it sees every other test's observations. The suite is serial
/// (AssemblyInfo.cs), so "last" is well defined.
/// </summary>
public sealed class LedgerRunsLastOrderer : ITestCollectionOrderer
{
    private readonly DefaultTestCollectionOrderer _default;

    public LedgerRunsLastOrderer(IMessageSink diagnosticMessageSink) =>
        _default = new DefaultTestCollectionOrderer();

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        List<ITestCollection> ordered = _default.OrderTestCollections(testCollections).ToList();
        List<ITestCollection> ledger = ordered
            .Where(c => c.DisplayName == SyncHazardLedgerTests.CollectionName)
            .ToList();
        ordered.RemoveAll(c => c.DisplayName == SyncHazardLedgerTests.CollectionName);
        ordered.AddRange(ledger);
        return ordered;
    }
}

/// <summary>
/// Keeps <see cref="KnownSyncHazards" /> honest: every entry is well formed
/// and still happens. Pattern: KnownDonorGaps in
/// Optimum.Tests/mod-patcher-manifest-consistency-tests.cs.
/// </summary>
[Collection(CollectionName)]
public class SyncHazardLedgerTests
{
    public const string CollectionName = "Synchronization hazard ledger (runs last)";

    private readonly ITestOutputHelper _output;

    public SyncHazardLedgerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EveryPinnedHazardStillOccurs()
    {
        string summary = SyncHazardLedger.Summary();
        _output.WriteLine("validation features: '" + GpuTest.ValidationFeatures + "'");
        _output.WriteLine(summary);

        string? summaryPath = Environment.GetEnvironmentVariable("OPTIMUM_TEST_VALIDATION_SUMMARY");
        if (!string.IsNullOrEmpty(summaryPath)) System.IO.File.WriteAllText(summaryPath, summary);

        List<string> stale = SyncHazardLedger.StaleEntries(KnownSyncHazards.Entries);
        Assert.True(stale.Count == 0,
            "These pinned synchronization hazards no longer occur; remove them from KnownSyncHazards:\n  " +
            string.Join("\n  ", stale));
    }

    /// <summary>
    /// The companion rule on synthetic data: an entry goes stale only when its
    /// test asserted and the hazard was absent, never because the test did not run.
    /// </summary>
    [Fact]
    public void AnEntryIsStaleOnlyWhenItsTestAssertedWithoutTheHazard()
    {
        const string testClass = nameof(SyncHazardLedgerTests) + "Synthetic";
        var entries = new[]
        {
            new KnownSyncHazard("SYNC-HAZARD-WRITE-AFTER-WRITE", testClass, "StillOccurs", "synthetic", "1B"),
            new KnownSyncHazard("SYNC-HAZARD-WRITE-AFTER-WRITE", testClass, "Vanished", "synthetic", "1B"),
            new KnownSyncHazard("SYNC-HAZARD-WRITE-AFTER-WRITE", testClass, "NeverRan", "synthetic", "2"),
        };

        SyncHazardLedger.Observe(testClass, "StillOccurs", new[] { "SYNC-HAZARD-WRITE-AFTER-WRITE" });
        SyncHazardLedger.Observe(testClass, "Vanished", Array.Empty<string>());

        Assert.Equal(
            new[] { "SYNC-HAZARD-WRITE-AFTER-WRITE | " + testClass + ".Vanished" },
            SyncHazardLedger.StaleEntries(entries));
    }

    /// <summary>
    /// An unpinned synchronization message fails NoSyncHazards and is left out
    /// of NoErrors; a best-practices warning fails neither.
    /// </summary>
    [Fact]
    public void AnUnlistedSynchronizationMessageFailsAndABestPracticesWarningDoesNot()
    {
        var advice = new List<string> { "[warning] [BestPractices-synthetic] advice" };
        ValidationAssert.NoErrors(advice);
        ValidationAssert.NoSyncHazards(advice);

        var hazard = new List<string> { "[error] [SYNC-HAZARD-WRITE-AFTER-WRITE] synthetic hazard" };
        ValidationAssert.NoErrors(hazard);
        XunitException failure = Assert.ThrowsAny<XunitException>(() => ValidationAssert.NoSyncHazards(hazard));
        Assert.Contains(
            "SYNC-HAZARD-WRITE-AFTER-WRITE | SyncHazardLedgerTests | " +
            nameof(AnUnlistedSynchronizationMessageFailsAndABestPracticesWarningDoesNot),
            failure.Message);
    }

    [Fact]
    public void EveryPinnedHazardNamesARealTestItsDefectAndTheRetiringPhase()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Assembly assembly = typeof(SyncHazardLedgerTests).Assembly;

        foreach (KnownSyncHazard entry in KnownSyncHazards.Entries)
        {
            string key = entry.Id + " | " + entry.TestClass + "." + entry.TestMethod;
            if (!seen.Add(key)) problems.Add(key + ": listed twice");
            if (!entry.Id.StartsWith("SYNC-", StringComparison.Ordinal)) problems.Add(key + ": not a SYNC- id");
            if (string.IsNullOrWhiteSpace(entry.Defect)) problems.Add(key + ": no defect named");
            if (entry.RetiredBy is not ("1B" or "2")) problems.Add(key + ": retiring phase must be 1B or 2");

            Type? type = assembly.GetType("Optimum.Render.Vulkan.Tests." + entry.TestClass);
            MethodInfo? method = type?.GetMethod(entry.TestMethod, BindingFlags.Public | BindingFlags.Instance);
            if (method == null || method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length == 0)
            {
                problems.Add(key + ": no such test");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData("[error] [SYNC-HAZARD-WRITE-AFTER-WRITE] vkQueueSubmit(): ...", "SYNC-HAZARD-WRITE-AFTER-WRITE", true, false)]
    [InlineData("[warning] [BestPractices-vkCreateDevice-physical-device-features-not-retrieved] ...", "BestPractices-vkCreateDevice-physical-device-features-not-retrieved", false, true)]
    [InlineData("[error] [VUID-vkCmdDraw-None-08600] ...", "VUID-vkCmdDraw-None-08600", false, false)]
    [InlineData("[error] no tag here", null, false, false)]
    public void MessageIdsComeFromTheBracketTagAfterTheSeverity(
        string message, string? id, bool synchronization, bool bestPractices)
    {
        Assert.Equal(id, ValidationAssert.MessageId(message));
        Assert.Equal(synchronization, ValidationAssert.IsSynchronization(message));
        Assert.Equal(bestPractices, ValidationAssert.IsBestPractices(message));
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/SyncValidationControlTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The positive control for <see cref="ValidationAssert.NoSyncHazards" />. A
/// suite with no synchronization messages means nothing unless synchronization
/// validation demonstrably reports, under a SYNC- id our message tag carries,
/// when a hazard is there. This test creates one on purpose.
/// </summary>
public class SyncValidationControlTests
{
    private readonly ITestOutputHelper _output;

    public SyncValidationControlTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void AnUnsynchronisedWriteAfterWriteIsReportedUnderASyncId()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        options.ValidationFeatures = "sync";
        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason),
            "No usable Vulkan device: " + reason);

        using (context)
        {
            Skip.IfNot(context!.ValidationEnabled, "Validation layer not installed.");
            const uint size = 16;
            using var commands = new SetupQueue(context);
            using var textures = new TextureManager(context, commands.Uploads);
            VulkanTexture a = textures.Get(textures.Create(size, size, Format.R8G8B8A8Unorm))!;
            VulkanTexture b = textures.Get(textures.Create(size, size, Format.R8G8B8A8Unorm))!;
            VulkanTexture c = textures.Get(textures.Create(size, size, Format.R8G8B8A8Unorm))!;

            commands.SubmitAndWait(commandBuffer =>
            {
                textures.TransitionTexture(commandBuffer, a, ImageLayout.General);
                textures.TransitionTexture(commandBuffer, b, ImageLayout.General);
                textures.TransitionTexture(commandBuffer, c, ImageLayout.General);

                var region = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(size, size, 1),
                };
                // Two writes to the same texels of b and a read of them, with no
                // barrier in between: the misuse the layer exists to name.
                context.Api.CmdCopyImage(commandBuffer, a.Image, ImageLayout.General, b.Image, ImageLayout.General, 1, &region);
                context.Api.CmdCopyImage(commandBuffer, c.Image, ImageLayout.General, b.Image, ImageLayout.General, 1, &region);
                context.Api.CmdCopyImage(commandBuffer, b.Image, ImageLayout.General, a.Image, ImageLayout.General, 1, &region);
            });

            List<string> snapshot = ValidationAssert.Snapshot(messages);
            foreach (string message in snapshot) _output.WriteLine(message);
            Assert.True(snapshot.Any(ValidationAssert.IsSynchronization),
                "synchronization validation reported nothing for a deliberate hazard:\n" + string.Join("\n", snapshot));
        }
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/ValidationFeaturesTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The extra validation checks (sync validation, best practices with the vendor
/// sets, GPU-assisted) are requested through VK_EXT_layer_settings, with the
/// deprecated VK_EXT_validation_features as the fallback for older layers, both
/// chained into vkCreateInstance's pNext. A chained struct whose instance extension
/// was never enabled is ignored by a conformant loader, so the two decisions have
/// to be made together - that was the bug.
/// </summary>
public class ValidationFeaturesTests
{
    private readonly ITestOutputHelper _output;

    public ValidationFeaturesTests(ITestOutputHelper output) => _output = output;

    private static Dictionary<string, VulkanContext.ValidationLayerSetting> SettingsFor(string features)
    {
        var byName = new Dictionary<string, VulkanContext.ValidationLayerSetting>();
        foreach (VulkanContext.ValidationLayerSetting setting in VulkanContext.ValidationLayerSettings(features))
        {
            Assert.True(byName.TryAdd(setting.Name, setting), setting.Name + " is requested twice");
        }
        return byName;
    }

    /// <summary>
    /// The layer's own setting names (docs/vulkan.md#validation-and-acceptance §1): best practices report as
    /// warnings and performance messages, so "best" also widens report_flags; the desktop vendor
    /// sets come with it, the mobile ones only on request.
    /// </summary>
    [Fact]
    public void TheFeatureNamesMapOntoTheLayersSettings()
    {
        Dictionary<string, VulkanContext.ValidationLayerSetting> settings = SettingsFor(" sync , BEST ");
        Assert.True(settings["validate_sync"].Enabled);
        Assert.True(settings.ContainsKey("syncval_message_extra_properties"));
        Assert.True(settings["validate_best_practices"].Enabled);
        Assert.True(settings.ContainsKey("validate_best_practices_nvidia"));
        Assert.True(settings.ContainsKey("validate_best_practices_amd"));
        Assert.False(settings.ContainsKey("validate_best_practices_arm"));
        Assert.Equal("error,warn,perf", settings["report_flags"].Text);

        Dictionary<string, VulkanContext.ValidationLayerSetting> mobile = SettingsFor("best,mobile");
        Assert.True(mobile.ContainsKey("validate_best_practices_arm"));
        Assert.True(mobile.ContainsKey("validate_best_practices_img"));
    }

    /// <summary>GPU-AV is advised against alongside CPU core validation, so "gpu-only" turns core off.</summary>
    [Fact]
    public void GpuOnlyTurnsCoreValidationOff()
    {
        Assert.True(SettingsFor("gpu")["gpuav_enable"].Enabled);
        Assert.False(SettingsFor("gpu").ContainsKey("validate_core"));

        Dictionary<string, VulkanContext.ValidationLayerSetting> gpuOnly = SettingsFor("gpu-only");
        Assert.True(gpuOnly["gpuav_enable"].Enabled);
        Assert.False(gpuOnly["validate_core"].Enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense,,")]
    public void AnEmptyOrUnknownFeatureListSetsNothing(string? setting)
    {
        Assert.Empty(VulkanContext.ValidationLayerSettings(setting));
    }

    [Fact]
    public void TheFeatureListParsesTheDocumentedNames()
    {
        List<ValidationFeatureEnableEXT> enables =
            VulkanContext.ParseValidationFeatures(" sync , BEST ,gpu");

        Assert.Equal(new[]
        {
            ValidationFeatureEnableEXT.SynchronizationValidationExt,
            ValidationFeatureEnableEXT.BestPracticesExt,
            ValidationFeatureEnableEXT.GpuAssistedExt,
        }, enables);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense,,")]
    public void AnEmptyOrUnknownFeatureListAsksForNothing(string? setting)
    {
        Assert.Empty(VulkanContext.ParseValidationFeatures(setting));
    }

    /// <summary>
    /// The real thing: an instance created with features requested must come up.
    /// Before the fix the extension was missing while the struct was chained;
    /// with the extension enabled the loader validates the struct, so a mistake
    /// in it now shows up as a failed vkCreateInstance rather than as silence.
    /// </summary>
    [SkippableFact]
    public void AnInstanceComesUpWithTheFeaturesRequested()
    {
        var messages = new List<string>();
        var options = GpuTest.ContextOptions(messages);
        // This test is about the features themselves, whatever the suite default.
        options.ValidationFeatures = "sync,best";

        bool created = VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason);
        if (!created) _output.WriteLine("Vulkan unavailable: " + failureReason);
        Skip.IfNot(created, "No usable Vulkan device.");

        using (context)
        {
            Skip.IfNot(context!.ValidationEnabled, "Validation layer not installed.");
            Assert.NotEqual(default, context.Instance);
            _output.WriteLine("layer " + context.ValidationLayerVersion + ": " + context.ValidationSettingsApplied);

            // A current layer takes the settings, not the deprecated struct.
            using var api = Vk.GetApi();
            if (VulkanContext.LayerAdvertisesExtension(api, "VK_LAYER_KHRONOS_validation", VulkanContext.LayerSettingsExtensionName))
            {
                Assert.StartsWith("layer settings validate_sync", context.ValidationSettingsApplied);
                Assert.Contains("report_flags=error,warn,perf", context.ValidationSettingsApplied);
            }
            Assert.NotEmpty(context.ValidationLayerVersion);
        }
    }

    /// <summary>
    /// The features struct is only chained when the layer really advertises
    /// VK_EXT_validation_features. Naming an extension the layer does not have
    /// fails vkCreateInstance with ErrorExtensionNotPresent, and the bootstrap
    /// answers a failed context by falling back to OpenGL without a word - so a
    /// deprecated extension would turn OPTIMUM_VULKAN_VALIDATION_FEATURES into
    /// "Vulkan silently stopped working".
    /// </summary>
    [SkippableFact]
    public void TheFeaturesExtensionIsCheckedAgainstTheLayer()
    {
        using var api = Vk.GetApi();
        const string layer = "VK_LAYER_KHRONOS_validation";

        Skip.IfNot(
            VulkanContext.LayerAdvertisesExtension(api, layer, "VK_EXT_debug_utils")
                || VulkanContext.LayerAdvertisesExtension(api, layer, VulkanContext.ValidationFeaturesExtensionName),
            "Validation layer not installed.");

        // Whatever the installed layer answers for the real extension, an
        // invented one must be answered with false rather than optimistically
        // enabled - that is the whole point of the guard.
        Assert.False(VulkanContext.LayerAdvertisesExtension(api, layer, "VK_EXT_optimum_not_a_real_extension"));
        // And a layer that is not installed advertises nothing.
        Assert.False(VulkanContext.LayerAdvertisesExtension(
            api, "VK_LAYER_OPTIMUM_not_installed", VulkanContext.ValidationFeaturesExtensionName));
    }

    /// <summary>
    /// OPTIMUM_VULKAN_VALIDATION doubles as a log path. A Windows path has no
    /// forward slash in it and used to be mistaken for the bare "on" switch,
    /// which silently redirected the log to the temp file.
    /// </summary>
    [Theory]
    [InlineData("1", "/fallback.log")]
    [InlineData("true", "/fallback.log")]
    [InlineData("/tmp/x.log", "/tmp/x.log")]
    [InlineData("C:\\logs\\vulkan.log", "C:\\logs\\vulkan.log")]
    public void TheValidationSettingIsAPathOnlyWhenItLooksLikeOne(string setting, string expected)
    {
        Assert.Equal(expected, VulkanDevice.ResolveValidationLogPath(setting, "/fallback.log"));
    }

    [Fact]
    public void AnUnsetValidationSettingMirrorsNowhere()
    {
        Assert.Null(VulkanDevice.ResolveValidationLogPath(null, "/fallback.log"));
    }
}
}
