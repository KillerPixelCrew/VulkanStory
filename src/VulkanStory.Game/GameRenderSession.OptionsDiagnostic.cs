using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    internal void OpenDiagnosticOptions(ClientMain world)
    {
        RequireActive();
        if (!HeadlessHarnessOptions.Enabled) throw new InvalidOperationException("Options diagnostic is harness-only.");
        try
        {
            Type escapeType = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogEscapeMenu")
                ?? throw new MissingMemberException("Original pause dialog type is absent.");
            GuiDialog parent = new GameGuiBindings().LoadedGuis(world)?.SingleOrDefault(dialog => dialog.GetType() == escapeType)
                ?? throw new InvalidOperationException("Original world pause dialog is absent.");
            if (!parent.IsOpened() && !parent.TryOpen()) throw new InvalidOperationException("Original pause dialog could not open.");
            var field = AccessTools.Field(escapeType, "gameSettingsMenu");
            var composite = field?.GetValue(parent) as GuiCompositeSettings
                ?? throw new MissingMemberException("Original pause Options composite is absent.");
            MethodInfo open = AccessTools.Method(typeof(GuiCompositeSettings), "OpenSettingsMenu", [])
                ?? throw new MissingMemberException("Original Options entry is absent.");
            if (open.Invoke(composite, null) is not true) throw new InvalidOperationException("Original Options entry declined.");
            GuiComposer landing = parent.SingleComposer;
            var button = landing.GetToggleButton("vulkanstory")
                ?? throw new InvalidOperationException("VulkanStory tab is absent from original Options.");
            int x = (int)(button.Bounds.renderX + button.Bounds.OuterWidth / 2);
            int y = (int)(button.Bounds.renderY + button.Bounds.OuterHeight / 2);
            var pixels = Window.PixelSize;
            if (x < 0 || x >= pixels.Width || y < 0 || y >= pixels.Height)
                throw new InvalidOperationException("VulkanStory entry center is outside the window: " + x + "," + y);
            // Exercise the normal composer click path, including the entry's
            // displayed-owner guard. Never call the custom panel constructor.
            landing.OnMouseDown(new MouseEvent(x, y, EnumMouseButton.Left, 0));
            landing.OnMouseUp(new MouseEvent(x, y, EnumMouseButton.Left, 0));
            GuiComposer shown = parent.SingleComposer;
            if (ReferenceEquals(shown, landing) || !shown.Composed ||
                !shown.DialogName.StartsWith("gamesettings-vulkanstory-", StringComparison.Ordinal))
                throw new InvalidOperationException("Click did not display the VulkanStory Options composer.");
            if (HeadlessHarnessOptions.FrameDirectory is { } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "options-diagnostic.json"), JsonSerializer.Serialize(new
                {
                    success = true, context = "world", page = "Image", world = world.GetType().FullName,
                    parent = parent.GetType().FullName, landing = landing.DialogName, displayed = shown.DialogName,
                    entryX = x, entryY = y, width = pixels.Width, height = pixels.Height,
                    hidden = !Window.IsVisible, focused = Window.IsFocused
                }));
            }
            platform.Logger.Notification("VulkanStory Options diagnostic: original pause Options click displayed {0}.", shown.DialogName);
        }
        catch (Exception error)
        {
            platform.Logger.Error("VulkanStory Options diagnostic failed: {0}", error.GetBaseException().Message);
            FailHeadlessRun("Options diagnostic failed: " + error.GetBaseException().Message, "optionsDiagnostic");
        }
    }
}
