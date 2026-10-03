using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class HeadlessGameBindings
{
    private static readonly AccessTools.FieldRef<ScreenManager, GuiScreen?> CurrentScreen =
        AccessTools.FieldRefAccess<ScreenManager, GuiScreen?>("CurrentScreen");
    private static readonly AccessTools.FieldRef<GuiScreenRunningGame, ClientMain?> RunningGame =
        AccessTools.FieldRefAccess<GuiScreenRunningGame, ClientMain?>("runningGame");

    internal static void Validate()
    {
        if (AccessTools.Field(typeof(ScreenManager), "CurrentScreen")?.FieldType != typeof(GuiScreen) ||
            AccessTools.Field(typeof(GuiScreenRunningGame), "runningGame")?.FieldType != typeof(ClientMain))
            throw new MissingFieldException("Official headless screen/client metadata changed.");
    }

    internal static ClientMain? CurrentRunningClient() => ClientProgram.screenManager is { } manager &&
        CurrentScreen(manager) is GuiScreenRunningGame screen ? RunningGame(screen) : null;
}
