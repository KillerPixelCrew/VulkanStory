using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>
/// Camera snapshot captured by the game temporal owner for one rendered frame.
/// Matrices keep the retained column-major, unjittered game convention; the
/// owner keeps their storage alive through post processing and presentation.
/// </summary>
internal readonly record struct GameTemporalFrame(
    ulong FrameId, bool WorldCaptured, bool MotionValid, bool CanGenerate,
    TemporalProviderFrame Provider,
    ReadOnlyMemory<float> View, ReadOnlyMemory<float> Projection,
    ReadOnlyMemory<float> PreviousView, ReadOnlyMemory<float> PreviousProjection)
{
    internal bool HasCamera => WorldCaptured && View.Length == 16 && Projection.Length == 16 &&
        PreviousView.Length == 16 && PreviousProjection.Length == 16;
}
