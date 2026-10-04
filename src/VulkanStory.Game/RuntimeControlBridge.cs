using System.Collections.Concurrent;
using System.Text.Json;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Only framework delegates and JSON cross into the ordinary mod. It never loads
// game integration/renderer/native assemblies to create a second session.
internal static class RuntimeControlBridge
{
    internal static void Install(ProcessRuntime runtime)
    {
        AppContext.SetData("VulkanStory.Runtime.ReadSettings", (Func<string>)runtime.ReadSettings);
        AppContext.SetData("VulkanStory.Runtime.FpsText", (Func<string>)(() => runtime.IsActive ? runtime.Session.FpsText : "Renderer inactive"));
        AppContext.SetData("VulkanStory.Runtime.ShowFpsCounter", (Func<bool>)(() => runtime.IsActive && runtime.Session.ShowFpsCounter));
        AppContext.SetData("VulkanStory.Runtime.Presentation", (Func<string>)(() => runtime.IsActive ? runtime.Session.Presentation : "Renderer inactive: " + runtime.Status));
        AppContext.SetData("VulkanStory.Runtime.ApplySettings", (Func<string, string?>)runtime.SaveSettings);
        AppContext.SetData("VulkanStory.Runtime.PreviewSettings", (Func<string, string?>)runtime.PreviewSettings);
        AppContext.SetData("VulkanStory.Runtime.ReloadSettings", (Func<string?>)runtime.ReloadSettings);
        AppContext.SetData("VulkanStory.Runtime.WorldReady", (Action<object>)runtime.QueueWorldReady);
        AppContext.SetData("VulkanStory.Runtime.WorldLeft", (Action<object>)runtime.QueueWorldLeft);
        AppContext.SetData("VulkanStory.Runtime.ControllerSettings", (Func<string?>)runtime.RequestControllerSettings);
        AppContext.SetData("VulkanStory.Runtime.ControllerSettingsAfterSave",
            (Func<string, Func<bool>, Action<string?>, string?>)runtime.RequestControllerSettingsAfterSave);
        if (HeadlessHarnessOptions.Enabled)
            AppContext.SetData("VulkanStory.Runtime.DiagnosticOptions", (Func<string, string?>)runtime.RequestDiagnosticOptions);
    }
    internal static void Remove()
    {
        foreach (string key in new[] { "ReadSettings", "FpsText", "ShowFpsCounter", "Presentation", "ApplySettings", "PreviewSettings", "ReloadSettings", "WorldReady", "WorldLeft", "ControllerSettings", "ControllerSettingsAfterSave", "DiagnosticOptions" })
            AppContext.SetData("VulkanStory.Runtime." + key, null);
    }
}

