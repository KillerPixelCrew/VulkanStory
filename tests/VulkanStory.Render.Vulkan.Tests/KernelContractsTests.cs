using System.Diagnostics;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.Present;
using Silk.NET.Vulkan;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

// Expectations carried from FramePlanningTests, FrameGenerationBoundaryTests,
// DeviceValidationTests, and UpscalerDeviceRequirementsTests in the source tree.
/// <summary>Checks retained CPU planning, device requirement, pacing target, and latency-report contracts.</summary>
/// <remarks>No native provider, GPU work, or physical input is executed.</remarks>
public sealed class KernelContractsTests
{
    [Fact]
    public void InitialTransientWritesDiscardButPersistentTargetsLoad()
    {
        static PassSignature Pass(int target, bool transient) => new()
        {
            NameId = target, Width = 32, Height = 24, FormatsId = 1,
            Attachments = [new AttachmentUse(target, ResourceUsage.ColorWrite, transient)]
        };

        FramePlan plan = FramePlan.Build([Pass(1, true), Pass(2, false)]);
        Assert.Equal(AttachmentLoadOp.DontCare, plan.LoadOp(0, 0));
        Assert.Equal(AttachmentLoadOp.Load, plan.LoadOp(1, 0));
    }

    [Theory]
    [InlineData(ResourceUsage.StorageReadCompute)]
    [InlineData(ResourceUsage.StorageWrite)]
    [InlineData(ResourceUsage.StorageReadWrite)]
    public void ComputeUsesOrderPreviousWritesEvenInTheSameStage(ResourceUsage next)
    {
        var state = new ResourceStateTracker(1, 1, false);
        var transitions = new List<ImageTransition>();
        state.Require(0, 1, 0, 1, ResourceUsage.StorageWrite, false, transitions);
        transitions.Clear();
        state.Require(0, 1, 0, 1, next, false, transitions);
        BarrierSides barrier = Assert.Single(transitions).Sides;
        Assert.Equal(ImageLayout.General, barrier.OldLayout);
        Assert.Equal(ImageLayout.General, barrier.NewLayout);
        Assert.True((barrier.SrcAccess & AccessFlags2.ShaderStorageWriteBit) != 0);
        Assert.True((barrier.DstStage & PipelineStageFlags2.ComputeShaderBit) != 0);
    }

    [Fact]
    public void ComputeMipDispatchCoversOddExtentsAndRejectsFeedback()
    {
        var pass = new ComputePassDeclaration
        {
            Bindings = [new ComputeBinding(0, 1, ComputeAccess.Sampled),
                new ComputeBinding(1, 1, ComputeAccess.StorageWrite, 1)],
            Dispatches = [ComputeDispatch.Covering(1)]
        };
        static ComputeImageInfo? Image(int _) => new(35, 19, 4, 1);
        Assert.Null(ComputePassPlanner.Validate(pass, Image));
        Assert.Equal((3u, 2u, 1u),
            ComputePassPlanner.Groups(pass.Dispatches[0], pass, Image, 8, 8));
        pass.Bindings[1] = pass.Bindings[1] with { BaseMip = 0 };
        Assert.NotNull(ComputePassPlanner.Validate(pass, Image));
    }

    [Fact]
    public void OptionalUnavailableInstanceExtensionDoesNotEnterEnabledSet()
    {
        var enabled = new List<string>();
        var requirements = new InstanceRequirements(
            new HashSet<string>(["VK_KHR_surface"], StringComparer.Ordinal), enabled);
        Assert.False(requirements.Request("VK_VULKANSTORY_missing", "test-provider"));
        Assert.True(requirements.Request("VK_KHR_surface", "test-provider"));
        Assert.Equal(new[] { "VK_KHR_surface" }, enabled);
    }

    [Fact]
    public void NativeRuntimeLookupRejectsPathsOutsideItsPrivateDirectory()
    {
        Assert.Throws<ArgumentException>(() => NativeRuntimePaths.DirectoryContaining("../other.dll"));
    }

    [Fact]
    public void ColorWriteFallbackNeverRequiresAnUnsupportedTier()
    {
        Assert.Equal(ColorWriteTier.PipelineKey,
            DeviceCaps.SelectColorWriteTier(false, false,
                DeviceCaps.ParseColorWriteTier("enable")));
        Assert.Equal(ColorWriteTier.DynamicMask,
            DeviceCaps.SelectColorWriteTier(false, true,
                DeviceCaps.ParseColorWriteTier(" MASK ")));
        Assert.Equal("VULKANSTORY_VULKAN_COLOR_WRITE_TIER", DeviceCaps.ColorWriteTierVariable);
    }

    [Fact]
    public void GeneratedRealPresentTargetsHalfARenderInterval()
    {
        long clock = Stopwatch.Frequency;
        var pacer = new GeneratedFramePacer(() => clock, _ => { });
        pacer.NoteGeneratedPresent();
        Assert.InRange(pacer.PendingRealTargetTicks - clock,
            Stopwatch.Frequency / 130, Stopwatch.Frequency / 110);
        clock += Stopwatch.Frequency / 60;
        pacer.NoteGeneratedPresent();
        Assert.InRange(pacer.PendingRealTargetTicks - clock,
            Stopwatch.Frequency / 130, Stopwatch.Frequency / 110);
        pacer.Reset();
        Assert.Equal(0, pacer.PendingRealTargetTicks);
    }

    [Fact]
    public void InputAgeAndCpuPhasesKeepTheirFrameBoundary()
    {
        var ages = new SdlInputAgeRecorder();
        ages.Record(1_000_000, 2_000_000);
        ages.Record(1_000_000, 4_000_000);
        SdlInputAgeSample sample = ages.Take();
        Assert.Equal(2, sample.Events);
        Assert.Equal(2.0, sample.MeanMs);
        Assert.Equal(3.0, sample.MaxMs);
        Assert.Equal(0, ages.Take().Events);

        LatencyFrameReport report = LatencyFrameReport.FromCpuTimestamps(
            11, 15, 1_200, 1_000, 2_000, 2_100, 2_800, 3_000, 3_500);
        Assert.Equal(11UL, report.FrameId);
        Assert.Equal(15UL, report.PresentId);
        Assert.Equal(200UL, report.InputUs);
        Assert.Equal(1_000UL, report.SimulationUs);
        Assert.Equal(700UL, report.RenderSubmitUs);
        Assert.Equal(500UL, report.PresentUs);
        Assert.Equal(2_500UL, report.TotalUs);
    }
}
