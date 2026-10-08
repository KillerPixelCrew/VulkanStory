using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using Xunit;

namespace VulkanStory.Render.Vulkan.Tests;

/// <summary>Checks that an unrequested DLSS path leaves host selection unchanged before native preparation.</summary>
/// <remarks>No NGX runtime or DLSS feature is loaded.</remarks>
public sealed class DlssProviderBoundaryTests
{
    /// <summary>Unrequested DLSS host selection recording whether preparation attempts runtime stand-down.</summary>
    private sealed class Selection : IUpscalerRuntimeState
    {
        public bool DlssRequested => false;
        public string Quality => "quality";
        public float LodBiasOffset => 1;
        public int DisableCalls;
        public void SetActivePlan(float renderScale, float lodBias) { }
        public void ClearActivePlan() { }
        public bool DisableAtRuntime() { DisableCalls++; return true; }
    }

    [Fact]
    public void UnrequestedDlssPreparationLeavesSelectionAloneBeforeNativeLoading()
    {
        var selection = new Selection();
        var messages = new List<string>();
        Assert.Null(DlssUpscaler.TryPrepare(selection, "", messages.Add));
        Assert.Equal(0, selection.DisableCalls);
        Assert.Empty(messages);
        using var host = new DlssUpscaler(selection, messages.Add);
        Assert.False(host.Requested);
        Assert.False(host.Active);
        Assert.Equal(0, host.FeaturesCreated);
    }
}
