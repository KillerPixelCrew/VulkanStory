using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Seam S1: device requirements, latency detection and backend selection.
///
/// The table cases run without a GPU; the device cases check that a contributor
/// really reaches VkDeviceCreateInfo, that asking for something the driver does
/// not have is a note rather than a failed device, and that the colour-write
/// tier - whose own tests pin it - comes out the same with contributors in play.
///
/// <para><b>The join (S1 -> S2-S7).</b> The selection made while the device is
/// created has to be the backend the frame actually runs on. Stage A owns the lib
/// hook, stage B the selection and stage C the markers; the only thing that joins
/// them is <c>VulkanDevice.InstallSelectedLatencyBackend</c>. Since wave 3 all four
/// backends exist, so a selection resolves to its own kind or to a rung further down
/// the degrade ladder. Whichever it is, with the mode off the installed backend
/// sleeps nowhere and owns no frame cap - "nothing on screen changes" is exactly that
/// assertion. (The shipped default is on since 2026-09-12; those tests pin the mode
/// themselves.)</para>
///
/// <para><b>"Off is off" on a real device.</b> A device that comes up with nobody
/// selecting a latency backend has the None backend, and driving the whole backend
/// interface across real frames neither paces the frame nor produces a validation
/// message. This is the check the later stages regress against.</para>
///
/// <para><b>The ladder.</b> All four backend kinds are constructible from the one
/// switch in <c>VulkanDevice.CreateLatencyBackend</c>, and a forced kind the device
/// cannot host degrades down the documented ladder
/// (<see cref="LatencyBackendSelector.Degrade" />: NV and AMD to Native, Native to
/// None) instead of silently becoming None or failing the device. The three backend
/// stages each tested their own backend against real hardware; what none of them
/// could test is that the merged switch still constructs the other two, so these run
/// headless and assert against the ladder rather than against one vendor's
/// hardware.</para>
/// </summary>
public class LatencyCapabilityTests
{
    private readonly ITestOutputHelper _output;

    public LatencyCapabilityTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------ tables

    // auto takes the best backend the device really supports; a forced backend
    // the device cannot support degrades and says so (the ColorWriteTier rule).
    [Theory]
    // no vendor support at all
    [InlineData(false, 0, false, false, null, "native")]
    // NV advertised but no present id: the extension has nothing to attribute to
    [InlineData(true, 2, false, false, null, "native")]
    [InlineData(true, 2, false, true, null, "nv")]
    [InlineData(true, 3, false, true, null, "nv")]
    // AMD's feature bit is all the AMD path needs
    [InlineData(false, 0, true, false, null, "amd")]
    // both: NV first
    [InlineData(true, 2, true, true, null, "nv")]
    // forced backends
    [InlineData(true, 2, true, true, "off", "off")]
    [InlineData(true, 2, true, true, "native", "native")]
    [InlineData(false, 0, true, false, "nv", "native")]
    [InlineData(true, 2, false, true, "amd", "native")]
    [InlineData(true, 2, false, true, "nv", "nv")]
    [InlineData(false, 0, true, false, "amd", "amd")]
    public void TheBestSupportedBackendAtOrBelowTheForcedOneIsSelected(
        bool nv, uint nvRevision, bool amd, bool presentId, string? forced, string expected)
    {
        var support = new LatencyDeviceSupport(nv, nvRevision, amd, presentId, presentId2: false);
        LatencyBackendKind selected = LatencyBackendSelector.Select(
            support, LatencyBackends.ParseBackend(forced), LatencyPresentPath.BlitFromOwned);
        Assert.Equal(expected, LatencyBackends.Token(selected));
    }

    [Fact]
    public void ADegradeIsLogged()
    {
        var notes = new List<string>();
        var support = new LatencyDeviceSupport(false, 0, false, false, false);
        LatencyBackendKind selected = LatencyBackendSelector.Select(
            support, LatencyBackendKind.NvLowLatency2, LatencyPresentPath.BlitFromOwned, notes.Add);

        Assert.Equal(LatencyBackendKind.Native, selected);
        Assert.Single(notes);
        Assert.Contains("nv", notes[0]);
        Assert.Contains("native", notes[0]);
        _output.WriteLine(notes[0]);
    }

