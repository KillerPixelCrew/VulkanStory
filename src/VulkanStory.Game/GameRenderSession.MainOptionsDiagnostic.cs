using System.Runtime.InteropServices;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Game.Input;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private GuiScreenSettings? mainOptionsDiagnostic;
    private int mainOptionsWarmup;
    private readonly string mainOptionsAction = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_MAIN_ACTION") ?? "open";
    private (bool Before, bool Expected)? mainActionValues;
    private bool mainActionAwaitFrame = true, mainActionComplete;
    /// <summary>Advances the explicitly selected original-main-menu Options diagnostic from the session event loop.</summary>
    private void PrepareMainOptionsDiagnostic()
    {
        if (headlessDone) return;
        if (Temporal.CurrentClient != null || HeadlessGameBindings.CurrentRunningClient() != null)
            throw new InvalidOperationException("Main-menu diagnostic must not have a running world.");
        if (mainOptionsDiagnostic == null)
        {
            var parent = new GameGuiBindings().CurrentScreen;
            if (parent?.IsOpened != true || !parent.ShowMainMenu ||
                !Graphics.TerrainShadersReady) return;
            if (++mainOptionsWarmup < 30) return;
            mainOptionsDiagnostic = new GuiScreenSettings(parent.ScreenManager, parent);
            parent.ScreenManager.LoadScreen(mainOptionsDiagnostic);
            var landing = mainOptionsDiagnostic.ElementComposer;
            var entry = landing.GetToggleButton("vulkanstory")
                ?? throw new InvalidOperationException("Main-menu Options has no VulkanStory entry.");
            ClickOptionsControl(landing, entry.Bounds, "main-menu VulkanStory entry");
            if (ReferenceEquals(mainOptionsDiagnostic.ElementComposer, landing) ||
                !mainOptionsDiagnostic.ElementComposer.DialogName.StartsWith("gamesettings-vulkanstory-", StringComparison.Ordinal))
                throw new InvalidOperationException("Main-menu entry did not display VulkanStory Options.");
            if (mainOptionsAction is not ("open" or "save" or "cancel"))
                throw new InvalidOperationException("Unknown main-menu Options action.");
            if (mainOptionsAction == "open")
                SelectDiagnosticOptionsPage(mainOptionsDiagnostic.ElementComposer, () => mainOptionsDiagnostic.ElementComposer);
            if (mainOptionsAction != "open")
                mainActionValues = ClickOptionsTaaAction(mainOptionsDiagnostic.ElementComposer, mainOptionsAction);
            headlessWorldFrame = -1; // In this explicit mode the counter denotes menu frames only.
        }
        if (!mainOptionsDiagnostic.IsOpened) throw new InvalidOperationException("Main-menu Options host was replaced.");
        headlessWorldFrame++;
    }
    private void CompleteMainOptionsAction()
    {
        if (!HeadlessHarnessOptions.MainMenuOptions || mainActionValues is not { } values || mainActionComplete) return;
        if (mainActionAwaitFrame) { mainActionAwaitFrame = false; return; }
        if (mainOptionsDiagnostic?.IsOpened != true ||
            !mainOptionsDiagnostic.ElementComposer.DialogName.StartsWith("gamesettings-graphics", StringComparison.Ordinal))
            throw new InvalidOperationException("Main-menu action did not return to Graphics.");
        var requested = JsonSerializer.Deserialize<RendererSettings>(RuntimeBootstrap.Current.ReadSettings())!;
        var persisted = new RendererSettingsStore(services.DataPath).Load();
        bool applied = services.RendererSettings.Settings.Taa;
        if (requested.Taa != values.Expected || persisted.Taa != values.Expected || applied != values.Expected)
            throw new InvalidOperationException("Main-menu Options action did not preserve the expected TAA state.");
        string directory = HeadlessHarnessOptions.FrameDirectory ?? throw new InvalidOperationException("Main-menu action directory is absent.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "main-options-action.json"), JsonSerializer.Serialize(new
        {
            success = true, action = mainOptionsAction, before = values.Before, expected = values.Expected,
            requested = requested.Taa, persisted = persisted.Taa, applied,
            returned = mainOptionsDiagnostic.ElementComposer.DialogName
        }));
        mainActionComplete = true;
    }
    private void CaptureMainOptionsDiagnostic()
    {
        if (headlessDone || mainOptionsDiagnostic == null || headlessWorldFrame < 0) return;
        if (HeadlessHarnessOptions.ShouldCapture(headlessWorldFrame))
        {
            Graphics.LoadFramebuffer(EnumFrameBuffer.Default);
            var size = Graphics.CaptureDisplaySize();
            byte[] pixels = new byte[checked(size.Width * size.Height * 4)];
            GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { Graphics.ReadCapturePixels(0, 0, size.Width, size.Height, pin.AddrOfPinnedObject()); }
            finally { pin.Free(); }
            if (HeadlessHarnessOptions.WriteFrame(headlessWorldFrame, size.Width, size.Height, pixels, true))
            { WriteHeadlessPng(headlessWorldFrame, size.Width, size.Height, pixels); headlessWritten++; }
        }
        if (!HeadlessHarnessOptions.CaptureFinished(headlessWorldFrame)) return;
        if (MultiplierDiagnosticRequested && !multiplierDiagnosticComplete)
            throw new InvalidOperationException("Main-menu multiplier drag/Save acknowledgement is missing.");
        if (mainOptionsAction != "open" && !mainActionComplete) throw new InvalidOperationException("Main-menu action acknowledgement is missing.");
        var directory = HeadlessHarnessOptions.FrameDirectory ?? throw new InvalidOperationException("Main-menu capture directory is absent.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "main-options-result.json"), JsonSerializer.Serialize(new
        {
            success = headlessWritten == HeadlessHarnessOptions.Frames.Length, context = "main", action = mainOptionsAction,
            hidden = !Window.IsVisible, focused = Window.IsFocused, hasWorld = Temporal.CurrentClient != null,
            host = mainOptionsDiagnostic.GetType().FullName, displayed = mainOptionsDiagnostic.ElementComposer.DialogName,
            gameAssembly = typeof(GameRenderSession).Assembly.Location, pid = Environment.ProcessId,
            menuFrame = headlessWorldFrame, requested = HeadlessHarnessOptions.Frames.Length, written = headlessWritten
        }));
        headlessDone = true;
        if (HeadlessHarnessOptions.ExitWhenDone) Input.RequestWindowExit();
    }
}
