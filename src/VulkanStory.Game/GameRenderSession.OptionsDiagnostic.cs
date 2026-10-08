using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;
using VulkanStory.Settings;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private sealed record PendingOptionsAction(ClientMain World, GuiDialog Parent, string Action, bool Before, bool Expected,
        bool AwaitNextFrame = true);
    private PendingOptionsAction? pendingOptionsAction;
    private sealed record PendingMultiplier(System.Func<GuiComposer?> Current, ClientMain? World, int Before,
        bool Clicked = false, bool Saved = false, bool AwaitFrame = true);
    private PendingMultiplier? pendingMultiplier;
    private bool multiplierDiagnosticComplete;
    private bool MultiplierDiagnosticRequested =>
        HeadlessHarnessOptions.Enabled && Environment.GetEnvironmentVariable("VULKANSTORY_OPTIONS_MULTIPLIER_CHECK") == "1";
    private (System.Func<GuiComposer?> Current, string Expected)? pendingOptionsPage;
    private string DiagnosticOptionsPage => Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_OPTIONS_PAGE") ?? "Image";
    private ClientMain? inventoryCycleWorld;
    private int inventoryCycleRemaining;
    private long inventoryCycleNextFrame;

    private void DriveInventoryCyclesBeforeInput()
    {
        if (inventoryCycleWorld is not { } world || headlessWorldFrame < inventoryCycleNextFrame) return;
        if (inventoryCycleRemaining == 0)
        {
            platform.Logger.Notification("[VulkanStory] Inventory cycles settled: {0}", Device.ResourceMemoryDiagnostics());
            inventoryCycleWorld = null;
            return;
        }
        var hotkey = world.api.Input.GetHotKeyByCode("inventorydialog")
            ?? throw new InvalidOperationException("Original inventory hotkey is absent.");
        if (hotkey.Handler == null || !hotkey.Handler(hotkey.CurrentMapping))
            throw new InvalidOperationException("Original inventory cycle hotkey declined.");
        bool opened = new GameGuiBindings().LoadedGuis(world)?.Any(dialog =>
            dialog.GetType().Name == "GuiDialogInventory" && dialog.IsOpened()) == true;
        bool expected = inventoryCycleRemaining % 2 == 0;
        if (opened != expected) throw new InvalidOperationException("Inventory cycle transition failed.");
        int step = 41 - inventoryCycleRemaining--;
        platform.Logger.Notification("[VulkanStory] Inventory cycle {0}/40 opened={1}: {2}",
            step, opened, Device.ResourceMemoryDiagnostics());
        inventoryCycleNextFrame = headlessWorldFrame + 20;
    }
    internal void OpenDiagnosticOptions(ClientMain world, string action = "open")
    {
        RequireActive();
        if (!HeadlessHarnessOptions.Enabled) throw new InvalidOperationException("Options diagnostic is harness-only.");
        if (action is "controller-gestures" or "controller-modifier")
        {
            controllers?.OpenDiagnosticSettings(world, action == "controller-gestures" ? 6 : 8);
            platform.Logger.Notification("[VulkanStory] Controller settings diagnostic opened: {0}", action);
            return;
        }
        if (action == "controller-radial")
        {
            StartControllerRadialDiagnostic(world);
            return;
        }
        if (action == "controller-inventory")
        {
            StartControllerInventoryDiagnostic(world);
            return;
        }
        if (action == "inventory-cycles")
        {
            inventoryCycleWorld = world;
            inventoryCycleRemaining = 40;
            inventoryCycleNextFrame = headlessWorldFrame;
            platform.Logger.Notification("[VulkanStory] Inventory cycles baseline: {0}", Device.ResourceMemoryDiagnostics());
            return;
        }
        if (action == "inventory")
        {
            var hotkey = world.api.Input.GetHotKeyByCode("inventorydialog")
                ?? throw new InvalidOperationException("Original inventory hotkey is absent.");
            if (hotkey.Handler == null || !hotkey.Handler(hotkey.CurrentMapping))
                throw new InvalidOperationException("Original inventory hotkey declined.");
            bool opened = new GameGuiBindings().LoadedGuis(world)?.Any(dialog =>
                dialog.GetType().Name == "GuiDialogInventory" && dialog.IsOpened()) == true;
            if (!opened) throw new InvalidOperationException("Original inventory dialog did not open.");
            platform.Logger.Notification("[VulkanStory] Inventory diagnostic: original inventory dialog opened.");
            return;
        }
        try
        {
            if (pendingOptionsAction != null) throw new InvalidOperationException("An Options action is already pending.");
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
            if (action == "open") SelectDiagnosticOptionsPage(shown, () => parent.SingleComposer);
            if (action != "open")
            {
                var values = ClickOptionsTaaAction(shown, action);
                pendingOptionsAction = new(world, parent, action, values.Before, values.Expected);
            }
        }
        catch (Exception error)
        {
            platform.Logger.Error("VulkanStory Options diagnostic failed: {0}", error.GetBaseException().Message);
            FailHeadlessRun("Options diagnostic failed: " + error.GetBaseException().Message, "optionsDiagnostic");
        }
    }
    private void SelectDiagnosticOptionsPage(GuiComposer composer, System.Func<GuiComposer?> current)
    {
        int index = Array.IndexOf(RendererSettingsPanel.PageNames, DiagnosticOptionsPage);
        if (index < 0) throw new InvalidOperationException("Unknown Options diagnostic page.");
        if (index != 0)
        {
            var bounds = composer.GetButton("vulkanstory-page-" + DiagnosticOptionsPage).Bounds;
            ClickOptionsControl(composer, bounds, DiagnosticOptionsPage + " page");
        }
        pendingOptionsPage = (current, DiagnosticOptionsPage);
        if (MultiplierDiagnosticRequested && DiagnosticOptionsPage != "Generation")
            throw new InvalidOperationException("Multiplier diagnostic requires the Generation page.");
    }
    private (bool Before, bool Expected) ClickOptionsTaaAction(GuiComposer shown, string action)
    {
        bool before = JsonSerializer.Deserialize<RendererSettings>(RuntimeBootstrap.Current.ReadSettings())!.Taa;
        ClickOptionsControl(shown, shown.GetSwitch("Taa").Bounds, "TAA switch");
        ClickOptionsFooter(shown, action);
        return (before, action == "save" ? !before : before);
    }
    private void ClickOptionsFooter(GuiComposer shown, string action, bool throughInput = false)
    {
        var background = shown.GetScrollbar("vulkanstory-scroll").Bounds.ParentBounds;
        double contentWidth = background.fixedWidth - 30;
        var footer = ElementBounds.Fixed(contentWidth - (action == "save" ? 120 : 260),
            background.fixedHeight - 30, 120, 30).WithParent(background);
        footer.CalcWorldBounds();
        if (throughInput)
        {
            int x = (int)(footer.renderX + footer.OuterWidth / 2);
            int y = (int)(footer.renderY + footer.OuterHeight / 2);
            var pixels = Window.PixelSize;
            if (x < 0 || x >= pixels.Width || y < 0 || y >= pixels.Height)
                throw new InvalidOperationException("Options footer is outside the viewport.");
            DiagnosticMouseMotion(x, y, 0);
            try { DiagnosticMouseButton(true, x, y); }
            finally { DiagnosticMouseButton(false, x, y); }
        }
        else ClickOptionsControl(shown, footer, action);
    }
    private void ClickOptionsControl(GuiComposer composer, ElementBounds bounds, string control)
    {
        int x = (int)(bounds.renderX + bounds.OuterWidth / 2);
        int y = (int)(bounds.renderY + bounds.OuterHeight / 2);
        var pixels = Window.PixelSize;
        if (x < 0 || x >= pixels.Width || y < 0 || y >= pixels.Height)
            throw new InvalidOperationException("Options " + control + " center is outside the window.");
        composer.OnMouseDown(new MouseEvent(x, y, EnumMouseButton.Left, 0));
        composer.OnMouseUp(new MouseEvent(x, y, EnumMouseButton.Left, 0));
    }
    /// <summary>Completes pending original Options actions after rendering so persisted and applied settings can be compared.</summary>
    private void CompleteOptionsDiagnostic()
    {
        if (pendingOptionsPage is { } selected)
        {
            pendingOptionsPage = null;
            var displayed = selected.Current();
            if (displayed == null || OptionsSettingsOwner.DiagnosticPage(displayed) != selected.Expected)
                throw new InvalidOperationException("Options page click did not display " + selected.Expected + ".");
            var directory = HeadlessHarnessOptions.FrameDirectory!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "options-page.json"), JsonSerializer.Serialize(new
            { success = true, expected = selected.Expected, observed = OptionsSettingsOwner.DiagnosticPage(displayed), displayed = displayed.DialogName }));
            if (MultiplierDiagnosticRequested)
                pendingMultiplier = new(selected.Current, Temporal.CurrentClient,
                    services.RendererSettings.Settings.FrameGenerationMultiplier);
        }
        CompleteMultiplierDiagnostic();
        CompleteMainOptionsAction();
        if (pendingOptionsAction is not { } pending) return;
        if (pending.AwaitNextFrame)
        {
            pendingOptionsAction = pending with { AwaitNextFrame = false };
            return; // Save's queued application runs at the next pre-input boundary.
        }
        pendingOptionsAction = null;
        try
        {
            if (!ReferenceEquals(Temporal.CurrentClient, pending.World) || !pending.Parent.IsOpened() ||
                pending.Parent.SingleComposer?.DialogName.StartsWith("gamesettings-graphics", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Options action did not return to the original Graphics composer.");
            var result = CheckCapturedTaa(pending.Expected, pending.Action);
            if (HeadlessHarnessOptions.FrameDirectory is { } directory)
                File.WriteAllText(Path.Combine(directory, "options-action.json"), JsonSerializer.Serialize(new
                {
                    success = true, action = pending.Action, before = pending.Before, expected = pending.Expected,
                    requested = result.Requested, persisted = result.Persisted, applied = result.Applied,
                    returned = pending.Parent.SingleComposer.DialogName
                }));
        }
        catch (Exception error) { FailHeadlessRun("Options action failed: " + error.GetBaseException().Message, "optionsDiagnostic"); }
    }
    private void DriveMultiplierDiagnosticBeforeInput()
    {
        if (pendingMultiplier is not { } pending || pending.Saved) return;
        GuiComposer shown = pending.Current() ?? throw new InvalidOperationException("Multiplier Options host closed.");
        if (!ReferenceEquals(Temporal.CurrentClient, pending.World) || OptionsSettingsOwner.DiagnosticPage(shown) != "Generation")
            throw new InvalidOperationException("Multiplier diagnostic lost its original Options page/world.");
        if (!pending.Clicked)
        {
            var slider = shown.GetSlider("FrameGenerationMultiplier");
            int y = (int)(slider.Bounds.renderY + slider.Bounds.OuterHeight / 2);
            int start = (int)(slider.Bounds.renderX + slider.Bounds.OuterWidth / 2);
            int end = (int)(slider.Bounds.renderX + slider.Bounds.OuterWidth - 2);
            var pixels = Window.PixelSize;
            if (start < 0 || end >= pixels.Width || y < 0 || y >= pixels.Height)
                throw new InvalidOperationException("Multiplier slider is outside the viewport.");
            // Widgets consult api.Input mouse coordinates, not just MouseEvent.
            // Use the same game input bridge as SDL to update and dispatch both.
            DiagnosticMouseMotion(start, y, 0);
            try
            {
                DiagnosticMouseButton(true, start, y);
                DiagnosticMouseMotion(end, y, end - start);
            }
            finally { DiagnosticMouseButton(false, end, y); }
            int observed = slider.GetValue();
            if (observed != 6)
                throw new InvalidOperationException("Multiplier input-bridge drag selected " + observed + ", expected 6.");
            pendingMultiplier = pending with { Clicked = true };
            return;
        }
        // Allow a rendered/captured 6x page before exercising Save.
        if (headlessWorldFrame < 120) return;
        ClickOptionsFooter(shown, "save", throughInput: true);
        pendingMultiplier = pending with { Saved = true };
    }
    private void CompleteMultiplierDiagnostic()
    {
        if (pendingMultiplier is not { Saved: true } pending) return;
        if (pending.AwaitFrame) { pendingMultiplier = pending with { AwaitFrame = false }; return; }
        var shown = pending.Current();
        if (!ReferenceEquals(Temporal.CurrentClient, pending.World) ||
            shown?.DialogName.StartsWith("gamesettings-graphics", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Multiplier Save did not return to original Graphics.");
        int requested = JsonSerializer.Deserialize<RendererSettings>(RuntimeBootstrap.Current.ReadSettings())!.FrameGenerationMultiplier;
        int persisted = new RendererSettingsStore(services.DataPath).Load().FrameGenerationMultiplier;
        int applied = services.RendererSettings.Settings.FrameGenerationMultiplier;
        if (requested != 6 || persisted != 6 || applied != 6)
            throw new InvalidOperationException("Multiplier Save did not persist/apply 6x.");
        File.WriteAllText(Path.Combine(HeadlessHarnessOptions.FrameDirectory!, "options-multiplier.json"), JsonSerializer.Serialize(new
        { success = true, context = pending.World == null ? "main" : "world", before = pending.Before,
            sliderValue = 6, requested, persisted, applied, returned = shown.DialogName,
            inputRoute = pending.World == null ? "screen-dispatch" : "hidden-client-processed",
            hidden = !Window.IsVisible, focused = Window.IsFocused }));
        pendingMultiplier = null;
        multiplierDiagnosticComplete = true;
    }
    private ClientMain? HiddenDiagnosticClient => MultiplierDiagnosticRequested &&
        !Window.IsVisible && !Window.IsFocused ? Temporal.CurrentClient : null;
    private void DiagnosticMouseMotion(int x, int y, int deltaX)
    {
        Input.Input.InjectPhysicalMouseMotion(x, y, deltaX, 0);
        // The running-game screen rejects unfocused OS input. Hidden diagnostic
        // events enter the owned client route without changing that focus policy.
        if (HiddenDiagnosticClient is { } world)
        {
            world.OnMouseMove(new MouseEvent(x, y, deltaX, 0));
            if (world.MouseCurrentX != x || world.MouseCurrentY != y)
                throw new InvalidOperationException("Hidden Options cursor was not accepted by the client input route; grabbed=" + world.MouseGrabbed + ".");
        }
    }
    private void DiagnosticMouseButton(bool down, int x, int y)
    {
        Input.Input.InjectPhysicalMouseButton(EnumMouseButton.Left, down, x, y);
        HiddenDiagnosticClient?.UpdateMouseButtonState(EnumMouseButton.Left, down);
    }
}
