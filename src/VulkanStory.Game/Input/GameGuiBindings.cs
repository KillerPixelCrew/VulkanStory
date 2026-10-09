using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

/// <summary>Validated cached accessors for the pinned game's private GUI/screen and caret fields.</summary>
internal sealed class GameGuiBindings
{
    private readonly AccessTools.FieldRef<ScreenManager, GuiScreen> currentScreen;
    private readonly AccessTools.FieldRef<GuiScreenRunningGame, ClientMain> runningGame;
    private readonly AccessTools.FieldRef<ClientMain, List<GuiDialog>> loadedGuis;
    private readonly AccessTools.FieldRef<GuiElementEditableTextBase, double> caretX, leftOffset;

    /// <summary>Validates field shapes before creating reusable accessors, avoiding repeated reflection in input paths.</summary>
    internal GameGuiBindings()
    {
        Validate();
        currentScreen = AccessTools.FieldRefAccess<ScreenManager, GuiScreen>("CurrentScreen");
        runningGame = AccessTools.FieldRefAccess<GuiScreenRunningGame, ClientMain>("runningGame");
        loadedGuis = AccessTools.FieldRefAccess<ClientMain, List<GuiDialog>>("LoadedGuis");
        caretX = AccessTools.FieldRefAccess<GuiElementEditableTextBase, double>("caretX");
        leftOffset = AccessTools.FieldRefAccess<GuiElementEditableTextBase, double>("renderLeftOffset");
    }

    /// <summary>Current screen, or null before the game's screen manager exists.</summary>
    internal GuiScreen? CurrentScreen => ClientProgram.screenManager is { } manager ? currentScreen(manager) : null;
    /// <summary>Current screen of a specific screen manager, such as the one registered as a platform key handler.</summary>
    internal GuiScreen? CurrentScreenOf(ScreenManager manager) => currentScreen(manager);
    /// <summary>Client instance associated with a running-game screen.</summary>
    internal ClientMain? RunningGame(GuiScreenRunningGame screen) => runningGame(screen);
    /// <summary>The game's live loaded-dialog list; callers do not own this collection.</summary>
    internal List<GuiDialog>? LoadedGuis(ClientMain game) => loadedGuis(game);
    /// <summary>Editable field caret X offset used to locate the SDL IME rectangle.</summary>
    internal double CaretX(GuiElementEditableTextBase text) => caretX(text);
    /// <summary>Editable field render-left offset used to locate the SDL IME rectangle.</summary>
    internal double LeftOffset(GuiElementEditableTextBase text) => leftOffset(text);

    /// <summary>Checks all required private GUI fields without instantiating their owners.</summary>
    /// <exception cref="MissingFieldException">A required declared field is absent, readonly, or has a changed type.</exception>
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
