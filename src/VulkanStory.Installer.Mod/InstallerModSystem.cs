using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VulkanStory.Installer.Mod;

public sealed class InstallerModSystem : ModSystem
{
    private ICoreClientAPI? client;
    private InstallerDialog? dialog;
    private bool prompted;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;
    public override void StartClientSide(ICoreClientAPI api)
    {
        client = api;
        api.Event.LevelFinalize += WorldReady;
        api.Event.LeaveWorld += LeaveWorld;
        api.ChatCommands.GetOrCreate("vulkanstoryinstall")
            .WithDescription("Open the Vulkan Story installer")
            .HandleWith(_ => { Open(); return TextCommandResult.Success(); });
    }
    private void WorldReady()
    {
        if (prompted || File.Exists(Path.Combine(AppContext.BaseDirectory, "VulkanStory", "package.json"))) return;
        prompted = true;
        Open();
    }
    private void Open()
    {
        if (client == null) return;
        dialog ??= new InstallerDialog(client);
        dialog.TryOpen();
    }
    private void LeaveWorld() { dialog?.Dispose(); dialog = null; }
    public override void Dispose()
    {
        if (client != null) { client.Event.LevelFinalize -= WorldReady; client.Event.LeaveWorld -= LeaveWorld; }
        LeaveWorld();
        client = null;
    }
}
