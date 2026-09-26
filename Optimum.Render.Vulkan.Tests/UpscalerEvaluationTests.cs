using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class UpscalerEvaluationTests
{
    private static readonly UpscalerPlan Plan = new(960, 540, 1920, 1080, "quality");
    private static readonly UpscalerFrame Frame = new(1, 2, 3, 4, null!);

    [Fact]
    public void ProviderExceptionInvokesFallback()
    {
        var backend = new FakeBackend { Throw = true };
        string? reason = null;

        Assert.False(VulkanClientPlatform.TryEvaluateUpscaler(backend, Plan, Frame,
            failure => reason = failure));
        Assert.Contains("xess evaluation threw: allocation failed", reason);
    }

    [Fact]
    public void ProviderFailureUsesItsDiagnostic()
    {
        var backend = new FakeBackend();
        string? reason = null;

        Assert.False(VulkanClientPlatform.TryEvaluateUpscaler(backend, Plan, Frame,
            failure => reason = failure));
        Assert.Equal("provider returned an error", reason);
    }

    [Fact]
    public void SuccessfulEvaluationDoesNotDisableTheProvider()
    {
        var backend = new FakeBackend { Succeed = true };
        bool disabled = false;

        Assert.True(VulkanClientPlatform.TryEvaluateUpscaler(backend, Plan, Frame,
            _ => disabled = true));
        Assert.False(disabled);
    }

    private sealed class FakeBackend : IUpscalerBackend
    {
        public bool Throw { get; init; }
        public bool Succeed { get; init; }
        public string Id => "xess";
        public bool Active => true;
        public string? Unavailable => null;
        public IDeviceRequirementContributor? Requirements => null;
        public bool BringUp(VulkanDevice device, IntPtr instance, IntPtr physicalDevice,
            IntPtr logicalDevice) => true;
        public bool TryPlan(int displayWidth, int displayHeight, string quality,
            out UpscalerPlan plan) { plan = Plan; return true; }
        public bool Evaluate(in UpscalerPlan plan, in UpscalerFrame frame, out string? error)
        {
            if (Throw) throw new InvalidOperationException("allocation failed");
            error = Succeed ? null : "provider returned an error";
            return Succeed;
        }
        public void RetireFeature() { }
        public void Shutdown() { }
        public void Dispose() { }
    }
}
