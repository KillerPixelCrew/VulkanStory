using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;
using VulkanStory.Settings;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private GameGuiBindings? scenarioGui;
    private GuiScreen? scenarioOriginalScreen;
    private ClientMain? scenarioOriginalWorld;
    private GuiDialog? scenarioPauseMenu;
    private GuiScreenSettings? scenarioMainOptions;
    private bool scenarioMainOptionsOpen;

    /// <summary>Captures the original screen/client once its selected scenario context is ready.</summary>
    /// <param name="scenario">Validated world or main-menu scenario.</param>
    /// <returns>True once the original host is ready; false while startup is still loading.</returns>
    private bool TryStartScenarioContext(HeadlessScenario scenario)
    {
        scenarioGui ??= new GameGuiBindings();
        GuiScreen? screen = scenarioGui.CurrentScreen;
        if (scenario.Context == "main")
        {
            if (Temporal.CurrentClient != null || HeadlessGameBindings.CurrentRunningClient() != null)
                throw new InvalidOperationException("Main scenario unexpectedly has a running world.");
            if (screen?.IsOpened != true || !screen.ShowMainMenu || !Graphics.TerrainShadersReady) return false;
        }
        else
        {
            if (HeadlessGameBindings.CurrentRunningClient() is not { BlocksReceivedAndLoaded: true } world ||
                !ReferenceEquals(Temporal.CurrentClient, world) || screen?.IsOpened != true) return false;
            scenarioOriginalWorld = world;
            scenarioPauseMenu = FindScenarioPauseMenu(world);
        }
        scenarioOriginalScreen = screen;
        return true;
    }

    /// <summary>Rejects replacement of the original world, dialog or expected main-menu screen.</summary>
    private void RequireScenarioContext()
    {
        if (scenarioOriginalScreen == null || scenarioGui == null)
            throw new InvalidOperationException("Scenario original host was not captured.");
        if (HeadlessHarnessOptions.Scenario!.Context == "main")
        {
            if (Temporal.CurrentClient != null || HeadlessGameBindings.CurrentRunningClient() != null ||
                !ReferenceEquals(scenarioGui.CurrentScreen,
                    scenarioMainOptionsOpen ? scenarioMainOptions : scenarioOriginalScreen))
                throw new InvalidOperationException("Main scenario lost its original Options/main-menu host or gained a world.");
        }
        else if (!ReferenceEquals(Temporal.CurrentClient, scenarioOriginalWorld) ||
            !ReferenceEquals(HeadlessGameBindings.CurrentRunningClient(), scenarioOriginalWorld) ||
            !ReferenceEquals(scenarioGui.CurrentScreen, scenarioOriginalScreen) ||
            !ReferenceEquals(FindScenarioPauseMenu(scenarioOriginalWorld!), scenarioPauseMenu))
            throw new InvalidOperationException("World scenario lost its original client, screen or pause dialog.");
    }

    /// <summary>Finds the game's existing pause dialog without creating or replacing a host.</summary>
    /// <param name="world">Captured original client identity.</param>
    /// <returns>The existing official pause dialog; absence or duplicate hosts fail the scenario.</returns>
    private GuiDialog FindScenarioPauseMenu(ClientMain world)
    {
        Type type = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogEscapeMenu")
            ?? throw new MissingMemberException("Original pause dialog type is absent.");
        return scenarioGui!.LoadedGuis(world)?.SingleOrDefault(dialog => dialog.GetType() == type)
            ?? throw new InvalidOperationException("Original world pause dialog is absent.");
    }

    /// <summary>Returns only the currently displayed composer of the captured original Options host.</summary>
    private GuiComposer? ScenarioOptionsComposer => HeadlessHarnessOptions.Scenario!.Context == "main"
        ? scenarioMainOptionsOpen && scenarioMainOptions?.IsOpened == true ? scenarioMainOptions.ElementComposer : null
        : scenarioPauseMenu?.IsOpened() == true ? scenarioPauseMenu.SingleComposer : null;

    /// <summary>Executes one scheduled Options operation through the existing widget or official host callback.</summary>
    /// <param name="action">Validated Options operation and optional page.</param>
    private void ExecuteScenarioOptions(HeadlessScenarioAction action)
    {
        RequireScenarioContext();
        bool main = HeadlessHarnessOptions.Scenario!.Context == "main";
        if (action.Operation == "open")
        {
            if (main)
            {
                if (!scenarioMainOptionsOpen)
                {
                    scenarioMainOptions = new GuiScreenSettings(scenarioOriginalScreen!.ScreenManager, scenarioOriginalScreen);
                    scenarioOriginalScreen.ScreenManager.LoadScreen(scenarioMainOptions);
                    scenarioMainOptionsOpen = true;
                }
            }
            else
            {
                GuiDialog parent = scenarioPauseMenu!;
                if (!parent.IsOpened() && !parent.TryOpen())
                    throw new InvalidOperationException("Original pause dialog could not open.");
                if (OptionsSettingsOwner.DiagnosticPage(parent.SingleComposer) == null)
                {
                    var composite = ScenarioOptionsComposite(parent);
                    var open = AccessTools.Method(typeof(GuiCompositeSettings), "OpenSettingsMenu", [])
                        ?? throw new MissingMemberException("Original Options entry is absent.");
                    if (open.Invoke(composite, null) is not true)
                        throw new InvalidOperationException("Original Options entry declined.");
                }
            }
            GuiComposer shown = ScenarioOptionsComposer ?? throw new InvalidOperationException("Original Options host is closed.");
            if (OptionsSettingsOwner.DiagnosticPage(shown) == null)
            {
                var entry = shown.GetToggleButton("vulkanstory")
                    ?? throw new InvalidOperationException("VulkanStory entry is absent from original Graphics.");
                ClickOptionsControl(shown, entry.Bounds, "VulkanStory entry");
                shown = ScenarioOptionsComposer ?? throw new InvalidOperationException("Options entry closed its original host.");
                if (OptionsSettingsOwner.DiagnosticPage(shown) == null)
                    throw new InvalidOperationException("Original entry did not display VulkanStory Options.");
            }
            string page = action.Page ?? "Image";
            if (OptionsSettingsOwner.DiagnosticPage(shown) != page)
                ClickOptionsControl(shown, shown.GetButton("vulkanstory-page-" + page).Bounds, page + " page");
        }
        else
        {
            GuiComposer shown = ScenarioOptionsComposer ?? throw new InvalidOperationException("Scheduled Options operation requires an open original host.");
            switch (action.Operation)
            {
                case "toggle-taa":
                    if (OptionsSettingsOwner.DiagnosticPage(shown) != "Image")
                        throw new InvalidOperationException("TAA toggle requires the Image page.");
                    // Preview and its footer are separate scheduled operations, allowing settled-frame assertions.
                    ClickOptionsControl(shown, shown.GetSwitch("Taa").Bounds, "TAA switch");
                    break;
                case "save":
                case "cancel":
                    if (OptionsSettingsOwner.DiagnosticPage(shown) == null)
                        throw new InvalidOperationException("Save/Cancel requires an active VulkanStory editor.");
                    ClickOptionsFooter(shown, action.Operation);
                    break;
                case "back" when !main:
                    if (!shown.DialogName.StartsWith("gamesettings-graphics", StringComparison.Ordinal))
                        throw new InvalidOperationException("Native Back requires original Graphics; Save/Cancel returns there first.");
                    var composite = ScenarioOptionsComposite(scenarioPauseMenu!);
                    var bounds = AccessTools.Field(typeof(GuiCompositeSettings), "backButtonBounds")?.GetValue(composite) as ElementBounds
                        ?? throw new MissingMemberException("Original Options Back bounds are absent.");
                    // Click the original closure: it also clears key selection and cancels hotkey capture.
                    ClickOptionsControl(shown, bounds, "original Back");
                    if (scenarioPauseMenu!.SingleComposer?.DialogName != "escapemenu")
                        throw new InvalidOperationException("Original Back did not restore pause-menu Home.");
                    break;
                case "resume":
                    if (shown.DialogName != "escapemenu")
                        throw new InvalidOperationException("Resume requires original pause-menu Home.");
                    var resume = AccessTools.Method(scenarioPauseMenu!.GetType(), "OnBackToGame", [])
                        ?? throw new MissingMemberException("Original resume callback is absent.");
                    if (resume.Invoke(scenarioPauseMenu, null) is not true || scenarioPauseMenu.IsOpened())
                        throw new InvalidOperationException("Original resume callback did not close the pause menu.");
                    break;
                case "back" when main:
                case "close" when main:
                    if (!scenarioMainOptions!.LeaveSettingsMenu())
                        throw new InvalidOperationException("Original main Options leave callback declined.");
                    scenarioMainOptionsOpen = false;
                    break;
                case "close":
                    // Retain the official Escape capture guard and OnGuiClosed unpause/preview restoration.
                    if (!scenarioPauseMenu!.OnEscapePressed() || scenarioPauseMenu.IsOpened())
                        throw new InvalidOperationException("Original Escape did not close Options; hotkey capture may still be active.");
                    break;
                default:
                    throw new InvalidOperationException("Unsupported Options lifecycle operation.");
            }
        }
        RequireScenarioContext();
    }

    /// <summary>Reads the unchanged host's settings composite for official entry and Back controls.</summary>
    /// <param name="host">Captured official dialog or screen.</param>
    /// <returns>The host's existing composite; an unsupported binding fails explicitly.</returns>
    private static GuiCompositeSettings ScenarioOptionsComposite(object host) =>
        AccessTools.Field(host.GetType(), "gameSettingsMenu")?.GetValue(host) as GuiCompositeSettings
            ?? throw new MissingMemberException("Original Options composite is absent.");

    /// <summary>Samples host and settings owners after this CPU frame; queued restoration may settle next tick.</summary>
    /// <returns>Displayed host/page, original pause state, requested preview, applied session state and actual persisted TAA.</returns>
    private (string Host, string? Page, bool? Paused, bool PauseOpen, bool Requested, bool Applied, bool Persisted)
        SnapshotScenarioOptions()
    {
        RequireScenarioContext();
        GuiComposer? shown = ScenarioOptionsComposer;
        string? page = shown == null ? null : OptionsSettingsOwner.DiagnosticPage(shown);
        if (page == null && shown != null)
            page = shown.DialogName.StartsWith("gamesettings-graphics", StringComparison.Ordinal) ? "Graphics"
                : shown.DialogName == "escapemenu" ? "Home" : null;
        string host = shown == null ? "none" : HeadlessHarnessOptions.Scenario!.Context;
        bool requested = JsonSerializer.Deserialize<RendererSettings>(RuntimeBootstrap.Current.ReadSettings())!.Taa;
        bool persisted = new RendererSettingsStore(services.DataPath).Load().Taa;
        return (host, page, scenarioOriginalWorld?.IsPaused, scenarioPauseMenu?.IsOpened() == true,
            requested, services.RendererSettings.Settings.Taa, persisted);
    }
}
