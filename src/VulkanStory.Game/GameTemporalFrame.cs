using VulkanStory.Contracts;

namespace VulkanStory.Game;

/// <summary>
/// Camera snapshot captured by the game temporal owner for one rendered frame.
/// Matrices keep the retained column-major, unjittered game convention; the
/// owner keeps their storage alive through post processing and presentation.
/// </summary>
/// <param name="FrameId">Device latency-frame identity captured by the temporal owner.</param>
/// <param name="WorldCaptured">Whether this frame supplied a world projection.</param>
/// <param name="MotionValid">Whether the complete current scene motion chain was accepted.</param>
/// <param name="CanGenerate">Whether pause/current-producer conditions allow FG preparation; reset frames still generate with Provider.Reset set.</param>
/// <param name="Provider">Provider-facing dimensions, timing, jitter and clip planes.</param>
/// <param name="View">Current unjittered 16-float column-major world view.</param>
/// <param name="Projection">Current unjittered 16-float column-major world projection.</param>
/// <param name="PreviousView">Previous world view in the same convention.</param>
/// <param name="PreviousProjection">Previous world projection in the same convention.</param>
/// <param name="CameraDeltaX">Current camera X displacement from the previous captured world frame.</param>
/// <param name="CameraDeltaY">Current camera Y displacement from the previous captured world frame.</param>
/// <param name="CameraDeltaZ">Current camera Z displacement from the previous captured world frame.</param>
/// <remarks>Read-only memory refers to temporal-owner arrays, not independent copies; consumers must not retain it beyond the next frame advance.</remarks>
internal readonly record struct GameTemporalFrame(
    ulong FrameId, bool WorldCaptured, bool MotionValid, bool CanGenerate,
    TemporalProviderFrame Provider,
    ReadOnlyMemory<float> View, ReadOnlyMemory<float> Projection,
    ReadOnlyMemory<float> PreviousView, ReadOnlyMemory<float> PreviousProjection,
    float CameraDeltaX = 0f, float CameraDeltaY = 0f, float CameraDeltaZ = 0f)
{
    /// <summary>Whether a world camera was captured and all four matrix views contain exactly 16 floats.</summary>
    internal bool HasCamera => WorldCaptured && View.Length == 16 && Projection.Length == 16 &&
        PreviousView.Length == 16 && PreviousProjection.Length == 16;
}