    [Fact]
    public void AnAvailableBackendIsNotLogged()
    {
        var notes = new List<string>();
        var support = new LatencyDeviceSupport(true, 2, false, true, false);
        Assert.Equal(LatencyBackendKind.NvLowLatency2, LatencyBackendSelector.Select(
            support, LatencyBackendKind.NvLowLatency2, LatencyPresentPath.BlitFromOwned, notes.Add));
        Assert.Empty(notes);
    }

    // A backend is only valid for the present path that drives it (seam S8):
    // both Vulkan swapchain paths host all of them, the D3D12 bridge hosts none.
    [Theory]
    // The path travels as its enum value: the enum is internal to the backend.
    [InlineData((int)LatencyPresentPath.BlitFromOwned, "nv")]
    [InlineData((int)LatencyPresentPath.DirectToSwapchain, "nv")]
    [InlineData((int)LatencyPresentPath.D3D12Bridge, "off")]
    public void APresentPathThatCannotHostTheBackendDegradesIt(int pathValue, string expected)
    {
        var path = (LatencyPresentPath)pathValue;
        var support = new LatencyDeviceSupport(true, 3, true, true, false);
        var notes = new List<string>();
        LatencyBackendKind selected = LatencyBackendSelector.Select(support, forced: null, path, notes.Add);

        Assert.Equal(expected, LatencyBackends.Token(selected));
        Assert.Contains(selected, LatencyBackendSelector.AllowedBackends(path));
        if (path == LatencyPresentPath.D3D12Bridge)
        {
            Assert.Equal(new[] { LatencyBackendKind.None }, LatencyBackendSelector.AllowedBackends(path));
            Assert.NotEmpty(notes);
        }
        else
        {
            Assert.Equal(4, LatencyBackendSelector.AllowedBackends(path).Length);
        }
    }

    [Fact]
    public void TheDegradeLadderEndsAtNone()
    {
        Assert.Equal(LatencyBackendKind.Native, LatencyBackendSelector.Degrade(LatencyBackendKind.NvLowLatency2));
        Assert.Equal(LatencyBackendKind.Native, LatencyBackendSelector.Degrade(LatencyBackendKind.AmdAntiLag));
        Assert.Equal(LatencyBackendKind.None, LatencyBackendSelector.Degrade(LatencyBackendKind.Native));
        Assert.Equal(LatencyBackendKind.None, LatencyBackendSelector.Degrade(LatencyBackendKind.None));
    }

