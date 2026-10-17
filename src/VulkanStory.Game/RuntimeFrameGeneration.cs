using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game;

/// <summary>Produces scene targets and camera constants for ReScaleFrame's FG session.</summary>
internal sealed class RuntimeFrameGeneration : IDisposable, IFrameGenerationInputs, IFrameGenerationSettings
{
    private readonly RendererSettingsState settings;
    private readonly FrameGenerationSession session;
    private GameGraphicsAdapter? graphics;
    private IReadOnlyList<FrameBufferRef>? buffers;
    private GameTemporalFrame frame;
    private static readonly float[] DepthRemap = [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, .5f, 0f, 0f, 0f, .5f, 1f];
    // Owner-thread matrix scratch. TryGetCamera's result borrows four of these
    // arrays; TagStreamlineFrame consumes them synchronously in the same frame.
    private readonly float[] projectionSource = new float[16], cameraProjection = new float[16],
        cameraInverseProjection = new float[16], cameraView = new float[16], cameraInverseView = new float[16],
        cameraViewProjection = new float[16], cameraInverseViewProjection = new float[16],
        cameraPreviousProjection = new float[16], cameraPreviousView = new float[16],
        cameraPreviousViewProjection = new float[16], cameraDelta = new float[16],
        cameraPreviousDeltaViewProjection = new float[16], cameraToPrevious = new float[16],
        cameraFromPrevious = new float[16], xessOrigin = new float[16], xessView = new float[16];

    internal RuntimeFrameGeneration(VulkanDevice device, RendererSettingsState settings,
        Action<string> notification, Action<string> error, Action requestTemporalReset)
    {
        this.settings = settings;
        session = new FrameGenerationSession(device, this, notification, error, requestTemporalReset);
    }
    internal string RequestedProvider => settings.StereoRendering ? "off" : settings.Settings.FrameGeneration;
    string IFrameGenerationSettings.Provider => RequestedProvider;
    int IFrameGenerationSettings.Multiplier => settings.Settings.FrameGenerationMultiplier;
    internal string EffectiveProvider => session.EffectiveProvider;
    internal bool PreparedThisFrame => session.PreparedThisFrame;
    internal bool ReadyToGenerate => session.ReadyToGenerate;
    internal bool ConfigurationBlocked => session.ConfigurationBlocked;
    internal string PreparationStatus => session.PreparationStatus;
    internal uint ConfiguredDlssGeneratedFrames => session.ConfiguredDlssGeneratedFrames;
    internal uint ConfiguredGeneratedFrames => session.ConfiguredGeneratedFrames;
    internal uint ActualDlssPresents => session.ActualDlssPresents;
    internal uint? LastReportedSdkGeneratedLimit => session.LastReportedSdkGeneratedLimit;
    internal StreamlineFrameGenerationState? LastDlssState => session.LastDlssState;
    internal int? LastDlssStateResult => session.LastDlssStateResult;
    internal DlssQueryObservation? HeadlessDlssQueryObservation => session.HeadlessDlssQueryObservation;
    internal string? Unavailable(string provider) => session.Unavailable(provider);
    internal uint? MaximumMultiplier(string provider) => session.MaximumMultiplier(provider);

    internal void Generate(GameGraphicsAdapter graphics, IReadOnlyList<FrameBufferRef> buffers,
        in GameTemporalFrame frame, bool hold = false)
    {
        this.graphics = graphics;
        this.buffers = buffers;
        this.frame = frame;
        try
        {
            session.Generate(new FrameGenerationFrame(frame.FrameId, frame.CanGenerate,
                frame.MotionValid, frame.HasCamera, frame.Provider,
                frame.CameraDeltaX, frame.CameraDeltaY, frame.CameraDeltaZ), this, hold);
        }
        finally { this.graphics = null; this.buffers = null; this.frame = default; }
    }

