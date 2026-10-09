using System.Diagnostics;

namespace VulkanStory.Game;

/// <summary>Which temporal reconstruction must have composed the scene before a held world is shown.</summary>
internal enum CompositionReconstruction
{
    /// <summary>No reconstruction is in effect (or the temporal pipeline cannot run); none is awaited.</summary>
    None,
    /// <summary>Native TAA resolves the scene; awaited through the frame's resolve.</summary>
    Taa,
    /// <summary>A super-resolution provider replaces TAA; awaited through its upscaled composite.</summary>
    Upscaler,
}

/// <summary>One composed world frame as the composition gate reads it, sampled after frame generation and before presentation.</summary>
/// <param name="WorldRendered">Whether the world scene rendered this frame.</param>
/// <param name="PipelineSkips">Draws this frame skipped because their pipeline was still compiling.</param>
/// <param name="PendingPipelines">Pipeline keys a draw or native request is still waiting for (prewarm backlog excluded).</param>
/// <param name="TemporalRequired">Whether the temporal pipeline is in effect and its compiled motion producers are enabled.</param>
/// <param name="MotionValid">Whether this frame's complete scene motion chain was accepted.</param>
/// <param name="MotionReadiness">The temporal owner's motion status, reported when motion blocks.</param>
/// <param name="Reconstruction">The reconstruction this frame must have composed.</param>
/// <param name="Reconstructed">Whether that reconstruction composed this frame.</param>
/// <param name="FrameGenerationRequired">Whether an effective (not failed) frame-generation provider must be ready; false while paused.</param>
/// <param name="FrameGenerationReady">Whether frame generation passed every check up to enabling, or waits on a configuration time cannot change.</param>
/// <param name="FrameGenerationStatus">The provider's preparation status, reported when generation blocks.</param>
/// <param name="JitterPhases">Jitter phases of the current render scale; reconstruction must cover them before the world shows.</param>
internal readonly record struct CompositionSample(
    bool WorldRendered, long PipelineSkips, int PendingPipelines,
    bool TemporalRequired, bool MotionValid, string MotionReadiness,
    CompositionReconstruction Reconstruction, bool Reconstructed,
    bool FrameGenerationRequired, bool FrameGenerationReady, string FrameGenerationStatus,
    int JitterPhases);

/// <summary>
/// The composition gate after world entry: decides, frame by frame, whether the session keeps the
/// last non-world image on screen while the world renders hidden behind it.
/// </summary>
/// <remarks>
/// <para>
/// Without it the first seconds of a world show the renderer assembling itself - draws missing
/// while their pipelines compile, the jittered scene before TAA has motion and history, then
/// reconstruction, then frame generation - because each stage only starts once the previous one
/// is valid. The gate arms when a new world client appears and opens when, for consecutive world
/// frames, no draw waited on a pipeline, scene motion was complete, the requested reconstruction
/// composed (for at least one full jitter cycle) and requested frame generation was ready to
/// enable. A hard wall-clock timeout from the first hidden world frame opens it regardless and
/// logs the unmet condition, so a stage that never becomes ready costs a delay, not the world.
/// </para>
/// <para>
/// Failed or unavailable providers do not block: the session samples the effective provider, which
/// is off after a refusal. A paused client does not wait for frame generation. The headless
/// harness bypasses the gate unless <c>VULKANSTORY_HEADLESS_COMPOSITION_HOLD=1</c> opts a
/// validation run in; its captures read the composed frame before any hold either way.
/// </para>
/// <para>
/// The hold hides only presentation: the game keeps receiving input and simulating the world
/// behind the held image. The timeout bounds how long that input acts unseen.
/// </para>
/// </remarks>
internal sealed class CompositionReadiness(bool enabled, Action<string> notification, Action<string> warning)
{
    /// <summary>Wall-clock bound on one hold, counted from its first hidden world frame.</summary>
    internal static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Consecutive composed frames required when no reconstruction is awaited.</summary>
    internal const int StableFrames = 3;
    /// <summary>Minimum consecutive reconstructed frames, raised to the jitter phase count for a reduced render scale.</summary>
    internal const int MinimumReconstructedFrames = 8;

