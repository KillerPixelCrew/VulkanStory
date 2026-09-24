using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class DeviceValidationTests
{
    private readonly ITestOutputHelper _output;
    public DeviceValidationTests(ITestOutputHelper output) => _output = output;

    private VulkanContext Open(List<string> messages)
    {
        var options = GpuTest.ContextOptions(messages);
        options.ValidationFeatures = "sync,best";
        bool created = VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason);
        // An explicitly requested device must fail visibly if it cannot be used.
        if (Environment.GetEnvironmentVariable(GpuTest.DeviceIndexVariable) != null)
            Assert.True(created, reason);
        Skip.IfNot(created, "No usable Vulkan device: " + reason);
        try
        {
            _output.WriteLine($"device={context!.Capabilities.DeviceName}; vendor={context.Capabilities.VendorId:X}; driver={context.Capabilities.DriverVersion}; API={context.Capabilities.ApiVersion}");
            _output.WriteLine("validation=" + context.ValidationSettingsApplied);
            Assert.True(context.ValidationEnabled, "Khronos validation must be installed for GPU acceptance.");
            Assert.DoesNotContain("NOT APPLIED", context.ValidationSettingsApplied);
            Assert.False(string.IsNullOrWhiteSpace(context.ValidationSettingsApplied));
            string? expected = Environment.GetEnvironmentVariable("OPTIMUM_TEST_DEVICE_NAME");
            if (!string.IsNullOrWhiteSpace(expected))
                Assert.Contains(expected, context.Capabilities.DeviceName, StringComparison.OrdinalIgnoreCase);
            return context;
        }
        catch { context!.Dispose(); throw; }
    }

    [SkippableFact]
    public void SelectedDeviceCreatesTheActualSharedPipelineLayout()
    {
        var messages = new List<string>();
        using (var context = Open(messages))
        {
            Assert.Empty(DescriptorIndexingFloor.Missing(context.Capabilities.DescriptorIndexing));
            using var layout = SharedPipelineLayout.CreateStandalone(context);
            Assert.NotEqual(0UL, layout.Layout.Handle);
            Assert.NotEqual(0UL, layout.TextureSetLayout.Handle);
            if (context.CheckpointsAvailable) Assert.NotNull(context.ReadQueueCheckpoints());
        }
        // Include destruction in the validation boundary.
        ValidationAssert.NoErrors(messages);
    }

    [SkippableFact]
    public unsafe void SynchronizationValidationDetectsAnActualMissingImageBarrier()
    {
        var messages = new List<string>();
        using (var context = Open(messages))
        using (var commands = new SetupQueue(context))
        using (var textures = new TextureManager(context, commands.Uploads))
        {
            var source = textures.Get(textures.Create(4, 4, Format.R8G8B8A8Unorm))!;
            var target = textures.Get(textures.Create(4, 4, Format.R8G8B8A8Unorm))!;
            commands.SubmitAndWait(commandBuffer =>
            {
                textures.TransitionTexture(commandBuffer, source, ImageLayout.TransferSrcOptimal);
                textures.TransitionTexture(commandBuffer, target, ImageLayout.TransferDstOptimal);
                var region = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(4, 4, 1),
                };
                for (int i = 0; i < 2; i++)
                    context.Api.CmdCopyImage(commandBuffer, source.Image, ImageLayout.TransferSrcOptimal,
                        target.Image, ImageLayout.TransferDstOptimal, 1, &region);
            });
        }
        var snapshot = ValidationAssert.Snapshot(messages);
        foreach (string message in snapshot) _output.WriteLine(message);
        Assert.Contains(snapshot, m => m.Contains("SYNC-HAZARD-WRITE-AFTER-WRITE", StringComparison.Ordinal));
        // This one deliberate hazard is the control, not an allowance for renderer errors.
        ValidationAssert.NoErrors(snapshot.Where(m => !m.Contains("SYNC-HAZARD-WRITE-AFTER-WRITE", StringComparison.Ordinal)).ToArray());
    }
}

internal static class ValidationAssert
{
    public static List<string> Snapshot(IReadOnlyCollection<string> messages)
    {
        lock (messages) return new List<string>(messages);
    }

    public static bool IsSynchronization(string message) =>
        message.Contains("[SYNC-", StringComparison.Ordinal);

    public static void NoErrors(IReadOnlyCollection<string> messages)
    {
        string[] errors = Snapshot(messages).Where(m =>
            m.StartsWith(VulkanContext.ErrorPrefix, StringComparison.Ordinal) || IsSynchronization(m)).ToArray();
        Assert.True(errors.Length == 0, "validation errors:\n" + string.Join("\n", errors));
    }

    // Retained for existing call sites while the feature suite is replaced.
    public static void NoSyncHazards(IReadOnlyCollection<string> messages, string callerFile = "")
    {
        string[] hazards = Snapshot(messages).Where(IsSynchronization).ToArray();
        Assert.True(hazards.Length == 0, "synchronization hazards:\n" + string.Join("\n", hazards));
    }
}
