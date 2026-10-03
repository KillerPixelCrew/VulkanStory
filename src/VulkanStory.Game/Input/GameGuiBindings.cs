using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

internal sealed class GameGuiBindings
{
    private readonly AccessTools.FieldRef<ScreenManager, GuiScreen> currentScreen;
    private readonly AccessTools.FieldRef<GuiScreenRunningGame, ClientMain> runningGame;
    private readonly AccessTools.FieldRef<ClientMain, List<GuiDialog>> loadedGuis;
    private readonly AccessTools.FieldRef<GuiElementEditableTextBase, double> caretX, leftOffset;

    internal GameGuiBindings()
    {
        Validate();
        currentScreen = AccessTools.FieldRefAccess<ScreenManager, GuiScreen>("CurrentScreen");
        runningGame = AccessTools.FieldRefAccess<GuiScreenRunningGame, ClientMain>("runningGame");
        loadedGuis = AccessTools.FieldRefAccess<ClientMain, List<GuiDialog>>("LoadedGuis");
        caretX = AccessTools.FieldRefAccess<GuiElementEditableTextBase, double>("caretX");
        leftOffset = AccessTools.FieldRefAccess<GuiElementEditableTextBase, double>("renderLeftOffset");
    }

    internal GuiScreen? CurrentScreen => ClientProgram.screenManager is { } manager ? currentScreen(manager) : null;
    internal ClientMain? RunningGame(GuiScreenRunningGame screen) => runningGame(screen);
    internal List<GuiDialog>? LoadedGuis(ClientMain game) => loadedGuis(game);
    internal double CaretX(GuiElementEditableTextBase text) => caretX(text);
    internal double LeftOffset(GuiElementEditableTextBase text) => leftOffset(text);

    internal static void Validate()
    {
        Field<ScreenManager, GuiScreen>("CurrentScreen");
        Field<GuiScreenRunningGame, ClientMain>("runningGame");
        Field<ClientMain, List<GuiDialog>>("LoadedGuis");
        Field<GuiElementEditableTextBase, double>("caretX");
        Field<GuiElementEditableTextBase, double>("renderLeftOffset");
    }

    private static void Field<TOwner, TValue>(string name)
    {
        FieldInfo? field = typeof(TOwner).GetField(name, BindingFlags.Instance |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (field is null || field.FieldType != typeof(TValue) || field.IsInitOnly)
            throw new MissingFieldException(typeof(TOwner).FullName, name);
    }
}
