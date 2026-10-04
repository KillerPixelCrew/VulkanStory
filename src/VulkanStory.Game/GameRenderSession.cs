using System.Diagnostics;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Platform.Sdl;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed record GameFrameSettings(bool Bloom, bool GodRays, bool Fxaa,
    bool Ssao, int ShadowQuality, bool Vsync, float MaxFps);

// The process session owns its concrete services. Startup supplies the selected
// game data path and normalized renderer settings, without placeholder callbacks.
internal sealed record GameSessionServices(RendererSettingsState RendererSettings, string DataPath)
{
    internal static GameSessionServices Load(string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        string resolved = Path.GetFullPath(dataPath);
        return new GameSessionServices(new RendererSettingsState(new RendererSettingsStore(resolved).Load()), resolved);
    }
}

internal sealed partial class GameRenderSession : IDisposable
{
    // A failed factory has no ProcessRuntime.session owner yet. If its cleanup
    // also fails, preserve the complete dependency graph for process lifetime.
    private static readonly List<GameRenderSession> FailedInitializationOwners = new();
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ClientPlatformWindows platform;
    private readonly StartupRoutingTransaction routing;
    private readonly GameSessionServices services;
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private SdlWindowHost? window;
    private VulkanDevice? device;
    private GameGraphicsAdapter? graphics;
    private GamePlatformAdapter? input;
    private RuntimeFrameGeneration? frameGeneration;
    private RuntimeUpscalers? upscalers;
    private GameTemporalOwner? temporal;
    private bool rebuildTargetsPending, reloadTerrainPending;
    private bool rendering, stopping, coreDisposed;
    private Exception? graphicsReleaseFailure;
    private Exception? renderCycleFailure;
    private bool? appliedVsync;
    private int? appliedFrameCap;
    private VulkanStory.Contracts.RendererLatencySelection? appliedLatency;
    private GameFrameSettings? pendingFrameSettings;
    private ulong inputFrameId;
    internal bool Stopping => stopping;
    internal bool Matches(ClientPlatformWindows candidate) => ReferenceEquals(platform, candidate);
    internal SdlWindowHost Window => window ?? throw new ObjectDisposedException(nameof(GameRenderSession));
    internal VulkanDevice Device => device ?? throw new ObjectDisposedException(nameof(GameRenderSession));
    internal GameGraphicsAdapter Graphics => graphics ?? throw new ObjectDisposedException(nameof(GameRenderSession));
    internal GamePlatformAdapter Input => input ?? throw new ObjectDisposedException(nameof(GameRenderSession));
    internal GameTemporalOwner Temporal => temporal ?? throw new ObjectDisposedException(nameof(GameRenderSession));

    private GameRenderSession(ClientPlatformWindows platform, StartupRoutingTransaction routing, GameSessionServices services)
    { this.platform = platform; this.routing = routing; this.services = services; }

