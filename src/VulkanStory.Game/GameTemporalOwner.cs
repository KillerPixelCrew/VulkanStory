using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game;

/// <summary>Session-owned camera history reached by patches on the original client.</summary>
internal sealed class GameTemporalOwner(ClientPlatformWindows platform, VulkanDevice device,
    RendererSettingsState settings)
{
    internal TemporalFrameState State { get; } = new();
    private EntityMotionHistory? entityMotion;
    private StandardMotionHistory? standardMotion;
    private InstanceMotionHistory? instanceMotion;
    internal EntityMotionHistory EntityMotion => entityMotion ??= new EntityMotionHistory(State);
    internal StandardMotionHistory StandardMotion => standardMotion ??= new StandardMotionHistory(State, EntityMotion);
    internal InstanceMotionHistory InstanceMotion => instanceMotion ??= new InstanceMotionHistory(State, EntityMotion);
    internal void SetMotionShaderMode(bool enabled)
    {
        if (EntityMotion.Enabled == enabled) return;
        EntityMotion.Enabled = enabled;
        State.RequestReset(EnumTemporalResetReason.ShaderReload);
    }
    private ClientMain? client;
    internal ClientMain? CurrentClient => client;
    private bool inScene, motionValid;
    private bool motionFailed, opaqueEntered, afterOitEntered;
    private string? motionDrawFailure;
    private float previousFov;
    private int? dimension;
    internal bool InScene => inScene;
    internal string MotionReadiness { get; private set; } = "no scene sampled";
    internal string? SceneSampleFailure => !inScene ? "outside scene" :
        !opaqueEntered ? "opaque stage not reached" :
        !afterOitEntered ? "AfterOIT stage not reached" :
        !State.JitterActive ? "motion-write window closed" :
        !State.WasViewCaptured(EnumTemporalView.World) ? "world projection not captured" :
        State.RenderedFrameId != device.LatencyFrameId ? "temporal frame identity mismatch" : null;
    internal bool HasCurrentSceneSample => inScene && opaqueEntered && afterOitEntered &&
        State.JitterActive && State.WasViewCaptured(EnumTemporalView.World) &&
        State.RenderedFrameId == device.LatencyFrameId;

    /// <summary>Begins scene ownership for a matching original client and requests history reset when client identity changes.</summary>
    /// <param name="value">Client associated with this owner platform.</param>
    /// <remarks>Recursive scene entry and mismatched platforms are rejected.</remarks>
    internal void EnterScene(ClientMain value)
    {
        if (inScene) throw new InvalidOperationException("Recursive world render loop.");
        if (!ReferenceEquals(value.Platform, platform)) throw new InvalidOperationException("Temporal client has another platform.");
        if (!ReferenceEquals(client, value))
        {
            client = value;
            State.RequestReset(EnumTemporalResetReason.WorldLoad);
        }
        inScene = true;
        motionValid = false;
        MotionReadiness = "scene producers pending";
        motionFailed = opaqueEntered = afterOitEntered = false;
        motionDrawFailure = null;
    }
    /// <summary>Advances camera/warp history and temporal frame identity at the scene Before stage.</summary>
    /// <param name="dt">Original render delta in seconds, converted to milliseconds for provider timing.</param>
    /// <remarks>Requires scene entry and a current client; captures FOV/dimension changes as reset reasons.</remarks>
    internal void Begin(float dt)
    {
        if (!inScene || client == null) throw new InvalidOperationException("Temporal advance is outside the world render loop.");
        var targets = platform.FrameBuffers;
        FrameBufferRef? primary = targets is { Count: > 0 } ? targets[0] : null;
        bool temporal = settings.EffectiveTaa || settings.UpscalerReplacesTaa || settings.Settings.FrameGeneration != "off";
        bool jitter = settings.EffectiveTaa || settings.UpscalerReplacesTaa;
        if (previousFov != 0 && previousFov != client.MainCamera.Fov)
            State.RequestReset(EnumTemporalResetReason.FovChange);
        previousFov = client.MainCamera.Fov;
        int currentDimension = client.EntityPlayer?.Pos.Dimension ?? 0;
        if (dimension.HasValue && dimension.Value != currentDimension)
            State.RequestReset(EnumTemporalResetReason.Dimension);
        dimension = currentDimension;
        float scale = settings.ActiveRenderScale > 0 ? settings.ActiveRenderScale : settings.Settings.RenderScale;
        State.AdvanceForFrame(device.LatencyFrameId, dt * 1000f, primary?.Width ?? client.Width,
            primary?.Height ?? client.Height, scale, client.MainCamera.ZNear, client.MainCamera.ZFar,
            client.MainCamera.Fov, client.shUniforms, temporal || settings.Settings.TaaJitterDev,
            jitter || settings.Settings.TaaJitterDev);
    }
    /// <summary>Records unjittered world camera, camera position and projection for the current scene.</summary>
    /// <param name="projection">Original 16-element column-major world projection.</param>
    internal void CaptureWorld(double[] projection)
    {
        if (!inScene || client == null) throw new InvalidOperationException("World camera capture is outside the scene.");
        State.CaptureCamera(client.MainCamera.CameraMatrix, client.MainCamera.CameraMatrixOrigin);
        State.CaptureCameraPosition(client.EntityPlayer?.CameraPos, client.shUniforms);
        State.RecordProjection(EnumTemporalView.World, projection);
    }
    /// <summary>Classifies the original projection as world or hand view and records it for the matching client.</summary>
    /// <param name="value">Client whose projection changed.</param>
    /// <param name="fov">FOV passed by the original projection call.</param>
    /// <param name="projection">Unjittered original projection matrix.</param>
    internal void RecordProjection(ClientMain value, float fov, double[] projection)
    {
        if (!ReferenceEquals(client, value)) return;
        State.RecordProjection(fov != value.MainCamera.Fov ? EnumTemporalView.Hand : EnumTemporalView.World, projection);
    }
    internal void CloseJitter() => State.JitterActive = false;
    /// <summary>Clears scene and camera ownership only when the departing client matches the current one.</summary>
    /// <param name="value">Client leaving or being disposed.</param>
    internal void DetachClient(ClientMain value)
    {
        if (!ReferenceEquals(client, value)) return;
        client = null;
        inScene = motionValid = false;
        MotionReadiness = "world detached";
        previousFov = 0;
        dimension = null;
        State.JitterActive = false;
        State.RequestReset(EnumTemporalResetReason.WorldLoad);
    }
    /// <summary>Closes the scene bracket and invalidates motion/history after an original scene failure.</summary>
    /// <param name="failure">Original exception, or null after a successful scene.</param>
    internal void ExitScene(Exception? failure)
    {
        inScene = false;
        if (failure != null)
        {
            motionValid = false;
            MotionReadiness = "scene render failed: " + failure.Message;
            State.JitterActive = false;
            State.RequestReset(EnumTemporalResetReason.CameraHistoryLost);
        }
    }
    // Called only after the dedicated scene's complete motion producer chain.
    // Camera capture alone cannot certify terrain/entities/liquids/sky motion.
    internal void PublishMotionCoverage(bool complete)
    {
        motionValid = complete;
        MotionReadiness = complete ? "ready" : "motion coverage reset";
    }
    internal void NoteSceneStage(EnumRenderStage stage)
    {
        if (!inScene) return;
        if (stage == EnumRenderStage.Opaque) opaqueEntered = true;
        if (stage == EnumRenderStage.AfterOIT) afterOitEntered = true;
    }
    /// <summary>Marks the current scene motion chain incomplete and retains its first rejection reason.</summary>
    /// <param name="reason">Optional producer rejection detail.</param>
    internal void RejectMotionDraw(string? reason = null)
    {
        if (!inScene) return;
        motionFailed = true; motionValid = false;
        motionDrawFailure ??= reason == null ? "scene producer rejected a motion draw" :
            "scene producer rejected a motion draw: " + reason;
        MotionReadiness = motionDrawFailure;
    }
    /// <summary>Publishes motion eligibility only after matching camera/stage and all producer-tail prerequisites succeed.</summary>
    /// <param name="producerTailComplete">Whether liquid/sky producer-tail coverage completed.</param>
    /// <param name="tailFailure">Specific reason for a declined tail, when known.</param>
    internal void CompleteSceneMotion(bool producerTailComplete, string? tailFailure = null)
    {
        motionValid = HasCurrentSceneSample && settings.EffectiveTemporalPipeline && EntityMotion.Enabled &&
            !motionFailed && producerTailComplete;
        MotionReadiness = motionValid ? "ready" : !settings.EffectiveTemporalPipeline ? "temporal pipeline off" :
            SceneSampleFailure ?? (!EntityMotion.Enabled ? "compiled scene motion disabled" :
                motionFailed ? motionDrawFailure ?? "scene producer rejected a motion draw" :
                tailFailure ?? "motion producer tail incomplete");
    }
    /// <summary>Requests a toggle reset and withdraws current motion eligibility before a settings/provider transition.</summary>
    internal void RequestReset()
    {
        State.RequestReset(EnumTemporalResetReason.Toggle);
        motionValid = false;
        MotionReadiness = "temporal reset requested";
        if (inScene) motionFailed = true;
    }
    /// <summary>Returns current camera/provider inputs and complete motion eligibility for this temporal frame.</summary>
    /// <returns>A borrowed camera snapshot; pause or incomplete motion prevents generation.</returns>
    /// <remarks>
    /// Matrix storage remains owned by the temporal state and is updated on later frame advance.
    /// A history reset does not prevent generation: frame generation passes the frame's provider
    /// reset flag on, so providers discard history at the cut instead of skipping it.
    /// </remarks>
    internal GameTemporalFrame Snapshot() => State.Snapshot(motionValid,
        client != null && !client.IsPaused && motionValid);
}
