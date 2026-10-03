using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VulkanStory.Mod;

// This lightweight entry must remain loadable when the early payload is absent.
// Runtime status uses framework data; renderer/game/native dependencies are not
// loaded a second time through the ordinary mod scanner.
public sealed class VulkanStoryModSystem : ModSystem
{
    private ICoreClientAPI? client;
    private RendererSettingsDialog? settings;
    private RendererFpsHud? fpsHud;
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;
    public override void StartClientSide(ICoreClientAPI api)
    {
        client = api;
        api.Event.LevelFinalize += WorldReady;
        api.Event.LeaveWorld += WorldLeft;
        var commandOwner = new WeakReference<VulkanStoryModSystem>(this);
        api.ChatCommands.GetOrCreate("vulkanstory")
            .WithDescription("VulkanStory settings, controller panel, status and set <setting> <value>")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("settings|controller|status|reload|set"),
                api.ChatCommands.Parsers.OptionalWord("setting"), api.ChatCommands.Parsers.OptionalAll("value"))
            // The public API has no unregister operation. Keep the registry
            // handler independent of this owner's lifetime and allow reattachment.
            .HandleWith(args => commandOwner.TryGetTarget(out var owner)
                ? owner.Command(args)
                : TextCommandResult.Error("VulkanStory client mod is unavailable."));
        string? status = AppContext.GetData("VulkanStory.Runtime.Status") as string;
        if (status == "active")
            api.Logger.Notification("VulkanStory: Vulkan renderer and SDL runtime active.");
        else if (status is null)
            api.Logger.Warning("VulkanStory: early runtime is missing. Install the runtime package beside the original game executable and restart.");
        else
            api.Logger.Warning("VulkanStory: renderer inactive ({0}). See the VulkanStory startup log.", status);
    }
    private void WorldReady()
    {
        if (client != null) { fpsHud ??= new RendererFpsHud(client); fpsHud.TryOpen(); }
        if (client != null && AppContext.GetData("VulkanStory.Runtime.WorldReady") is Action<object> callback)
            callback(client.World);
    }
    private void WorldLeft()
    {
        settings?.TryClose(); settings?.Dispose(); settings = null;
        fpsHud?.TryClose(); fpsHud?.Dispose(); fpsHud = null;
        if (client != null && AppContext.GetData("VulkanStory.Runtime.WorldLeft") is Action<object> callback)
            callback(client.World);
    }
    private TextCommandResult Command(TextCommandCallingArgs args)
    {
        if (client == null) return TextCommandResult.Error("VulkanStory client mod is unavailable.");
        string action = (args[0] as string ?? "settings").ToLowerInvariant();
        if (action == "diagnostic-options")
        {
            if (AppContext.GetData("VulkanStory.Runtime.DiagnosticOptions") is not Func<string?> inspect)
                return TextCommandResult.Error("Options diagnostics are available only in an isolated harness client.");
            string? error = inspect();
            return error == null ? TextCommandResult.Success("Options diagnostic queued.") : TextCommandResult.Error(error);
        }
        if (action == "controller")
        {
            if (AppContext.GetData("VulkanStory.Runtime.ControllerSettings") is not System.Func<string?> open)
                return TextCommandResult.Error("VulkanStory controller settings are unavailable.");
            string? error = open();
            return error == null ? TextCommandResult.Success("Controller settings requested.") : TextCommandResult.Error(error);
        }
        if (action == "set")
        {
            if (args[1] is not string key || string.IsNullOrWhiteSpace(key) ||
                args[2] is not string value || string.IsNullOrWhiteSpace(value))
                return TextCommandResult.Error("Use .vulkanstory set <setting> <value>.");
            if (AppContext.GetData("VulkanStory.Runtime.ReadSettings") is not System.Func<string> read ||
                AppContext.GetData("VulkanStory.Runtime.ApplySettings") is not System.Func<string, string?> apply)
                return TextCommandResult.Error("VulkanStory runtime is unavailable.");
            try
            {
                string? error = apply(RendererSettingCommand.Update(read(), key, value));
                return error == null ? TextCommandResult.Success("VulkanStory setting saved and queued. Device/window options require restart.")
                    : TextCommandResult.Error(error);
            }
            catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException)
            { return TextCommandResult.Error(error.Message); }
        }
        if (string.IsNullOrEmpty(action) || action == "settings")
        {
            if (AppContext.GetData("VulkanStory.Runtime.ReadSettings") is not System.Func<string> read ||
                AppContext.GetData("VulkanStory.Runtime.ApplySettings") is not System.Func<string, string?> apply)
            { return TextCommandResult.Error("VulkanStory runtime is unavailable. Restart after installing or enabling it."); }
            settings?.TryClose(); settings?.Dispose();
            settings = new RendererSettingsDialog(client, read(), apply);
            return settings.TryOpen() ? TextCommandResult.Success()
                : TextCommandResult.Error("VulkanStory settings could not be opened.");
        }
        if (action == "reload")
        {
            if (AppContext.GetData("VulkanStory.Runtime.ReloadSettings") is not System.Func<string?> reload)
            { return TextCommandResult.Error("VulkanStory runtime is unavailable. Restart after installing or enabling it."); }
            string? error = reload();
            return error == null
                ? TextCommandResult.Success("VulkanStory settings reloaded. Window and device options take effect after restarting.")
                : TextCommandResult.Error("VulkanStory settings could not be reloaded: " + error);
        }
        return action == "status"
            ? TextCommandResult.Success(AppContext.GetData("VulkanStory.Runtime.Presentation") is System.Func<string> presentation
                ? presentation() : "VulkanStory: " + (AppContext.GetData("VulkanStory.Runtime.Status") as string ?? "not loaded"))
            : TextCommandResult.Error("Use .vulkanstory [settings|controller|status|reload|set <setting> <value>].");
    }
    public override void Dispose()
    {
        settings?.TryClose(); settings?.Dispose(); settings = null;
        fpsHud?.TryClose(); fpsHud?.Dispose(); fpsHud = null;
        if (client != null)
        {
            client.Event.LevelFinalize -= WorldReady; client.Event.LeaveWorld -= WorldLeft;
        }
        client = null;
        base.Dispose();
    }
}