    internal static GameRenderSession Create(ClientPlatformWindows platform, StartupRoutingTransaction routing,
        GameSessionServices services, string title, int width, int height, int windowState = 0, int windowBorder = 0)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(services.RendererSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(services.DataPath);
        var session = new GameRenderSession(platform, routing, services);
        try
        {
            session.window = SdlWindowHost.Create(title, width, height, hidden: HeadlessHarnessOptions.KeepWindowHidden,
                keepHidden: HeadlessHarnessOptions.KeepWindowHidden);
            session.SetWindowIcon();
            session.window.SetMinimumSize(600, 400);
            session.window.SetBordered(windowBorder != 2);
            session.window.SetResizable(windowBorder == 0);
            if (!HeadlessHarnessOptions.Enabled && windowState == 3) session.window.SetFullscreen(true);
            else if (windowState == 2) session.window.Maximize();
            else if (windowState == 1) session.window.Minimize();
            session.window.Sync();
            session.device = new VulkanDevice();
            session.ConfigureDevice();
            session.device.NativeShadersEnabled = services.RendererSettings.Settings.NativeShaders;
            session.device.EnableStreamline = services.RendererSettings.Settings.Streamline;
            session.device.LatencySelection = services.RendererSettings.LatencySelection;
            session.device.FrameGenerationMultiplier = services.RendererSettings.Settings.FrameGenerationMultiplier;
            session.upscalers = new RuntimeUpscalers(services.RendererSettings, services.DataPath,
                message => platform.Logger.Notification("{0}", message), session.RequestProviderTargetRebuild,
                session.ApplyTerrainLodBias);
            session.upscalers.Prepare(session.device);
            var pixels = session.window.PixelSize;
            if (!session.device.Initialize(session.window, pixels.Width, pixels.Height, out string reason))
                throw new InvalidOperationException("Vulkan session initialization failed: " + reason);
            // SDK requirements were contributed by the owned SR registry before
            // initialization; provider contexts now receive the real device.
            // Bring-up failures use the same preparation rollback and drain.
            session.upscalers.BringUp();
            session.NotifyDeviceCreated();
            session.temporal = new GameTemporalOwner(platform, session.device, services.RendererSettings);
            session.frameGeneration = new RuntimeFrameGeneration(session.device, services.RendererSettings,
                message => platform.Logger.Notification("{0}", message),
                message => platform.Logger.Error("{0}", message), session.temporal.RequestReset);
            session.AttachPresentationCounters();
            session.graphics = GameGraphicsAdapter.Attach(platform, session.device,
                () => routing.RoutingEnabled && !session.stopping, () => platform.ENABLE_MIPMAPS,
                () => Vintagestory.Client.NoObf.ClientSettings.MipMapLevel, () => platform.GlErrorChecking, session.CreateShaderCallbacks());
            session.graphics.ConfigurePostSettings(services.RendererSettings);
            session.graphics.ConfigureAmbientOcclusion(session.temporal);
            session.graphics.ConfigureShaderOverrides(session.shaderOverrides ??
                throw new InvalidOperationException("Shader override policy was not prepared."));
            session.graphics.ConfigureFramebufferHost(session.CreateFramebufferHost());
            session.input = GamePlatformAdapter.Attach(platform, session.window, routing, session.CreatePlatformCallbacks());
            platform.WindowSize.Width = pixels.Width;
            platform.WindowSize.Height = pixels.Height;
            return session;
        }
        catch (Exception failure)
        {
            try { session.ReleaseSessionResources(); }
            catch (Exception cleanup)
            {
                lock (FailedInitializationOwners) FailedInitializationOwners.Add(session);
                throw new AggregateException("Session initialization and cleanup failed; remaining owners retained until process exit.", failure, cleanup);
            }
            throw;
        }
    }

