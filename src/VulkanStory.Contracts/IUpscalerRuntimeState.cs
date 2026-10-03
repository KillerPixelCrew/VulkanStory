namespace VulkanStory.Contracts;

/// <summary>
/// The game/mod-side upscaler selection and session status. Providers receive
/// this interface instead of reading injected game configuration statics.
/// </summary>
public interface IUpscalerRuntimeState
{
    bool DlssRequested { get; }
    string Quality { get; }
    float LodBiasOffset { get; }
    void SetActivePlan(float renderScale, float lodBias);
    void ClearActivePlan();
    /// <summary>Returns true only for the first runtime stand-down.</summary>
    bool DisableAtRuntime();
}