internal sealed partial class ProcessRuntime
{
    private readonly ConcurrentQueue<Action> pendingControls = new();
    private RendererSettings? requestedSettings;
    private static readonly JsonSerializerOptions SettingsJson = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private GameSessionServices ControlServices => selectedServices ??= GameSessionServices.Load(GamePaths.DataPath);
    internal string? RequestControllerSettings()
    {
        if (!IsActive) return "VulkanStory renderer is inactive.";
        if (!ControlServices.RendererSettings.Settings.ControllerEnabled)
            return "Enable ControllerEnabled in VulkanStory settings first.";
        pendingControls.Enqueue(() => { if (IsActive) Session.OpenControllerSettings(); });
        return null;
    }
    internal string ReadSettings() => JsonSerializer.Serialize(Volatile.Read(ref requestedSettings) ?? ControlServices.RendererSettings.Settings, SettingsJson);
    internal string? RequestDiagnosticOptions(string action)
    {
        action = string.IsNullOrWhiteSpace(action) ? "open" : action.Trim().ToLowerInvariant();
        if (action is not ("open" or "save" or "cancel" or "inventory" or "inventory-cycles" or "controller-inventory" or "controller-radial" or "controller-gestures" or "controller-modifier")) return "Unknown inventory/options diagnostic action.";
        if (!HeadlessHarnessOptions.Enabled || !IsActive || Session.Temporal.CurrentClient is not { } world)
            return "Options diagnostics require an isolated harness world.";
        pendingControls.Enqueue(() =>
        {
            if (IsActive && ReferenceEquals(Session.Temporal.CurrentClient, world)) Session.OpenDiagnosticOptions(world, action);
        });
        return null;
    }
    internal string? RequestControllerSettingsAfterSave(string json, Func<bool> live, Action<string?> completed)
    {
        if (!IsActive || Session.Temporal.CurrentClient is not { } world)
            return "Controller settings require a loaded world.";
        if (!live()) return "The settings editor is no longer active.";
        try
        {
            RendererSettings next = (JsonSerializer.Deserialize<RendererSettings>(json, SettingsJson) ??
                throw new ArgumentException("Settings are empty.")).Normalize();
            if (!next.ControllerEnabled) return "Enable Controllers before opening controller settings.";
            new RendererSettingsStore(ControlServices.DataPath).Save(next);
            Volatile.Write(ref requestedSettings, next);
            pendingControls.Enqueue(() =>
            {
                if (!IsActive || !ReferenceEquals(Session.Temporal.CurrentClient, world))
                { completed("The settings editor or world closed. Saved settings remain persisted."); return; }
                try { Session.ApplyRendererSettings(next); }
                catch (Exception error)
                {
                    completed("Settings application failed: " + error.Message);
                    throw; // A failed provider drain must still stop dependent frame work.
                }
                if (!live()) { completed("Opening was cancelled. Saved settings remain applied."); return; }
                // Controller enablement/polling follows pending controls. Open at
                // the next boundary, after the device profile can be discovered.
                pendingControls.Enqueue(() =>
                {
                    if (!live() || !IsActive || !ReferenceEquals(Session.Temporal.CurrentClient, world))
                    { completed("The settings editor or world closed. Saved settings remain persisted."); return; }
                    if (!ControlServices.RendererSettings.Settings.ControllerEnabled)
                    { completed("Controllers were disabled before the panel opened."); return; }
                    string? openError;
                    try
                    {
                        openError = Session.TryOpenControllerSettings() && Session.OpenedControllerSettings(world) != null
                            ? null : "Connect a controller to open controller settings. Saved settings remain applied.";
                    }
                    catch (Exception error) { openError = "Controller settings could not open: " + error.Message; }
                    completed(openError);
                });
            });
            return null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { return error.Message; }
    }
    internal string? PreviewSettings(string json)
    {
        try
        {
            RendererSettings next = (JsonSerializer.Deserialize<RendererSettings>(json, SettingsJson) ??
                throw new ArgumentException("Settings are empty.")).Normalize();
            var services = ControlServices;
            Volatile.Write(ref requestedSettings, next);
            pendingControls.Enqueue(() =>
            {
                // Slider events can arrive faster than frame boundaries. Apply only
                // the latest request, not every intermediate resource rebuild.
                if (!ReferenceEquals(Volatile.Read(ref requestedSettings), next)) return;
                if (IsActive) Session.ApplyRendererSettings(next);
                else services.RendererSettings.Apply(next);
            });
            return null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return error.Message; }
    }
    internal string? SaveSettings(string json)
    {
        try
        {
            RendererSettings next = (JsonSerializer.Deserialize<RendererSettings>(json, SettingsJson) ??
                throw new ArgumentException("Settings are empty.")).Normalize();
            var services = ControlServices;
            new RendererSettingsStore(services.DataPath).Save(next);
            Volatile.Write(ref requestedSettings, next);
            pendingControls.Enqueue(() =>
            {
                if (IsActive) Session.ApplyRendererSettings(next);
                else services.RendererSettings.Apply(next);
            });
            return null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { return error.Message; }
    }
    internal string? ReloadSettings()
    {
        try
        {
            var services = ControlServices;
            RendererSettings next = new RendererSettingsStore(services.DataPath).Load();
            Volatile.Write(ref requestedSettings, next);
            pendingControls.Enqueue(() =>
            {
                if (IsActive) Session.ApplyRendererSettings(next);
                else services.RendererSettings.Apply(next);
            });
            return null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { return error.Message; }
    }
    internal void QueueWorldReady(object world) => pendingControls.Enqueue(() =>
    {
        if (IsActive && world is ClientMain client && client.Platform is ClientPlatformWindows platform && Session.Matches(platform))
            Session.NoteWorldReady(client);
    });
    internal void QueueWorldLeft(object world) => pendingControls.Enqueue(() =>
    {
        if (IsActive && world is ClientMain client) Session.NoteWorldLeft(client);
    });
    internal void ApplyPendingControls()
    {
        RequireOwner();
        // Snapshot the count so callbacks queued during processing wait for the
        // next boundary instead of extending this input frame indefinitely.
        int count = pendingControls.Count;
        for (int index = 0; index < count && pendingControls.TryDequeue(out var action); index++) action();
    }
    internal void ClearPendingControls() { while (pendingControls.TryDequeue(out _)) { } }
}
