using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private enum ScenarioWorldCyclePhase { AwaitMenu, AwaitWorld }

    private sealed class ScenarioWorldCycle(HeadlessScenarioAction action, StartServerArgs savedArgs,
        ClientMain departedClient)
    {
        internal HeadlessScenarioAction Action { get; } = action;
        internal StartServerArgs SavedArgs { get; } = savedArgs;
        internal ClientMain DepartedClient { get; } = departedClient;
        internal ScenarioWorldCyclePhase Phase { get; set; } = ScenarioWorldCyclePhase.AwaitMenu;
    }

    private ScenarioWorldCycle? scenarioWorldCycle;
    private bool ScenarioWorldCyclePending => scenarioWorldCycle != null;

    /// <summary>Exits the captured world through the game's normal soft-exit path without replacing the process session.</summary>
    private void StartScenarioWorldCycle(HeadlessScenarioAction action)
    {
        RequireScenarioContext();
        if (HeadlessHarnessOptions.Scenario!.Context != "world" || scenarioTick != action.Tick ||
            scenarioOriginalScreen is not GuiScreenRunningGame screen ||
            scenarioOriginalWorld is not { IsSingleplayer: true } departed)
            throw new InvalidOperationException("World cycle requires its declared tick and original singleplayer host.");
        if (scenarioWorldCycle != null || scenarioExecutedActions.Contains(action.Index))
            throw new InvalidOperationException("World cycle was already started: " + action.Id);
        StartServerArgs savedArgs = screen.serverargs?.Clone()
            ?? throw new InvalidOperationException("World cycle has no original singleplayer arguments.");
        scenarioWorldCycle = new ScenarioWorldCycle(action, savedArgs, departed);
        scenarioTick = null;
        try { screen.ExitOrRedirect(false, "headless world cycle", EnumExitMode.SoftExit); }
        finally
        {
            // The old screen and pause dialog also retain the departing client.
            scenarioOriginalScreen = null;
            scenarioOriginalWorld = null;
            scenarioPauseMenu = null;
        }
        WriteScenarioEvent(action, "beforeInput", true, false, "world exit requested", null);
    }

    /// <summary>Waits for a complete menu boundary, then reloads the same isolated world once and verifies its new client.</summary>
    private void AdvanceScenarioWorldCycle(HeadlessScenario scenario)
    {
        ScenarioWorldCycle cycle = scenarioWorldCycle
            ?? throw new InvalidOperationException("World cycle is not pending.");
        if (cycle.Phase == ScenarioWorldCyclePhase.AwaitMenu)
        {
            if (platform.IsServerRunning || scenarioGui?.CurrentScreen is not { IsOpened: true, ShowMainMenu: true } ||
                Temporal.CurrentClient != null || HeadlessGameBindings.CurrentRunningClient() != null || diagnosticWorldReady)
                return;
            WriteScenarioEvent(cycle.Action, "beforeInput", true, false, new
            {
                transition = "menuObserved", serverRunning = false, worldReady = false,
                temporalClientAttached = false, runningClientAttached = false
            }, null);
            // Set the phase before the official call: neither failures nor later frames retry it.
            cycle.Phase = ScenarioWorldCyclePhase.AwaitWorld;
            ClientProgram.screenManager.ConnectToSingleplayer(cycle.SavedArgs);
            return;
        }

        if (HeadlessGameBindings.CurrentRunningClient() is not { BlocksReceivedAndLoaded: true } current ||
            !diagnosticWorldReady || scenarioGui?.CurrentScreen is not GuiScreenRunningGame { IsOpened: true } ||
            !ReferenceEquals(Temporal.CurrentClient, current))
            return;
        if (ReferenceEquals(current, cycle.DepartedClient))
            throw new InvalidOperationException("World cycle reattached the departed client.");
        if (!ReferenceEquals(current.Platform, platform))
            throw new InvalidOperationException("World cycle changed its process platform.");
        if (!TryStartScenarioContext(scenario)) return;
        RequireScenarioContext();
        scenarioTick = cycle.Action.Tick;
        WriteScenarioEvent(cycle.Action, "beforeInput", true, true, new
        {
            transition = "worldReloaded", menuObserved = true, freshClient = true,
            clientIdentitiesMatch = true, worldReady = diagnosticWorldReady,
            samePlatform = true, processId = Environment.ProcessId
        }, null);
        if (!scenarioExecutedActions.Add(cycle.Action.Index))
            throw new InvalidOperationException("World cycle was executed more than once: " + cycle.Action.Id);
        ClearScenarioWorldCycle();
        scenarioFailurePhase = "renderCycle";
    }

    private void ClearScenarioWorldCycle() => scenarioWorldCycle = null;
}