    // VkLatencySubmissionPresentIdNV, and with it per-submit attribution, only
    // exists from revision 3; 615.71.09 is revision 2.
    [Theory]
    [InlineData(0u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    public void PerSubmitAttributionNeedsRevisionThree(uint revision, bool expected)
    {
        var support = new LatencyDeviceSupport(revision > 0, revision, false, true, false);
        Assert.Equal(expected, support.NvPerSubmitAttribution);
        Assert.Equal(3u, LatencyBackendSelector.NvPerSubmitAttributionRevision);
    }

    [Fact]
    public void TheSummaryCarriesTheBackendAndTheRevision()
    {
        string summary = LatencyBackendSelector.Summary(
            LatencyBackendKind.NvLowLatency2,
            new LatencyDeviceSupport(true, 2, false, true, false),
            presentIdEnabled: true);

        Assert.Contains("latency backend nv", summary);
        Assert.Contains("rev 2", summary);
        Assert.Contains("present id ON", summary);
    }

    // ------------------------------------------------------------------ device

    /// <summary>
    /// A contributor that asks for one extension the device really has, one it
    /// certainly does not, and chains a feature struct. It records what it saw so
    /// the test can assert on the refusal as well as on the acceptance.
    /// </summary>
    private sealed class RecordingContributor : IDeviceRequirementContributor
    {
        /// <summary>
        /// Extensions with no dependencies of their own, so enabling one cannot
        /// fail vkCreateDevice for a reason that has nothing to do with the seam.
        /// </summary>
        private static readonly string[] Candidates =
        {
            "VK_KHR_shader_non_semantic_info",
            "VK_EXT_pipeline_creation_feedback",
            "VK_KHR_push_descriptor",
            "VK_EXT_memory_priority",
        };

        public const string AbsentExtension = "VK_OPTIMUM_extension_that_does_not_exist";

        public string Name => "test";
        public string? Requested;
        public bool AbsentRequestRefused;
        public bool SawInstanceStage;
        public bool SawDeviceStage;
        public uint RequestedSpecVersion;

        public void ContributeInstanceExtensions(InstanceRequirements requirements)
        {
            SawInstanceStage = true;
            Assert.False(requirements.Request(AbsentExtension, Name));
            Assert.DoesNotContain(AbsentExtension, requirements.Enabled);
        }

        public void ContributeDeviceRequirements(DeviceRequirements requirements)
        {
            SawDeviceStage = true;
            AbsentRequestRefused = !requirements.Request(AbsentExtension, 0, Name);
            Assert.DoesNotContain(AbsentExtension, requirements.Enabled);
            // A revision nothing can satisfy is refused the same way.
            Assert.False(requirements.Request("VK_KHR_swapchain", uint.MaxValue, Name));

            foreach (string candidate in Candidates)
            {
                if (requirements.IsEnabled(candidate) || !requirements.Has(candidate)) continue;
                RequestedSpecVersion = requirements.SpecVersion(candidate);
                Assert.True(requirements.Request(candidate, 0, Name));
                Requested = candidate;
                break;
            }

            // A feature struct with nothing switched on: harmless to the device,
            // and it proves the chain builder accepts a contributor's struct
            // alongside the colour-write tier's own.
            requirements.ChainFeature(new PhysicalDeviceShaderDrawParametersFeatures
            {
                SType = StructureType.PhysicalDeviceShaderDrawParametersFeatures,
            });
        }
    }

    [SkippableFact]
    public void AContributorsExtensionsReachTheCreatedDeviceAndAMissingOneDoesNotBreakIt()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        var contributor = new RecordingContributor();
        options.RequirementContributors.Add(contributor);

        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason),
            "No usable Vulkan device: " + failureReason);

