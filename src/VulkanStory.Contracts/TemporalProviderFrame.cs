namespace VulkanStory.Contracts;

/// <summary>
/// Current rendered frame data consumed by reconstruction and frame generation.
/// The game adapter captures this after camera/jitter setup, and owns the inverse
/// view storage until every provider evaluation for this frame has returned.
/// </summary>
public readonly record struct TemporalProviderFrame(
    float JitterX, float JitterY, bool Reset, float DeltaTimeMs,
    float NearPlane, float FarPlane, float FovRadians,
    float PlayerPositionX, float PlayerPositionY, float PlayerPositionZ,
    ReadOnlyMemory<float> InverseView)
{
    /// <summary>FSR 3 FG needs a 4x4 inverse of the camera's origin view matrix.</summary>
    public bool HasInverseView => InverseView.Length >= 16;
}
