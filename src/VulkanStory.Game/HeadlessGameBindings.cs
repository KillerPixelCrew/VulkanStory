using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Cached original client bindings used by the isolated headless harness to select screens and request ordinary shutdown.</summary>
internal static class HeadlessGameBindings
{
    private static readonly AccessTools.FieldRef<ScreenManager, GuiScreen?> CurrentScreen =
        AccessTools.FieldRefAccess<ScreenManager, GuiScreen?>("CurrentScreen");
    private static readonly AccessTools.FieldRef<GuiScreenRunningGame, ClientMain?> RunningGame =
        AccessTools.FieldRefAccess<GuiScreenRunningGame, ClientMain?>("runningGame");

    /// <summary>Checks original client lifecycle bindings used by the isolated harness before routing installation.</summary>
    internal static void Validate()
    {
        if (AccessTools.Field(typeof(ScreenManager), "CurrentScreen")?.FieldType != typeof(GuiScreen) ||
            AccessTools.Field(typeof(GuiScreenRunningGame), "runningGame")?.FieldType != typeof(ClientMain))
            throw new MissingFieldException("Official headless screen/client metadata changed.");
    }

    /// <summary>Finds the original world client only when the current screen is a running-game host.</summary>
    /// <returns>Original loaded client or null in menu/other screens.</returns>
    internal static ClientMain? CurrentRunningClient() => ClientProgram.screenManager is { } manager &&
        CurrentScreen(manager) is GuiScreenRunningGame screen ? RunningGame(screen) : null;
}