    private FrameBufferRef? Target(int index) => buffers != null && index >= 0 && index < buffers.Count &&
        buffers[index] is { Disposed: false } target ? target : null;
    private static FrameGenerationTarget Describe(FrameBufferRef? target) => target == null ? default :
        new(target.Width, target.Height, target.DepthTextureId,
            target.ColorTextureIds is { Length: > 0 } colors ? colors[0] : 0, target.ColorTextureIds?.Length ?? 0);
    int IFrameGenerationInputs.MotionAttachment => graphics!.FrameState.MotionAttachment;
    FrameGenerationTarget IFrameGenerationInputs.Primary => Describe(Target(0));
    FrameGenerationTarget IFrameGenerationInputs.Output => Describe(Target(GameGraphicsAdapter.GeneratedFrameIndex));
    FrameGenerationTarget IFrameGenerationInputs.Scene => Describe(graphics!.SceneNoHudCaptured ? Target(graphics.SceneNoHudFramebufferIndex) : null);
    FrameGenerationTarget IFrameGenerationInputs.Ui => Describe(Target(graphics!.UiFramebufferIndex));
    bool IFrameGenerationInputs.TryGetCamera(int width, int height, out NgxFrameGenerationCamera camera) =>
        TryGetCamera(frame, width, height, out camera);
    bool IFrameGenerationInputs.TryPrepareMotion(out int texture, out string reason)
    {
        bool ready = graphics!.PrepareSdkMotion(frame, buffers!);
        texture = graphics.PreparedSdkMotionTexture;
        reason = graphics.SdkMotionReadiness;
        return ready;
    }
    bool IFrameGenerationInputs.ComposeGeneratedFrameUi() =>
        Target(GameGraphicsAdapter.GeneratedFrameIndex) is { } output &&
        Target(graphics!.UiFramebufferIndex) is { } ui && graphics.ComposeGeneratedFrameUi(output, ui);
    unsafe void IFrameGenerationInputs.FillXessCamera(ref XessPresentationFrame constants,
        double originX, double originY, double originZ)
    {
        float[] origin = Mat4f.Identity(xessOrigin);
        origin[12] = -(float)originX; origin[13] = -(float)originY; origin[14] = -(float)originZ;
        frame.View.Span.CopyTo(cameraView);
        ReadOnlySpan<float> view = Mat4f.Mul(xessView, cameraView, origin);
        ReadOnlySpan<float> projection = VulkanProjection(frame.Projection.Span, cameraProjection);
        fixed (float* viewMatrix = constants.ViewMatrix, projectionMatrix = constants.ProjectionMatrix)
        {
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                viewMatrix[row * 4 + column] = view[column * 4 + row];
                projectionMatrix[row * 4 + column] = projection[column * 4 + row];
            }
        }
    }

    /// <summary>Builds Vulkan-depth camera constants and current/previous clip transforms from the borrowed temporal snapshot.</summary>
    /// <param name="frame">Current unjittered column-major world camera/history.</param>
    /// <param name="width">Render width used for camera aspect.</param>
    /// <param name="height">Render height used for camera aspect.</param>
    /// <param name="camera">Validated provider camera on success; default when prerequisites or inversions fail.</param>
    /// <returns>True when the camera data and every required inverse are valid.</returns>
    /// <remarks>The camera borrows this instance's matrix scratch; it is valid until the next camera or XeSS preparation.</remarks>
    internal bool TryGetCamera(in GameTemporalFrame frame, int width, int height,
        out NgxFrameGenerationCamera camera)
    {
        camera = default;
        if (!frame.HasCamera || width <= 0 || height <= 0) return false;
        // Same GL [-w,w] to Vulkan [0,w] clip-depth remap as the shader rewriter.
        float[] projection = VulkanProjection(frame.Projection.Span, cameraProjection);
        float[] inverseProjection = Mat4f.Invert(cameraInverseProjection, projection);
        frame.View.Span.CopyTo(cameraView);
        float[] view = cameraView;
        float[] inverseView = Mat4f.Invert(cameraInverseView, view);
        if (inverseProjection == null || inverseView == null) return false;
        float[] currentViewProj = Mat4f.Mul(cameraViewProjection, projection, view);
        float[] inverseCurrent = Mat4f.Invert(cameraInverseViewProjection, currentViewProj);
        if (inverseCurrent == null) return false;
        float[] previousProjection = VulkanProjection(frame.PreviousProjection.Span, cameraPreviousProjection);
        frame.PreviousView.Span.CopyTo(cameraPreviousView);
        float[] previousViewProj = Mat4f.Mul(cameraPreviousViewProjection, previousProjection, cameraPreviousView);
        float[] delta = Mat4f.Identity(cameraDelta);
        delta[12] = frame.CameraDeltaX; delta[13] = frame.CameraDeltaY; delta[14] = frame.CameraDeltaZ;
        previousViewProj = Mat4f.Mul(cameraPreviousDeltaViewProjection, previousViewProj, delta);
        float[] toPrevious = Mat4f.Mul(cameraToPrevious, previousViewProj, inverseCurrent);
        float[] fromPrevious = Mat4f.Invert(cameraFromPrevious, toPrevious);
        if (fromPrevious == null) return false;
        var temporal = frame.Provider;
        camera = new NgxFrameGenerationCamera(projection, inverseProjection, toPrevious, fromPrevious,
            temporal.NearPlane, temporal.FarPlane, temporal.FovRadians, (float)width / height,
            temporal.JitterX, temporal.JitterY, temporal.PlayerPositionX, temporal.PlayerPositionY,
            temporal.PlayerPositionZ, inverseView[4], inverseView[5], inverseView[6],
            inverseView[0], inverseView[1], inverseView[2], -inverseView[8], -inverseView[9], -inverseView[10]);
        return camera.IsValid;
    }

    /// <summary>Copies a 16-float GL projection and remaps its clip depth to Vulkan's zero-to-one range.</summary>
    /// <param name="projection">Column-major GL projection; exactly 16 floats.</param>
    /// <param name="output">Scratch that receives the remapped projection.</param>
    /// <returns><paramref name="output" />.</returns>
    private float[] VulkanProjection(ReadOnlySpan<float> projection, float[] output)
    {
        projection.CopyTo(projectionSource);
        return Mat4f.Mul(output, DepthRemap, projectionSource);
    }

    internal void Reset() => session.Reset();
    public void Dispose() => session.Dispose();
}