    private object? armedClient;
    private bool holding;
    private long firstWorldFrame;
    private int readyFrames, worldFrames;
    private string unmet = "no world frame rendered";

    /// <summary>Whether the gate can arm at all; false for the headless harness unless it opted in.</summary>
    internal bool Enabled => enabled;
    /// <summary>Whether the session presents the held image instead of this world frame.</summary>
    internal bool Holding => holding;

    /// <summary>Arms the gate for a world client it has not seen yet.</summary>
    /// <param name="client">Original world client identity; repeated calls for the armed one are ignored.</param>
    /// <returns>True when this call armed a new hold.</returns>
    internal bool Arm(object client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!enabled || ReferenceEquals(client, armedClient)) return false;
        armedClient = client;
        holding = true;
        firstWorldFrame = 0;
        readyFrames = worldFrames = 0;
        unmet = "no world frame rendered";
        notification("VulkanStory: holding the loading image until world composition is ready");
        return true;
    }

    /// <summary>Opens the gate without waiting when the armed world leaves; that client never re-arms it.</summary>
    /// <param name="client">Departing original world client identity.</param>
    internal void Disarm(object client)
    {
        if (!ReferenceEquals(client, armedClient) || !holding) return;
        holding = false;
        notification("VulkanStory: world left before its composition was ready (" + unmet + ")");
    }

    /// <summary>Records one composed frame and decides whether the next one is still held.</summary>
    /// <param name="sample">This frame's readiness, sampled before presentation.</param>
    /// <returns>True while the gate still holds; false once it opened (on this frame or before).</returns>
    internal bool Observe(in CompositionSample sample)
    {
        if (!holding) return false;
        if (sample.WorldRendered)
        {
            worldFrames++;
            if (firstWorldFrame == 0) firstWorldFrame = Stopwatch.GetTimestamp();
        }
        string? blocker = Blocker(sample);
        readyFrames = blocker == null ? readyFrames + 1 : 0;
        int required = sample.Reconstruction == CompositionReconstruction.None ? StableFrames :
            Math.Max(MinimumReconstructedFrames, Math.Max(StableFrames, sample.JitterPhases));
        unmet = blocker ?? readyFrames + "/" + required + " consecutive composed frames";
        if (blocker == null && readyFrames >= required)
        {
            holding = false;
            notification("VulkanStory: world composition ready after " + worldFrames + " frames (" +
                (int)Stopwatch.GetElapsedTime(firstWorldFrame).TotalMilliseconds + " ms); releasing the held loading image");
            return false;
        }
        if (firstWorldFrame != 0 && Stopwatch.GetElapsedTime(firstWorldFrame) >= HoldTimeout)
        {
            holding = false;
            warning("VulkanStory: world composition hold timed out after " + (int)HoldTimeout.TotalSeconds +
                " s and " + worldFrames + " frames; showing the world with an unmet condition: " + unmet);
            return false;
        }
        return true;
    }

    /// <summary>The first condition this frame fails, or null when it counts as composed.</summary>
    private static string? Blocker(in CompositionSample sample)
    {
        if (!sample.WorldRendered) return "no world frame rendered";
        if (sample.PipelineSkips > 0) return sample.PipelineSkips + " draws skipped while their pipelines compile";
        if (sample.PendingPipelines > 0) return sample.PendingPipelines + " requested pipelines still compiling";
        if (!sample.TemporalRequired) return null;
        if (!sample.MotionValid) return "scene motion incomplete: " + sample.MotionReadiness;
        if (sample.Reconstruction == CompositionReconstruction.Upscaler && !sample.Reconstructed)
            return "the upscaled composite was not produced";
        if (sample.Reconstruction == CompositionReconstruction.Taa && !sample.Reconstructed)
            return "the TAA resolve was not produced";
        if (sample.FrameGenerationRequired && !sample.FrameGenerationReady)
            return "frame generation not ready: " + sample.FrameGenerationStatus;
        return null;
    }
}
