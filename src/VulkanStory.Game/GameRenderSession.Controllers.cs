using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;
using VulkanStory.Render.Vulkan;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private SdlGamepadInput? controllers;
    private SdlGuiTextInput? guiText;
    internal ControllerMovementState ControllerMovement { get; } = new();
    internal void ReleaseControllerWorld(ClientMain client) => controllers?.WorldLeaving(client);
    internal ControllerSettingsDialog? OpenedControllerSettings(ClientMain owner) => controllers?.OpenedSettings(owner);
    internal bool OwnsControllerSettings(ClientMain owner, ControllerSettingsDialog dialog) => controllers?.OwnsSettings(owner, dialog) == true;
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
    private GamePlatformCallbacks CreatePlatformCallbacks()
    {
        RequireOwner();
        guiText = new SdlGuiTextInput(Window, new ControllerTextTargets(
            screen => (screen as ControllerKeyboardScreen)?.Target,
            dialog => (dialog as ControllerKeyboardDialog)?.Target));
        controllers = new SdlGamepadInput(new SessionControllerHost(this, platform, Window, guiText, ControllerMovement));
        bool devicesChanged = false;
        bool controllerEnabled = services.RendererSettings.Settings.ControllerEnabled;
        return new GamePlatformCallbacks(
            BeforeInput: () =>
            {
                AdvanceHeadlessScenarioBeforeInput();
                RuntimeBootstrap.Current.ApplyPendingControls();
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
            },
            InputPumped: () => Device.MarkLatency(inputFrameId, LatencyMarker.InputSample),
            UpdateControllers: () =>
            {
                if (controllerEnabled) controllers.PrepareForEventPump();
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
            StopAndDrainGraphics: StopAndDrain);
    }
    private void RecomposeControllerGui()
    {
        RequireOwner();
        Vintagestory.Client.ScreenManager.GuiComposers?.MarkAllDialogsForRecompose();
        temporal?.CurrentClient?.GuiComposers?.MarkAllDialogsForRecompose();
    }
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
