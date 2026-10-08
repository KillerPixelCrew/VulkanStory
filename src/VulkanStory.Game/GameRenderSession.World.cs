using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    /// <summary>Resets analog state, diagnostic world counters and temporal readiness for a client belonging to this platform.</summary>
    /// <param name="client">Original world client associated with this platform.</param>
    internal void NoteWorldReady(ClientMain client)
    {
        RequireActive();
        if (!ReferenceEquals(client.Platform, platform)) throw new InvalidOperationException("World belongs to another platform.");
        ControllerMovement.Reset();
        diagnosticWorldReady = true;
        diagnosticWorldFrames = 0;
        diagnosticCaptureAttempted = false;
        diagnosticCapture = null;
        diagnosticCaptureFrame = 0;
        Temporal.State.RequestReset(EnumTemporalResetReason.WorldLoad);
        Temporal.PublishMotionCoverage(false);
    }
    /// <summary>Releases matching world controller/temporal state and output readiness while the process window/device survive.</summary>
    /// <param name="client">Departing original client identity.</param>
    internal void NoteWorldLeft(ClientMain client)
    {
        RequireActive();
        // The original disposal prefix may already have detached this world.
        // Still retire its outputs, but never invalidate a newer attached world.
        if (Temporal.CurrentClient != null && !ReferenceEquals(Temporal.CurrentClient, client)) return;
        ReleaseControllerWorld(client);
        AnalogClientConsumerPatches.ClientLeaving(client);
        diagnosticWorldReady = false;
        Temporal.DetachClient(client);
        frameGeneration!.Reset();
        Graphics.TaaHistoryValid = false;
        Graphics.SceneNoHudCaptured = false;
        ClearPresentation();
    }
}
