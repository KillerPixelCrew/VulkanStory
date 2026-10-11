using Vintagestory.API.Client;

namespace VulkanStory.Installer.Mod;

internal sealed class InstallerDialog : GuiDialog
{
    private readonly CancellationTokenSource cancellation = new();
    private string status = "Download the latest Vulkan Story release and install it into this game folder?\n\nAfter installation, close the game and start it again to activate Vulkan Story.";
    private int dirty;
    private bool busy, finished;
    public InstallerDialog(ICoreClientAPI api) : base(api) => Compose();
    public override string ToggleKeyCombinationCode => null!;
    public override bool DisableMouseGrab => true;
    public override void OnRenderGUI(float deltaTime)
    {
        if (Interlocked.Exchange(ref dirty, 0) != 0) Compose();
        base.OnRenderGUI(deltaTime);
    }
    private void Compose()
    {
        ClearComposers();
        var bounds = ElementBounds.Fixed(0, 0, 500, 230).WithFixedPadding(GuiStyle.ElementToDialogPadding);
        var composer = capi.Gui.CreateCompo("vulkanstory-installer", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(bounds).AddDialogTitleBar("Vulkan Story Installer", () => TryClose())
            .BeginChildElements(bounds)
            .AddStaticText(status, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 40, 500, 130));
        if (!busy && !finished)
            composer.AddSmallButton("Install", () => { BeginInstall(); return true; }, ElementBounds.Fixed(0, 190, 120, 30));
        composer.AddSmallButton(busy ? "Cancel download" : finished ? "Close" : "Later", () =>
        {
            if (busy) cancellation.Cancel();
            else TryClose();
            return true;
        }, ElementBounds.Fixed(300, 190, 200, 30));
        SingleComposer = composer.EndChildElements().Compose();
    }
    private async void BeginInstall()
    {
        if (busy || cancellation.IsCancellationRequested) return;
        busy = true;
        Update("Checking the latest release…");
        try
        {
            string version = await RuntimeDownload.InstallAsync(AppContext.BaseDirectory, Update, cancellation.Token);
            finished = true;
            Update($"Vulkan Story {version} is installed.\n\nClose the game and start it again to activate it. You can now remove the installer mod.");
        }
        catch (OperationCanceledException) { finished = true; Update("Installation cancelled. Close this window and re-enter the world to try again."); }
        catch (Exception error) { Update("Installation failed: " + error.Message + "\n\nYou can retry or download the release manually from GitHub."); }
        finally { busy = false; Interlocked.Exchange(ref dirty, 1); }
    }
    private void Update(string message) { status = message; Interlocked.Exchange(ref dirty, 1); }
    public override void Dispose() { cancellation.Cancel(); base.Dispose(); }
}
