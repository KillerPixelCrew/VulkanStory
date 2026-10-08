using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private SdlGamepadInput? controllers;
    private ControllerPerformanceDiagnostics? controllerPerformance;
    private SdlGuiTextInput? guiText;
    /// <summary>Session-owned negotiated analog state shared by controller input and original client packet consumers.</summary>
    internal ControllerMovementState ControllerMovement { get; } = new();
    /// <summary>Releases controller input/UI ownership for the departing original world.</summary>
    /// <param name="client">Departing client identity.</param>
    internal void ReleaseControllerWorld(ClientMain client) => controllers?.WorldLeaving(client);
    /// <summary>Finds the currently opened controller settings dialog for this world.</summary>
    /// <param name="owner">Original client identity.</param>
    /// <returns>The matching dialog, or null.</returns>
    internal ControllerSettingsDialog? OpenedControllerSettings(ClientMain owner) => controllers?.OpenedSettings(owner);
    /// <summary>Checks whether the controller owner still owns this exact dialog/world pair.</summary>
    /// <param name="owner">Original client identity.</param>
    /// <param name="dialog">Dialog whose ownership is being checked.</param>
    /// <returns>True only for the current owned dialog.</returns>
    internal bool OwnsControllerSettings(ClientMain owner, ControllerSettingsDialog dialog) => controllers?.OwnsSettings(owner, dialog) == true;
    /// <summary>Attempts to open the active device profile settings from the session owner thread.</summary>
    /// <returns>True when opening succeeded; false when no eligible controller/settings owner exists.</returns>
    internal bool TryOpenControllerSettings()
    {
        RequireActive();
        return controllers?.OpenSettings() == true;
    }
    internal void OpenControllerSettings()
    {
        RequireActive();
        if (!TryOpenControllerSettings())
            Temporal.CurrentClient?.api?.ShowChatMessage("Connect a controller to open VulkanStory controller settings.");
    }
    /// <summary>Connects the owned SDL loop to pacing, input markers, controller updates and frame rendering; diagnostic recording stays behind its recording gate.</summary>
    private GamePlatformCallbacks CreatePlatformCallbacks()
    {
        RequireOwner();
        guiText = new SdlGuiTextInput(Window, new ControllerTextTargets(
            screen => (screen as ControllerKeyboardScreen)?.Target,
            dialog => (dialog as ControllerKeyboardDialog)?.Target));
        controllers = new SdlGamepadInput(new SessionControllerHost(this, platform, Window, guiText, ControllerMovement));
        bool devicesChanged = false;
        bool controllerEnabled = services.RendererSettings.Settings.ControllerEnabled;
        var performance = new ControllerPerformanceDiagnostics(message =>
        {
            var settings = services.RendererSettings.Settings;
            GameFrameSettings frameSettings = CaptureFrameSettings();
            platform.Logger.Notification("{0}; FG={1}; latency={2}; vsync={3}; maxFps={4}; controller={5}",
                message, settings.FrameGeneration, settings.LowLatencyMode, frameSettings.Vsync, frameSettings.MaxFps, controllers?.Status ?? "disposed");
            if (VulkanStats.MemorySource is { } memory)
                platform.Logger.Notification("[VulkanStory] Controller performance GPU memory: {0}", memory.DiagnosticMemoryLine());
        });
        controllerPerformance = performance;
        return new GamePlatformCallbacks(
            BeforeInput: () =>
            {
                RequireActive();
                AdvanceHeadlessScenarioBeforeInput();
                RuntimeBootstrap.Current.ApplyPendingControls();
                try
                {
                    OptionsSettingsOwner.ApplyPendingReturns();
                    DriveMultiplierDiagnosticBeforeInput();
                    DriveInventoryCyclesBeforeInput();
                    DriveControllerInventoryDiagnosticBeforeInput();
                    DriveControllerRadialDiagnosticBeforeInput();
                }
                catch (Exception error) { RecordHeadlessRenderFailure(error); throw; }
                bool enabled = services.RendererSettings.Settings.ControllerEnabled;
                if (enabled != controllerEnabled)
                {
                    if (!enabled)
                    {
                        controllers.SuspendInput();
                        input?.Input.ReleaseControllers();
                        if (ControllerHints.SetControllerActive(false)) RecomposeControllerGui();
                    }
                    else devicesChanged = true; // Rescan after hot-plug while disabled.
                    controllerEnabled = enabled;
                }
                PrepareFramePacing();
                Device.MarkLatency(inputFrameId, LatencyMarker.InputSample);
            },
            InputPumped: () => { },
            UpdateControllers: () =>
            {
                long started = performance.Recording ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (controllerEnabled) controllers.PrepareForEventPump();
                if (performance.Recording)
                    performance.RecordGamepadUpdate(System.Diagnostics.Stopwatch.GetTimestamp() - started);
                Device.MarkVendorInputStart(inputFrameId);
            },
            ControllersPumped: () =>
            {
                bool changed = devicesChanged; devicesChanged = false;
                if (controllerEnabled) controllers.PollAfterEventPump(changed);
                else controllers.PollSuspended();
            },
            ControllerActivity: id => { if (controllerEnabled) controllers.NoteGamepadActivity(id); },
            RefreshControllers: () => devicesChanged = true,
            ControllerFocusLost: controllers.OnFocusLost,
            PhysicalInput: () =>
            {
                performance.PhysicalInput();
                if (ControllerHints.SetControllerActive(false)) RecomposeControllerGui();
            },
            TouchEnabled: () => services.RendererSettings.Settings.TouchEnabled,
            HasTextTarget: guiText.HasCurrentTarget,
            SyncTextInput: guiText.Sync,
            TextTargetRevision: () => guiText.TargetRevision,
            StopTextInput: guiText.Stop,
            ResizeGraphics: Resize,
            RecomposeGui: RecomposeControllerGui,
            RecordInputAge: VulkanStats.RecordSdlInputAge,
            StopAndDrainGraphics: StopAndDrain)
        {
            ControllerPerformance = performance,
            ControllerDiagnosticsEnabled = () => services.RendererSettings.Settings.ControllerEnabled,
            ControllerInputActive = () => controllers?.InputActive == true,
        };
    }
    private void RecomposeControllerGui()
    {
        RequireOwner();
        Vintagestory.Client.ScreenManager.GuiComposers?.MarkAllDialogsForRecompose();
        temporal?.CurrentClient?.GuiComposers?.MarkAllDialogsForRecompose();
    }
    /// <summary>Disposes controller ownership, stops text input and resets analog movement during session teardown.</summary>
    private void StopControllers()
    {
        var failures = new List<Exception>();
        try { controllers?.Dispose(); controllers = null; }
        catch (Exception error) { failures.Add(error); }
        try { guiText?.Stop(); }
        catch (Exception error) { failures.Add(error); }
        ControllerMovement.Reset();
        if (failures.Count != 0) throw new AggregateException("Controller/text input cleanup failed.", failures);
    }
}