    internal void Run()
    {
        RequireActive();
        input!.Run();
    }
    internal void RenderFrame()
    {
        RequireActive();
        if (rendering) throw new InvalidOperationException("Recursive game frame.");
        rendering = true;
        bool gpuCycleStarted = false;
        try
        {
            GameFrameSettings settings = pendingFrameSettings ?? throw new InvalidOperationException("SDL input must start this frame before rendering.");
            pendingFrameSettings = null;
            float delta = (float)frameClock.Elapsed.TotalSeconds;
            frameClock.Restart();
            PrepareHeadlessFrame(ref delta);
            if (appliedVsync != settings.Vsync) { Device.SetVSync(settings.Vsync); appliedVsync = settings.Vsync; }
            ScreenManager.FrameProfiler.Begin(null);
            ScreenManager.FrameProfiler.Mark("sleep");
            GameFrameBindings.Adopt(platform, settings);
            ApplyPendingProviderTargets();
            Graphics.FrameState = Graphics.FrameState with { Ssao = settings.Ssao, MotionWriteActive = false };
            Graphics.UpscaledThisFrame = Graphics.UpscaledCompositeReady = Graphics.SceneNoHudCaptured = false;
            Graphics.TaaResolvedThisFrame = false;
            Graphics.Stated.UiImageFramebuffer = 0;
            Device.RedirectDefaultFramebuffer(0);
            gpuCycleStarted = true;
            Device.BeginFrame();
            GameFrameBindings.Dispatch(platform, delta);
            Graphics.ComposeUiTarget(); // Flush any still-open owned UI scope before provider presentation.
            CompleteOptionsDiagnostic();
            CaptureDiagnosticWorldFrame();
            CaptureHeadlessFrame();
            CaptureScenarioReadbacks();
            frameGeneration!.Generate(Graphics, platform.FrameBuffers, Temporal.Snapshot());
            Device.Present();
            SampleFps();
            PublishPresentation();
            PublishDiagnosticFrame();
            ScreenManager.FrameProfiler.End();
            ThrottleHeadlessFrame();
            CompleteHeadlessScenarioFrame();
        }
        catch (Exception error)
        {
            renderCycleFailure ??= error;
            if (gpuCycleStarted) device?.RetainFailedFrame(error);
            RecordHeadlessRenderFailure(error);
            throw;
        }
        finally { rendering = false; }
    }
    private void PrepareFramePacing()
    {
        RequireActive();
        Device.LatencySelection = services.RendererSettings.LatencySelection;
        GameFrameSettings settings = CaptureFrameSettings();
        int frameCap = float.IsFinite(settings.MaxFps) && settings.MaxFps > 0 ? (int)settings.MaxFps : 0;
        if (appliedFrameCap != frameCap || appliedLatency != Device.LatencySelection)
        {
            Device.SetVendorLatencyFrameCap(frameCap);
            appliedFrameCap = frameCap; appliedLatency = Device.LatencySelection;
        }
        // Limiting occurs before collecting input, alongside vendor sleep.
        if (!settings.Vsync && !Device.VendorLatencyOwnsFrameCap && settings.MaxFps > 10 && settings.MaxFps < 241)
        {
            int delay = (int)(1000f / settings.MaxFps - frameClock.Elapsed.TotalMilliseconds);
            if (delay > 0) Thread.Sleep(delay);
        }
        pendingFrameSettings = settings;
        inputFrameId = Device.BeginLatencyFrame();
        long sleepStarted = controllerPerformance?.Recording == true ? Stopwatch.GetTimestamp() : 0;
        Device.SleepVendorLatency(inputFrameId, services.RendererSettings.Settings.FrameGeneration != "off");
        if (sleepStarted != 0)
            controllerPerformance!.RecordVendorSleep(Stopwatch.GetTimestamp() - sleepStarted);
        // XeLL requires SimulationStart immediately after the final sleep.
        // IME/controller setup then precedes the Anti-Lag input-start marker.
        Device.MarkLatency(inputFrameId, LatencyMarker.SimulationStart);
    }
    private void RequestProviderTargetRebuild()
    {
        RequireOwner();
        rebuildTargetsPending = true;
        reloadTerrainPending |= TerrainShadersReady();
    }
    private void ApplyPendingProviderTargets()
    {
        // Retain the loading-screen guard: initial shader load must finish
        // before rebuilding targets or reloading terrain sampler definitions.
        if (!rebuildTargetsPending || !TerrainShadersReady()) return;
        rebuildTargetsPending = false;
        platform.RebuildFrameBuffers();
        if (reloadTerrainPending) ReloadTerrainShaders();
        reloadTerrainPending = false;
    }
    internal bool RenderUpscaler()
    {
        RequireActive();
        // Called by the migrated post chain before bloom/final/UI, never from
        // BeforePresent where the final scene has already been composed.
        GameTemporalFrame temporal = Temporal.Snapshot();
        if (temporal.FrameId != Device.LatencyFrameId || !temporal.HasCamera || !temporal.MotionValid)
        { Graphics.UpscaledThisFrame = false; return false; }
        bool evaluated = upscalers!.Evaluate(Graphics, platform.FrameBuffers, temporal.Provider);
        return evaluated;
    }
    internal void RenderTemporalPostTail()
    {
        RequireActive();
        if (!GameFramebufferBindings.OffscreenEnabled(platform)) return;
        // AO completes before this seam. Reconstruction precedes bloom,
        // god rays and luma; UI never enters either reconstruction input.
        if (!RenderUpscaler()) Graphics.RenderTaaResolve(Temporal);
        Graphics.RenderPostTail(Graphics.PostSceneTexture(), Graphics.PostGlowTexture());
    }
    internal void RenderPostProcessing(float[]? projection)
    {
        RequireActive();
        if (!GameFramebufferBindings.OffscreenEnabled(platform)) return;
        Graphics.RenderAmbientOcclusionPost(projection!);
        RenderTemporalPostTail();
    }
    internal void ApplyRendererSettings(RendererSettings next)
    {
        RequireActive();
        next = next.Normalize();
        RendererSettings previous = services.RendererSettings.Settings;
        // Pacing/input options are consumed at pre-input; the FPS overlay reads
        // settings directly. These saves do not invalidate scene resources or
        // temporal history. Any other changed property keeps the full reset path.
        if ((previous with
        {
            LowLatencyMode = next.LowLatencyMode,
            ShowFpsCounter = next.ShowFpsCounter,
            ControllerEnabled = next.ControllerEnabled,
            TouchEnabled = next.TouchEnabled,
        }) == next)
        {
            services.RendererSettings.Apply(next);
            return;
        }
        frameGeneration!.Reset();
        services.RendererSettings.Apply(next);
        upscalers!.ApplySettings();
        Temporal.RequestReset();
        ApplyTerrainLodBias(services.RendererSettings.EffectiveTaa ?
            services.RendererSettings.Settings.TaaMipBias : 0f);
    }
    private void Resize(int width, int height)
    {
        RequireActive();
        if (width <= 0 || height <= 0) return;
        Device.Resize(width, height);
        Temporal.State.RequestReset(EnumTemporalResetReason.Resize);
        platform.RebuildFrameBuffers();
    }
    private void StopAndDrain()
    {
        RequireOwner();
        if (graphicsReleaseFailure != null)
            throw new InvalidOperationException("Session graphics release failed; cleanup is terminal and owners remain retained.", graphicsReleaseFailure);
        if (coreDisposed) return;
        if (rendering) throw new InvalidOperationException("Stop outside the current render frame.");
        var failures = new List<Exception>();
        try
        {
            // Failed/unsubmitted recording can still own provider references and
            // pending timeline retirements. Preserve the entire graph before
            // clearing GUI owners or beginning any dependent release.
            device?.RequireFrameRelease();
            // GUI LoadedTextures must be released while routed texture deletion
            // can still reach this live Vulkan adapter. Rollback is too late.
            OptionsSettingsOwner.ClearAll();
            StopControllers();
            stopping = true;
            frameGeneration?.Dispose(); frameGeneration = null;
            device?.ReleaseStreamlineFrameGenerationResources();
            graphics?.ReleaseAmbientOcclusion();
            graphics?.ReleaseOit();
            graphics?.ReleasePreviousAnimations();
            graphics?.ReleaseLiquidMotionProgram();
            upscalers?.Dispose(); upscalers = null;
            // Device disposal waits owned GPU work/presentation while SDL lives.
            device?.Dispose(); device = null;
            VulkanStats.ConfigurePresentationObserver(null, null);
            graphics?.Dispose(); graphics = null;
            coreDisposed = graphics == null;
        }
        catch (Exception error)
        {
            stopping = true;
            graphicsReleaseFailure = error;
            failures.Add(error);
        }
        if (failures.Count != 0) throw new AggregateException("Session graphics cleanup failed.", failures);
    }
    private void ReleaseSessionResources()
    {
        RequireOwner();
        var failures = new List<Exception>();
        try { StopAndDrain(); } catch (Exception error) { failures.Add(error); }
        // A failed GPU drain retains SDL. Other cleanup failures must not prevent
        // input detachment/window release once device disposal has completed.
        if (device == null)
        {
            if (input != null)
            {
                try { input.DetachAfterDrain(); input = null; window = null; }
                catch (Exception error) { failures.Add(error); }
            }
            else
            {
                try { window?.Dispose(); window = null; }
                catch (Exception error) { failures.Add(error); }
            }
        }
        if (failures.Count != 0) throw new AggregateException("Session resource cleanup failed.", failures);
    }
    private void RequireOwner()
    { if (ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Session work requires its owner thread."); }
    private void RequireActive()
    {
        RequireOwner();
        if (renderCycleFailure != null)
            throw new InvalidOperationException("Session render cycle failed; further frame/control work is rejected.", renderCycleFailure);
        if (stopping || !routing.RoutingEnabled || input is null) throw new InvalidOperationException("Complete SDL/Vulkan session routing is not active.");
    }
    public void Dispose()
    {
        RequireOwner();
        ReleaseSessionResources();
    }
}
