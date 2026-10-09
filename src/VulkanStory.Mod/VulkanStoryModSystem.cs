using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VulkanStory.Mod;

// This lightweight entry must remain loadable when the early payload is absent.
// Runtime status uses framework data; renderer/game/native dependencies are not
// loaded a second time through the ordinary mod scanner.
/// <summary>API-only client mod entry attaching settings, status commands, FPS HUD, and world callbacks to the early process runtime.</summary>
/// <remarks>Runtime access uses framework callbacks so missing early renderer dependencies do not prevent status reporting.</remarks>
public sealed class VulkanStoryModSystem : ModSystem
{
    private ICoreClientAPI? client;
    private RendererSettingsDialog? settings;
    private RendererFpsHud? fpsHud;
    /// <inheritdoc />
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;
    /// <inheritdoc />
    /// <remarks>Registers world events and a weak-owner chat handler, then reports the early runtime's current status.</remarks>
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
    /// <summary>Opens the FPS HUD and forwards world readiness to the already loaded runtime when its callback exists.</summary>
    private void WorldReady()
    {
        if (client != null) { fpsHud ??= new RendererFpsHud(client); fpsHud.TryOpen(); }
        if (client != null && AppContext.GetData("VulkanStory.Runtime.WorldReady") is Action<object> callback)
            callback(client.World);
    }
    /// <summary>Closes owned GUI/HUD objects and forwards departure to the early runtime.</summary>
    private void WorldLeft()
    {
        CloseOwnedGui();
        if (client != null && AppContext.GetData("VulkanStory.Runtime.WorldLeft") is Action<object> callback)
            callback(client.World);
    }
    /// <summary>Closes and disposes the owned settings dialog and FPS HUD.</summary>
    private void CloseOwnedGui()
    {
        settings?.TryClose(); settings?.Dispose(); settings = null;
        fpsHud?.TryClose(); fpsHud?.Dispose(); fpsHud = null;
    }
    /// <summary>Reads the runtime's settings read/apply callbacks when the early runtime published them.</summary>
    private static bool TryGetSettingsCallbacks(out System.Func<string> read, out System.Func<string, string?> apply)
    {
        read = null!; apply = null!;
        if (AppContext.GetData("VulkanStory.Runtime.ReadSettings") is not System.Func<string> reader ||
            AppContext.GetData("VulkanStory.Runtime.ApplySettings") is not System.Func<string, string?> applier)
            return false;
        read = reader; apply = applier;
        return true;
    }
    /// <summary>Dispatches renderer settings/status/controller commands through process callbacks and returns user-visible availability/errors.</summary>
    private TextCommandResult Command(TextCommandCallingArgs args)
    {
        if (client == null) return TextCommandResult.Error("VulkanStory client mod is unavailable.");
        string action = (args[0] as string ?? "settings").ToLowerInvariant();
        if (action == "diagnostic-options")
        {
            if (AppContext.GetData("VulkanStory.Runtime.DiagnosticOptions") is not System.Func<string, string?> inspect)
                return TextCommandResult.Error("Options diagnostics are available only in an isolated harness client.");
            string? error = inspect(args[1] as string ?? "open");
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
            if (!TryGetSettingsCallbacks(out var read, out var apply))
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
            if (!TryGetSettingsCallbacks(out var read, out var apply))
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
    /// <inheritdoc />
    /// <remarks>Disposes owned GUI/HUD objects and detaches world events; the weak chat handler can remain in the public registry.</remarks>
    public override void Dispose()
    {
        CloseOwnedGui();
        if (client != null)
        {
            client.Event.LevelFinalize -= WorldReady; client.Event.LeaveWorld -= WorldLeft;
        }
        client = null;
        base.Dispose();
    }
}
