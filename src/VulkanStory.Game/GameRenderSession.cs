using System.Diagnostics;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Platform.Sdl;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

/// <summary>One pre-input snapshot of ordinary post-processing, shadow and frame-pacing settings.</summary>
/// <remarks><c>Vsync</c> selects the swap interval; <c>FrameSleep</c> enables the CPU frame limiter (both follow the original VSync mode).</remarks>
internal sealed record GameFrameSettings(bool Bloom, bool GodRays, bool Fxaa,
    bool Ssao, int ShadowQuality, bool Vsync, bool FrameSleep, float MaxFps);

// The process session owns its concrete services. Startup supplies the selected
// game data path and normalized renderer settings, without placeholder callbacks.
/// <summary>Concrete renderer settings and resolved game data path selected for the process session.</summary>
internal sealed record GameSessionServices(RendererSettingsState RendererSettings, string DataPath)
{
    internal static GameSessionServices Load(string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        string resolved = Path.GetFullPath(dataPath);
        return new GameSessionServices(new RendererSettingsState(new RendererSettingsStore(resolved).Load()), resolved);
    }
}

/// <summary>Owns the SDL window, Vulkan device, input adapters, temporal history and providers on one session thread; the original game supplies simulation and scene callbacks.</summary>
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
    private CompositionReadiness? composition;
    private bool rebuildTargetsPending, reloadTerrainPending;
    private bool rendering, stopping, coreDisposed;
    private Exception? graphicsReleaseFailure;
    private Exception? renderCycleFailure;
    private int? appliedFrameCap;
    private VulkanStory.Contracts.RendererLatencySelection? appliedLatency;
    private GameFrameSettings? pendingFrameSettings;
    private ulong inputFrameId;
    private string? reconstructionDecline;
    private string? upscalerFrameRefusal;
    internal bool Stopping => stopping;
    internal bool Matches(ClientPlatformWindows candidate) => ReferenceEquals(platform, candidate);
    internal SdlWindowHost Window => window ?? throw new ObjectDisposedException(nameof(GameRenderSession));
    internal VulkanDevice Device => device ?? throw new ObjectDisposedException(nameof(GameRenderSession));
    internal GameGraphicsAdapter Graphics => graphics ?? throw new ObjectDisposedException(nameof(GameRenderSession));
    internal GamePlatformAdapter Input => input ?? throw new ObjectDisposedException(nameof(GameRenderSession));
    internal GameTemporalOwner Temporal => temporal ?? throw new ObjectDisposedException(nameof(GameRenderSession));

    private GameRenderSession(ClientPlatformWindows platform, StartupRoutingTransaction routing, GameSessionServices services)
    { this.platform = platform; this.routing = routing; this.services = services; }

    /// <summary>Creates the owned SDL window, Vulkan device, providers and adapters while startup routing is dormant.</summary>
    /// <param name="platform">Original game platform identity retained by every adapter.</param>
    /// <param name="routing">Prepared startup transaction whose predicate gates game draws.</param>
    /// <param name="services">Resolved data path and normalized session settings.</param>
    /// <param name="title">Original game window title.</param>
    /// <param name="width">Requested window width in drawable pixels.</param>
    /// <param name="height">Requested window height in drawable pixels.</param>
    /// <param name="windowState">Original window-state numeric value; hidden harness policy still controls visibility.</param>
    /// <param name="windowBorder">Original border numeric value mapped to SDL border/resizability.</param>
    /// <returns>A fully prepared session to be committed by the startup transaction.</returns>
    /// <remarks>Factory failure releases partial resources. Cleanup failure retains the complete owner graph and throws both failures.</remarks>
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
            // The game requests (and remembers) drawable pixels; SDL creates logical units,
            // which differ only under high-density scaling.
            if (session.window.WindowSize != session.window.PixelSize) session.window.SetPixelSize(width, height);
            session.SetWindowIcon();
            // The original enforces a 600x400 ClientSize floor, and ClientSize is its drawable/viewport extent.
            session.window.SetMinimumPixelSize(600, 400);
            session.window.SetBordered(windowBorder != 2);
            // Borderless (Hidden) startup stays resizable so SDL_MaximizeWindow can
            // honour windowed fullscreen; only Fixed disables resizing.
            session.window.SetResizable(windowBorder != 1);
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
            // Streamline PCL pings arrive as a registered Windows message inside SDL's event
            // pump. StopAndDrain removes the hook before device disposal (window disposal repeats it).
            uint pclMessage = session.device.PclWindowMessage;
            if (pclMessage != 0 && !session.window.InstallPclPingHook(pclMessage, () => session.device?.MarkPclLatencyPing()))
                platform.Logger.Warning("SDL could not install the Streamline PCL Windows message hook");
            // SDK requirements were contributed by the owned SR registry before
            // initialization; provider contexts now receive the real device.
            // Bring-up failures use the same preparation rollback and drain.
            session.upscalers.BringUp();
            session.NotifyDeviceCreated();
            session.temporal = new GameTemporalOwner(platform, session.device, services.RendererSettings);
            session.frameGeneration = new RuntimeFrameGeneration(session.device, services.RendererSettings,
                message => platform.Logger.Notification("{0}", message),
                message => platform.Logger.Error("{0}", message), session.temporal.RequestReset);
            // The headless harness is not held unless a validation run opts in; its captures
            // read the composed frame before any hold either way.
            session.composition = new CompositionReadiness(!HeadlessHarnessOptions.Active ||
                Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_COMPOSITION_HOLD") == "1",
                message => platform.Logger.Notification("{0}", message),
                message => platform.Logger.Warning("{0}", message));
            session.AttachPresentationCounters();
            session.graphics = GameGraphicsAdapter.Attach(platform, session.device,
                () => routing.RoutingEnabled && !session.stopping, () => platform.ENABLE_MIPMAPS,
                () => Vintagestory.Client.NoObf.ClientSettings.MipMapLevel, () => platform.GlErrorChecking, session.CreateShaderCallbacks());
            session.graphics.ConfigurePostSettings(services.RendererSettings);
            session.graphics.ConfigureAmbientOcclusion(session.temporal);
            session.graphics.ConfigureShaderOverrides(session.shaderOverrides ??
                throw new InvalidOperationException("Shader override policy was not prepared."));
            session.graphics.ConfigureFramebufferHost(session.CreateFramebufferHost());
            session.input = GamePlatformAdapter.Attach(platform, session.window, routing, session.CreatePlatformCallbacks(), windowBorder);
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

    /// <summary>Runs the active SDL loop on the session owner thread until the ordinary close path accepts exit.</summary>
    internal void Run()
    {
        RequireActive();
        input!.Run();
    }
    /// <summary>Dispatches one original game frame between owned Vulkan begin and provider/presentation boundaries.</summary>
    /// <remarks>Requires a pre-input settings snapshot and rejects recursion. A render failure retains the failed frame and rejects later frame/control work.</remarks>
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
            // The session is the only swap-interval owner; SetVSync ignores an unchanged value.
            Device.SetVSync(settings.Vsync);
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
            // Background pipeline results publish at BeginFrame; skips counted after it are this frame's.
            long pipelineSkipsAtStart = Device.PipelineDrawsSkipped;
            GameFrameBindings.Dispatch(platform, delta);
            Graphics.ComposeUiTarget(); // Flush any still-open owned UI scope before provider presentation.
            CompleteOptionsDiagnostic();
            CaptureDiagnosticWorldFrame();
            CaptureHeadlessFrame();
            CaptureScenarioReadbacks();
            // Diagnostics above read the composed frame; a hold replaces only what is presented.
            bool hold = ApplyCompositionHold();
            frameGeneration!.Generate(Graphics, platform.FrameBuffers, Temporal.Snapshot(), hold);
            ObserveCompositionReadiness(pipelineSkipsAtStart);
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
        // A failed/unavailable FG provider no longer forces low latency On.
        Device.LatencySelection = services.RendererSettings.EffectiveLatencySelection(frameGeneration!.Unavailable);
        GameFrameSettings settings = CaptureFrameSettings();
        int frameCap = float.IsFinite(settings.MaxFps) && settings.MaxFps > 0 ? (int)settings.MaxFps : 0;
        if (appliedFrameCap != frameCap || appliedLatency != Device.LatencySelection)
        {
            Device.SetVendorLatencyFrameCap(frameCap);
            appliedFrameCap = frameCap; appliedLatency = Device.LatencySelection;
        }
        // Limiting occurs before collecting input, alongside vendor sleep.
        // The original applies frame sleep in every VSync mode except "VSync only".
        if (settings.FrameSleep && !Device.VendorLatencyOwnsFrameCap && settings.MaxFps > 10 && settings.MaxFps < 241)
        {
            // Fractional, high-resolution wait: Thread.Sleep(int) truncates and can
            // overshoot by a whole ~15.6 ms timer tick without 1 ms resolution.
            double delay = 1000.0 / settings.MaxFps - frameClock.Elapsed.TotalMilliseconds;
            if (delay > 0) VulkanStory.Render.Vulkan.Present.PreciseSleep.For(delay);
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
    /// <summary>Evaluates SR at the reconstruction seam before bloom, final composition and UI.</summary>
    /// <returns>True when this matching camera/motion frame was evaluated; false when the temporal prerequisites decline.</returns>
    internal bool RenderUpscaler()
    {
        RequireActive();
        // Called by the migrated post chain before bloom/final/UI, never from
        // BeforePresent where the final scene has already been composed.
        GameTemporalFrame temporal = Temporal.Snapshot();
        upscalerFrameRefusal = temporal.FrameId != Device.LatencyFrameId ? "temporal frame identity mismatch" :
            !temporal.HasCamera ? "world camera not captured" :
            !temporal.MotionValid ? Temporal.MotionReadiness : null;
        if (upscalerFrameRefusal != null)
        { Graphics.UpscaledThisFrame = false; return false; }
        bool evaluated = upscalers!.Evaluate(Graphics, platform.FrameBuffers, temporal.Provider);
        if (!evaluated) upscalerFrameRefusal = upscalers.EvaluationReadiness;
        return evaluated;
    }
    /// <summary>Runs SR or native TAA followed by the retained display-resolution post tail.</summary>
    /// <remarks>AO precedes this seam and UI is excluded from reconstruction inputs.</remarks>
    internal void RenderTemporalPostTail()
    {
        RequireActive();
        if (!GameFramebufferBindings.OffscreenEnabled(platform)) return;
        // AO completes before this seam. Reconstruction precedes bloom,
        // god rays and luma; UI never enters either reconstruction input.
        if (!RenderUpscaler())
        {
            CaptureNativeTaaInputs();
            Graphics.RenderTaaResolve(Temporal);
        }
        ObserveReconstruction();
        Graphics.RenderPostTail(Graphics.PostSceneTexture(), Graphics.PostGlowTexture());
    }
    private void ObserveReconstruction()
    {
        RendererSettings requested = services.RendererSettings.Settings;
        if (requested.Upscaler == "off" && !requested.Taa)
        { reconstructionDecline = null; return; }
        if (Graphics.UpscaledThisFrame || Graphics.TaaResolvedThisFrame)
        {
            if (reconstructionDecline != null)
                platform.Logger.Notification("VulkanStory: temporal reconstruction resumed with {0} at frame {1}.",
                    Graphics.UpscaledThisFrame ? services.RendererSettings.EffectiveUpscaler : "TAA", Device.LatencyFrameId);
            reconstructionDecline = null;
            return;
        }
        if (reconstructionDecline != null) return;
        reconstructionDecline = services.RendererSettings.UpscalerReplacesTaa
            ? upscalerFrameRefusal ?? "upscaler declined" : Graphics.TaaReadiness;
        platform.Logger.Warning("VulkanStory: temporal reconstruction dropped at frame {0}: {1}; motion: {2}.",
            Device.LatencyFrameId, reconstructionDecline, Temporal.MotionReadiness);
    }
    /// <summary>Executes AO followed by temporal reconstruction and display-resolution post stages.</summary>
    /// <param name="projection">Original scene projection used by the AO path; ignored when offscreen rendering is disabled.</param>
    internal void RenderPostProcessing(float[]? projection)
    {
        RequireActive();
        if (!GameFramebufferBindings.OffscreenEnabled(platform)) return;
        Graphics.RenderAmbientOcclusionPost(projection!);
        RenderTemporalPostTail();
    }
    /// <summary>Normalizes and applies requested options to this active session.</summary>
    /// <param name="next">Requested settings snapshot.</param>
    /// <remarks>Pacing/input/FPS, multiplier-only, per-frame cosmetic and restart-only changes retain scene resources. Other changes reset FG/SR/history and request target/shader rebuilding.</remarks>
    internal void ApplyRendererSettings(RendererSettings next)
    {
        RequireActive();
        next = next.Normalize();
        RendererSettings previous = services.RendererSettings.Settings;
        // Pacing/input options are consumed at pre-input; the FPS overlay reads
        // settings directly. Sharpening strength, debug views and the god-ray
        // sample cap are read per frame as shader constants or pass selection.
        // Enabled, NativeShaders and Streamline are consumed only at startup and
        // are marked restart in the panel. These saves do not invalidate scene
        // resources or temporal history. Any other changed property keeps the
        // full reset path.
        if ((previous with
        {
            LowLatencyMode = next.LowLatencyMode,
            ShowFpsCounter = next.ShowFpsCounter,
            ControllerEnabled = next.ControllerEnabled,
            TouchEnabled = next.TouchEnabled,
            FrameGenerationMultiplier = next.FrameGenerationMultiplier,
            TaaSharpness = next.TaaSharpness,
            TaaDebugView = next.TaaDebugView,
            AmbientOcclusionDebugView = next.AmbientOcclusionDebugView,
            GodRaysSampleCap = next.GodRaysSampleCap,
            Enabled = next.Enabled,
            NativeShaders = next.NativeShaders,
            Streamline = next.Streamline,
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
            // Unhook PCL pings before any provider/device release, so SDL's message pump can no
            // longer reach the device. Window disposal repeats this as an idempotent no-op.
            window?.RemovePclPingHook();
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
            graphics?.ReleaseTaaSampleTargets();
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
    /// <summary>Drains and releases providers/device before detaching input and destroying the SDL window.</summary>
    /// <remarks>Must run on the owner thread outside rendering. Failed GPU release retains dependent window/patch owners and propagates.</remarks>
    public void Dispose()
    {
        RequireOwner();
        ReleaseSessionResources();
    }
}
