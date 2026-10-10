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
        // Usually already armed by the client's first scene; a no-op for the armed client.
        if (composition!.Arm(client)) Graphics.PrepareTemporalPipelines(platform.FrameBuffers, worldLoaded: true);
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
        composition!.Disarm(client);
        frameGeneration!.Reset();
        Graphics.ReleaseTaaSampleTargets();
        Graphics.TaaHistoryValid = false;
        Graphics.SceneNoHudCaptured = false;
        ClearPresentation();
    }
    /// <summary>Arms the composition gate for a new world client and applies its hold to this frame's default image.</summary>
    /// <returns>True while the gate holds, so frame generation stays paused before enabling.</returns>
    /// <remarks>
    /// Runs after the world and UI composed into the default image and the diagnostic captures read
    /// it, before frame generation and presentation. A frame without a world scene (the loading
    /// screen) is presented as composed and becomes the held image; a world frame during the hold
    /// is overwritten with that image, so every present path shows it without a path of its own.
    /// </remarks>
    private bool ApplyCompositionHold()
    {
        CompositionReadiness readiness = composition!;
        if (!readiness.Enabled) return false;
        // A new client's first scene arms the gate in the very frame it rendered hidden.
        if (Temporal.CurrentClient is { } client && readiness.Arm(client))
            Graphics.PrepareTemporalPipelines(platform.FrameBuffers, worldLoaded: true);
        if (Temporal.State.RenderedFrameId != Device.LatencyFrameId)
        {
            // Outside a world (and while held) this is the image a hold keeps on screen. A world
            // frame that skipped its scene once the gate opened needs no copy.
            if (readiness.Holding || Temporal.CurrentClient == null) Device.CaptureHeldFrame();
            return readiness.Holding;
        }
        if (readiness.Holding) Device.OverwriteDefaultWithHeld();
        return readiness.Holding;
    }
    /// <summary>Samples this frame's composition readiness while the gate holds and releases the held image once it opens.</summary>
    /// <param name="pipelineSkipsAtStart">Pipeline-skipped draw count sampled right after this frame's BeginFrame.</param>
    private void ObserveCompositionReadiness(long pipelineSkipsAtStart)
    {
        CompositionReadiness readiness = composition!;
        if (!readiness.Holding) return;
        RendererSettingsState state = services.RendererSettings;
        GameTemporalOwner owner = Temporal;
        RuntimeFrameGeneration generation = frameGeneration!;
        GameTemporalFrame frame = owner.Snapshot();
        // Without offscreen targets, a motion attachment or compiled motion producers no temporal
        // stage can become valid, so none is awaited (the providers report their own refusal).
        bool temporalPipeline = state.EffectiveTemporalPipeline && owner.EntityMotion.Enabled &&
            GameFramebufferBindings.OffscreenEnabled(platform) && Graphics.FrameState.MotionAttachment >= 0;
        CompositionReconstruction reconstruction = !temporalPipeline ? CompositionReconstruction.None :
            state.UpscalerReplacesTaa ? CompositionReconstruction.Upscaler :
            state.EffectiveTaa ? CompositionReconstruction.Taa : CompositionReconstruction.None;
        float scale = state.ActiveRenderScale > 0f ? state.ActiveRenderScale : state.Settings.RenderScale;
        var sample = new CompositionSample(
            WorldRendered: owner.State.RenderedFrameId == Device.LatencyFrameId,
            PipelineSkips: Device.PipelineDrawsSkipped - pipelineSkipsAtStart,
            PendingPipelines: Device.PendingDemandPipelines,
            TemporalRequired: temporalPipeline,
            MotionValid: frame.MotionValid && frame.FrameId == Device.LatencyFrameId,
            MotionReadiness: owner.MotionReadiness,
            Reconstruction: reconstruction,
            Reconstructed: reconstruction == CompositionReconstruction.Upscaler
                ? Graphics.UpscaledCompositeReady : Graphics.TaaResolvedThisFrame,
            // A failed provider is effectively off; a paused client never generates.
            FrameGenerationRequired: generation.EffectiveProvider != "off" && owner.CurrentClient?.IsPaused != true,
            FrameGenerationReady: generation.ReadyToGenerate || generation.ConfigurationBlocked,
            FrameGenerationStatus: generation.PreparationStatus,
            JitterPhases: TemporalMath.JitterPhaseCount(scale > 0f ? 1f / scale : 1f));
        if (!readiness.Observe(sample)) Device.ReleaseHeldFrame();
    }
}
