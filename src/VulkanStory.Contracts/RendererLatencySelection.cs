namespace VulkanStory.Contracts;

/// <summary>Immutable host selection read by the retained latency path.</summary>
public sealed record RendererLatencySelection(string RequestedMode, string EffectiveFrameGeneration)
{
    // Preserve the existing product rule: active FG requires low latency even
    // when the user's stored selection is Off. Native SDK calls still gate on availability.
    public int EffectiveMode => RequestedMode switch
    {
        "off" when EffectiveFrameGeneration is not ("dlss" or "fsr3" or "xess") => 0,
        "boost" => 2,
        _ => 1,
    };
}

public interface ILatencyStageListener
{
    void OnFrameRenderStart();
}