        using (context)
        {
            Assert.True(contributor.SawInstanceStage);
            Assert.True(contributor.SawDeviceStage);
            Assert.True(contributor.AbsentRequestRefused);
            Assert.DoesNotContain(RecordingContributor.AbsentExtension, context!.EnabledDeviceExtensions);
            Assert.DoesNotContain(RecordingContributor.AbsentExtension, context.EnabledInstanceExtensions);

            if (contributor.Requested != null)
            {
                _output.WriteLine("contributor asked for " + contributor.Requested +
                    " revision " + contributor.RequestedSpecVersion);
                Assert.Contains(contributor.Requested, context.EnabledDeviceExtensions);
            }
            else
            {
                _output.WriteLine("no candidate extension available on this driver; " +
                    "the refusal and chain cases still ran");
            }

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    [SkippableFact]
    public void ContributorsDoNotDisturbTheColourWriteTier()
    {
        Skip.IfNot(VulkanContext.TryCreate(GpuTest.ContextOptions(), out VulkanContext? plain, out string? why),
            "No usable Vulkan device: " + why);

        ColorWriteTier tier;
        bool dynamicBlend;
        using (plain)
        {
            tier = plain!.Capabilities.ColorWriteTier;
            dynamicBlend = plain.Capabilities.DynamicColorBlend;
        }

        VulkanContextOptions options = GpuTest.ContextOptions();
        options.RequirementContributors.Add(new RecordingContributor());
        Assert.True(VulkanContext.TryCreate(options, out VulkanContext? withContributor, out string? failureReason),
            "the device came up without contributors but not with them: " + failureReason);

        using (withContributor)
        {
            _output.WriteLine("colour write tier " + DeviceCaps.Token(tier) +
                " -> " + DeviceCaps.Token(withContributor!.Capabilities.ColorWriteTier));
            Assert.Equal(tier, withContributor.Capabilities.ColorWriteTier);
            Assert.Equal(dynamicBlend, withContributor.Capabilities.DynamicColorBlend);
        }
    }

    [SkippableFact]
    public void TheDeviceReportsWhatTheDriverOffersForLatency()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            VulkanCapabilities capabilities = context!.Capabilities;
            _output.WriteLine(capabilities.DeviceName + " / " + capabilities.DriverName);
            _output.WriteLine("latency support: " + capabilities.LatencySupport);
            _output.WriteLine("per-submit attribution: " + capabilities.LatencySupport.NvPerSubmitAttribution);
            _output.WriteLine("summary: " + capabilities.LatencySummary);
            _output.WriteLine("enabled device extensions: " + string.Join(", ", context.EnabledDeviceExtensions));

            // Detection is recorded either way; a revision is only reported when
            // the extension is really there.
            if (!capabilities.LatencySupport.NvLowLatency2)
            {
                Assert.Equal(0u, capabilities.LatencySupport.NvLowLatency2SpecVersion);
            }
            else
            {
                Assert.True(capabilities.LatencySupport.NvLowLatency2SpecVersion > 0);
            }

            // Headless: no swapchain, so nothing vendor-specific is enabled and
            // the selection lands on Native. Nothing that is not used is enabled.
            Assert.Equal(LatencyBackendKind.Native, capabilities.LatencyBackend);
            Assert.False(capabilities.NvLowLatency2Enabled);
            Assert.False(capabilities.AmdAntiLagEnabled);
            Assert.False(capabilities.PresentIdEnabled);
            Assert.DoesNotContain(LatencyBackendSelector.NvLowLatency2ExtensionName, context.EnabledDeviceExtensions);
            Assert.DoesNotContain(LatencyBackendSelector.AmdAntiLagExtensionName, context.EnabledDeviceExtensions);
            Assert.DoesNotContain(LatencyBackendSelector.PresentIdExtensionName, context.EnabledDeviceExtensions);
            Assert.Null(context.NvLowLatency2);
            Assert.Null(context.AmdAntiLag);
            Assert.Contains("latency backend native", capabilities.LatencySummary);

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    [SkippableFact]
    public void ForcingABackendTheHeadlessDeviceCannotRunDegradesInsteadOfFailing()
    {
        var messages = new List<string>();
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        options.LatencyBackend = LatencyBackendKind.NvLowLatency2;

        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason),
            "No usable Vulkan device: " + failureReason);

        using (context)
        {
            Assert.Equal(LatencyBackendKind.Native, context!.Capabilities.LatencyBackend);
            Assert.False(context.Capabilities.NvLowLatency2Enabled);
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    // ---- the selection reaches the frame (S1 -> S2-S7) ---------------------

    [SkippableFact]
    public void TheDeviceRunsTheBackendItsCapabilitiesSelected()
    {
        // The setting ships on since 2026-09-12, so "off is off" is asked for here.
        using LatencyModeScope mode = LatencyModeScope.Off();
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanCapabilities capabilities = device!.ContextForTests.Capabilities;
            _output.WriteLine(capabilities.LatencySummary);

            // Either the selection is implemented and installed, or it is not
            // implemented yet and the device fell back to None. Nothing else.
            Assert.True(
                device.Latency.Kind == capabilities.LatencyBackend ||
                device.Latency.Kind == LatencyBackendKind.None,
                "selected " + LatencyBackends.Token(capabilities.LatencyBackend) +
                " but installed " + LatencyBackends.Token(device.Latency.Kind));

            // The stats source is the very instance the frame uses, so the line
            // cannot report a backend the frame is not running.
            Assert.Same(device.Latency, VulkanStats.LatencySource);

            // With the mode off, whichever backend was installed never takes the
            // client's FPS limiter away.
            Assert.False(device.Latency.OwnsFrameCap);

            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void TheStatsLineNamesTheInstalledBackendAndItsRevision()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            // The revision token comes from the same capability the selection read.
            Assert.Equal(device!.ContextForTests.Capabilities.LatencySupport.NvLowLatency2SpecVersion, VulkanStats.LatencyRevision);

            VulkanStats.SampleIfDue(TimeSpan.Zero);
            string? sample = VulkanStats.SampleIfDue(TimeSpan.Zero);
            Assert.NotNull(sample);

            string latency = LineStartingWith(sample!, "stats.latency ");
            _output.WriteLine(latency);
            Assert.Contains("backend=" + LatencyBackends.Token(device.Latency.Kind), latency);
            Assert.Contains(
                "rev=" + device.ContextForTests.Capabilities.LatencySupport.NvLowLatency2SpecVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                latency);
            Assert.Contains("mode=" + VulkanStats.ModeToken(device.Latency.Settings.Mode), latency);

            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void DisposingTheDeviceLeavesNoStaleBackendBehindTheStatsLine()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        device!.Dispose();

        Assert.Null(VulkanStats.LatencySource);
        Assert.Equal(0u, VulkanStats.LatencyRevision);
    }

    /// <summary>Present ids may only be chained where the feature was enabled.</summary>
    [SkippableFact]
    public void PresentIdsAreOnlyChainedWhenTheFeatureWasEnabled()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            // A headless device has no swapchain at all, so the only thing to
            // assert here is the capability the wiring copies from: it is false
            // unless a vendor backend that needs it was selected on a presentable
            // device, and the swapchain copies exactly this value.
            Assert.False(device!.ContextForTests.Capabilities.PresentIdEnabled);
            GpuTest.AssertClean(device);
        }
    }

    private static string LineStartingWith(string sample, string prefix)
    {
        foreach (string line in sample.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        }

        throw new Xunit.Sdk.XunitException("no line starting with '" + prefix + "' in:\n" + sample);
    }

    // ---- off is off, on a real device --------------------------------------

    [SkippableFact]
    public void AFreshDeviceRunsTheNoneBackendAndNeverPacesTheFrame()
    {
        // The setting ships on since 2026-09-12, so "off is off" asks for off.
        using LatencyModeScope mode = LatencyModeScope.Off();
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            ILatencyBackend latency = device!.Latency;
            _output.WriteLine("latency backend: " + LatencyBackends.Token(latency.Kind));

            // With the mode off the installed backend - None, or the Native
            // one auto-selection lands on where no vendor path exists - is disabled:
            // it sleeps nowhere and owns no frame cap. "Off is off" is that, not the
            // identity of the instance.
            Assert.False(latency.OwnsFrameCap);
            Assert.Equal(LatencyMode.Off, latency.Settings.Mode);
            Assert.Equal(0UL, latency.Settings.MinimumIntervalUs);

            for (ulong frame = 1; frame <= 3; frame++)
            {
                // The frame as the seams will drive it (ILatencyBackend's call map).
                Assert.Equal(0UL, latency.Sleep(frame));
                latency.Marker(frame, LatencyMarker.InputSample);
                latency.Marker(frame, LatencyMarker.SimulationStart);

                device.BeginFrame();
                latency.Marker(frame, LatencyMarker.SimulationEnd);
                latency.Marker(frame, LatencyMarker.RenderSubmitStart);
                latency.Marker(frame, LatencyMarker.RenderSubmitEnd);

                latency.Marker(frame, LatencyMarker.PresentStart);
                // Headless: Present submits the frame, there is no swapchain.
                device.Present();
                latency.Marker(frame, LatencyMarker.PresentEnd);
                latency.OnPresent(frame, presentId: frame);
            }

            LatencyFrameReport[] reports = latency.TakeReports();
            Assert.Equal(3, reports.Length);
            for (int i = 0; i < reports.Length; i++)
            {
                LatencyFrameReport report = reports[i];
                _output.WriteLine($"frame {report.FrameId} present {report.PresentId}: " +
                    $"input {report.InputUs}us sim {report.SimulationUs}us submit {report.RenderSubmitUs}us " +
                    $"present {report.PresentUs}us total {report.TotalUs}us");
                Assert.Equal((ulong)(i + 1), report.FrameId);
                Assert.Equal((ulong)(i + 1), report.PresentId);
                // Real clock, real frame: the whole frame took some measurable time,
                // and a CPU-timestamp backend reports nothing about the driver.
                Assert.True(report.TotalUs > 0, "the report's total must come off the clock");
                Assert.Equal(0UL, report.DriverUs);
                Assert.Equal(0UL, report.OsRenderQueueUs);
                Assert.Equal(0UL, report.GpuUs);
            }
            // Drained: the stats sample sees each frame exactly once.
            Assert.Empty(latency.TakeReports());

            GpuTest.AssertClean(device);
        }
    }

    /// <summary>
    /// The decision of 2026-09-12: latency reduction ships on, on every GPU. A device
    /// that comes up with the shipped setting untouched therefore has an enabled
    /// backend that owns the client's frame cap, and the cap the lib hands over
    /// through <c>SetLatencyFrameCap</c> reaches it as the matching minimum interval -
    /// including the small background-window cap, which is the number an unfocused
    /// window would otherwise stop being paced by.
    /// </summary>
    [SkippableFact]
    public void TheShippedDefaultPacesTheFrameAndTakesTheClientsFrameCap()
    {
        using LatencyModeScope mode = LatencyModeScope.Of(LatencyModeScope.ShippedDefault);
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            ILatencyBackend latency = device!.Latency;
            _output.WriteLine("latency backend: " + LatencyBackends.Token(latency.Kind) +
                " mode " + latency.Settings.Mode);

            // On by default means: a real backend, enabled, owning the frame cap.
            Assert.NotEqual(LatencyBackendKind.None, latency.Kind);
            Assert.Equal(LatencyMode.On, latency.Settings.Mode);
            Assert.True(latency.OwnsFrameCap);
            // The device installs no cap of its own; the client's is the only source.
            Assert.Equal(0UL, latency.Settings.MinimumIntervalUs);

            var platform = new Platform.VulkanClientPlatform(null!);
            platform.LatencyBackendOverride = latency;

            // The background-window cap (OptimumBgMaxFps = 30 after sustained focus loss).
            platform.SetLatencyFrameCap(30);
            Assert.Equal(33333UL, latency.Settings.MinimumIntervalUs);
            Assert.Equal(30u, latency.Settings.MaxFps);
            Assert.Equal(LatencyMode.On, latency.Settings.Mode);

            // Repeating it changes nothing, so nothing is re-applied to the driver.
            LatencySettings unchanged = latency.Settings;
            for (int frame = 0; frame < 4; frame++) platform.SetLatencyFrameCap(30);
            Assert.Equal(unchanged, latency.Settings);

            // Focused again: the foreground cap, and then uncapped.
            platform.SetLatencyFrameCap(144);
            Assert.Equal(LatencySettings.IntervalUsForFps(144), latency.Settings.MinimumIntervalUs);
            platform.SetLatencyFrameCap(0);
            Assert.Equal(0UL, latency.Settings.MinimumIntervalUs);

            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void TheForcedBackendOptionReachesTheContextOptions()
    {
        using LatencyModeScope mode = LatencyModeScope.Off();
        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
        LatencyBackendKind? seen = null;
        device.ConfigureContextOptions = options =>
        {
            suite?.Invoke(options);
            options.LatencyBackend = LatencyBackendKind.Native;
            seen = options.LatencyBackend;
        };

        if (!device.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device.Dispose();
            Skip.If(true, "No usable Vulkan device: " + failureReason);
        }

        using (device)
        {
            Assert.Equal(LatencyBackendKind.Native, seen);
            // Whatever was installed, with the mode off it paces nothing.
            Assert.False(device.Latency.OwnsFrameCap);
            GpuTest.AssertClean(device);
        }
    }

    // ---- every forced kind is constructible, or degrades down the ladder ----

    /// <summary>
    /// Every kind the env override (<c>OPTIMUM_VULKAN_LATENCY</c>) can name
    /// reaches the device and comes back as either that kind or a kind on its
    /// degrade ladder. None is on every ladder, so the assertion has teeth only
    /// because it also asserts the ladder never skips a rung upwards.
    /// </summary>
    [SkippableTheory]
    [InlineData(0)] // None
    [InlineData(1)] // Native
    [InlineData(2)] // NvLowLatency2
    [InlineData(3)] // AmdAntiLag
    public void EveryForcedBackendIsConstructibleOrDegradesDownTheLadder(int forcedKind)
    {
        // The setting ships on since 2026-09-12; this test is about the ladder, so it
        // pins the mode off and keeps its "takes no FPS limiter away" assertion.
        using LatencyModeScope mode = LatencyModeScope.Off();
        // LatencyBackendKind is internal, so the theory data is its numeric value.
        var forced = (LatencyBackendKind)forcedKind;
        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
        device.ConfigureContextOptions = options =>
        {
            suite?.Invoke(options);
            options.LatencyBackend = forced;
        };

        if (!device.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device.Dispose();
            Skip.If(true, "Vulkan unavailable: " + failureReason);
        }

        using (device)
        {
            LatencyBackendKind installed = device.Latency.Kind;
            _output.WriteLine("forced " + LatencyBackends.Token(forced) +
                " -> selected " + LatencyBackends.Token(device.ContextForTests.Capabilities.LatencyBackend) +
                " -> installed " + LatencyBackends.Token(installed) +
                " (" + device.ContextForTests.Capabilities.LatencySummary + ")");

            Assert.Contains(installed, Ladder(forced));

            // Forcing off means off: no other kind is reachable from None.
            if (forced == LatencyBackendKind.None) Assert.Equal(LatencyBackendKind.None, installed);

            // The instance the frame runs on is the instance the stats line and
            // the frame ring were handed - the merge must not have split them.
            Assert.Same(device.Latency, VulkanStats.LatencySource);

            // With the mode off, no installed backend takes the client's FPS
            // limiter away, whichever kind it turned out to be.
            Assert.False(device.Latency.OwnsFrameCap);

            GpuTest.AssertClean(device);
        }
    }

    /// <summary>
    /// Auto selection is the plan's order: NV where the device really offers it,
    /// then AMD, then Native, and never None (only an explicit "off" yields None).
    /// Asserted against what this device advertises, so it holds on any hardware.
    /// </summary>
    [SkippableFact]
    public void AutoSelectionTakesNvThenAmdThenNativeAndNeverNone()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            LatencyDeviceSupport support = device!.ContextForTests.Capabilities.LatencySupport;
            _output.WriteLine(support.ToString());

            LatencyBackendKind expected = support.NvUsable
                ? LatencyBackendKind.NvLowLatency2
                : support.AmdUsable ? LatencyBackendKind.AmdAntiLag : LatencyBackendKind.Native;

            Assert.Equal(expected, LatencyBackends.SelectBackend(support.NvUsable, support.AmdUsable, null));
            Assert.NotEqual(LatencyBackendKind.None, LatencyBackends.SelectBackend(false, false, null));

            // And the table the present-path filter walks is that same order.
            Assert.Equal(
                new[]
                {
                    LatencyBackendKind.NvLowLatency2,
                    LatencyBackendKind.AmdAntiLag,
                    LatencyBackendKind.Native,
                    LatencyBackendKind.None,
                },
                LatencyBackendSelector.AllowedBackends(LatencyPresentPath.BlitFromOwned));

            GpuTest.AssertClean(device);
        }
    }

    /// <summary>The kinds a forced kind may legitimately end up as, best first.</summary>
    private static List<LatencyBackendKind> Ladder(LatencyBackendKind forced)
    {
        var rungs = new List<LatencyBackendKind> { forced };
        LatencyBackendKind rung = forced;
        while (rung != LatencyBackendKind.None)
        {
            rung = LatencyBackendSelector.Degrade(rung);
            rungs.Add(rung);
        }

        return rungs;
    }
}
