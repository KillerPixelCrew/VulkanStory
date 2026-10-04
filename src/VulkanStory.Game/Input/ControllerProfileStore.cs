using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VulkanStory.Game.Input;

/// <summary>Editable controller settings. Device entries inherit the default on first use.</summary>
internal sealed class ControllerProfile
{
    public string Name { get; set; } = "Default";
    public float MoveDeadzone { get; set; } = 0.16f;
    public float MoveOuterDeadzone { get; set; }
    public float MoveCurveExponent { get; set; } = 0.5f;
    public float MovePressThreshold { get; set; } = 0.28f;
    public float MoveReleaseThreshold { get; set; } = 0.18f;
    public float LookDeadzone { get; set; } = 0.16f;
    public float LookOuterDeadzone { get; set; }
    public bool MenuCursorUsesLeftStick { get; set; } = true;
    public float LookSensitivity { get; set; } = 820f;
    public float CursorSensitivity { get; set; } = 900f;
    public float LookCurveExponent { get; set; } = 1.5f;
    public float TriggerThreshold { get; set; } = 0.5f;
    public int MoveXAxis { get; set; } = 0;
    public int MoveYAxis { get; set; } = 1;
    public int LookXAxis { get; set; } = 2;
    public int LookYAxis { get; set; } = 3;
    public bool InvertMoveX { get; set; }
    public bool InvertMoveY { get; set; }
    public bool InvertLookX { get; set; }
    public bool ToggleSneak { get; set; } = true;
    public bool InvertLookY { get; set; }
    public bool GyroEnabled { get; set; }
    public bool GyroRequireSecondaryTrigger { get; set; } = true;
    public float GyroSensitivity { get; set; } = 500f;
    public float GyroDeadzone { get; set; } = 0.02f;
    public bool InvertGyroX { get; set; }
    public bool InvertGyroY { get; set; }
    public bool RumbleEnabled { get; set; }
    public bool TriggerRumbleEnabled { get; set; }
    public float RumbleStrength { get; set; } = 0.18f;
    public float TriggerRumbleStrength { get; set; } = 0.14f;
    public int RumbleDurationMs { get; set; } = 70;
    public int AcceptButton { get; set; } = 0;
    public int BackButton { get; set; } = 1;
    public int DropButton { get; set; } = 12;
    public int InventoryButton { get; set; } = 3;
    public int CraftingButton { get; set; } = 2;
    public int TakeHalfButton { get; set; } = 2;
    public int QuickMoveButton { get; set; } = 3;
    public int GuiSelectButton { get; set; } = 0;
    public int GuiBackButton { get; set; } = 1;
    public int RadialButton { get; set; } = 8;
    public bool RadialEnabled { get; set; } = true;
    public bool ModifierEnabled { get; set; }
    public int ModifierButton { get; set; } = 9;
    public int TapThresholdMs { get; set; } = 350;
    public int LongPressThresholdMs { get; set; } = 700;
    public Dictionary<string, ControllerGestureMode> ActionModes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ModifierBindings { get; set; } = new(StringComparer.Ordinal);
    public bool RadialHoldToOpen { get; set; } = true;
    public string[] RadialActions { get; set; } = ["inventory", "menu", "settings", "drop", "previous", "next", "firstslot", "screenshot"];
    public int MenuButton { get; set; } = 6;
    public int SettingsButton { get; set; } = 4;
    public int SneakButton { get; set; } = 1;
    public int SprintButton { get; set; } = 7;
    public int PreviousHotbarButton { get; set; } = 9;
    public int NextHotbarButton { get; set; } = 10;
    public int PrimaryTriggerAxis { get; set; } = 5;
    public int SecondaryTriggerAxis { get; set; } = 4;
    public bool PrimaryTriggerNegative { get; set; }
    public bool SecondaryTriggerNegative { get; set; }

    public ControllerProfile Validated() => new()
    {
        Name = Name ?? "Controller",
        MoveDeadzone = Math.Clamp(MoveDeadzone, 0f, 0.8f),
        MoveOuterDeadzone = Math.Clamp(MoveOuterDeadzone, 0f, 0.2f),
        MoveCurveExponent = Math.Clamp(MoveCurveExponent, 0.5f, 3f),
        MovePressThreshold = Math.Clamp(MovePressThreshold, 0.05f, 0.95f),
        MoveReleaseThreshold = Math.Clamp(Math.Min(MoveReleaseThreshold, MovePressThreshold), 0f, 0.9f),
        LookDeadzone = Math.Clamp(LookDeadzone, 0f, 0.8f),
        LookOuterDeadzone = Math.Clamp(LookOuterDeadzone, 0f, 0.2f),
        MenuCursorUsesLeftStick = MenuCursorUsesLeftStick,
        LookSensitivity = Math.Clamp(LookSensitivity, 20f, 3000f),
        CursorSensitivity = Math.Clamp(CursorSensitivity, 20f, 3000f),
        LookCurveExponent = Math.Clamp(LookCurveExponent, 1f, 3f),
        TriggerThreshold = Math.Clamp(TriggerThreshold, 0.05f, 0.95f),
        MoveXAxis = AxisOrDefault(MoveXAxis, 0),
        MoveYAxis = AxisOrDefault(MoveYAxis, 1),
        LookXAxis = AxisOrDefault(LookXAxis, 2),
        LookYAxis = AxisOrDefault(LookYAxis, 3),
        InvertMoveX = InvertMoveX,
        InvertMoveY = InvertMoveY,
        InvertLookX = InvertLookX,
        ToggleSneak = ToggleSneak,
        InvertLookY = InvertLookY,
        GyroEnabled = GyroEnabled,
        GyroRequireSecondaryTrigger = GyroRequireSecondaryTrigger,
        GyroSensitivity = Math.Clamp(GyroSensitivity, 20f, 3000f),
        GyroDeadzone = Math.Clamp(GyroDeadzone, 0f, 0.5f),
        InvertGyroX = InvertGyroX,
        InvertGyroY = InvertGyroY,
        RumbleEnabled = RumbleEnabled,
        TriggerRumbleEnabled = TriggerRumbleEnabled,
        RumbleStrength = Math.Clamp(RumbleStrength, 0f, 1f),
        TriggerRumbleStrength = Math.Clamp(TriggerRumbleStrength, 0f, 1f),
        RumbleDurationMs = Math.Clamp(RumbleDurationMs, 20, 250),
        AcceptButton = ButtonOrDefault(AcceptButton, 0),
        BackButton = ButtonOrDefault(BackButton, 1),
        DropButton = ButtonOrDefault(DropButton, 12),
        InventoryButton = ButtonOrDefault(InventoryButton, 3),
        CraftingButton = ButtonOrDefault(CraftingButton, 2),
        TakeHalfButton = ButtonOrDefault(TakeHalfButton, 2),
        QuickMoveButton = ButtonOrDefault(QuickMoveButton, 3),
        GuiSelectButton = ButtonOrDefault(GuiSelectButton, 0),
        GuiBackButton = ButtonOrDefault(GuiBackButton, 1),
        RadialButton = ButtonOrDefault(RadialButton, 8),
        RadialEnabled = RadialEnabled,
        ModifierEnabled = ModifierEnabled,
        ModifierButton = ButtonOrDefault(ModifierButton, 9),
        TapThresholdMs = Math.Clamp(TapThresholdMs, 100, 1000),
        LongPressThresholdMs = Math.Clamp(LongPressThresholdMs, Math.Clamp(TapThresholdMs, 100, 1000) + 50, 2000),
        ActionModes = ValidatedModes(),
        ModifierBindings = ValidatedModifierBindings(),
        RadialHoldToOpen = RadialHoldToOpen,
        RadialActions = ValidatedRadialActions(),
        MenuButton = ButtonOrDefault(MenuButton, 6),
        SettingsButton = ButtonOrDefault(SettingsButton, 4),
        SneakButton = ButtonOrDefault(SneakButton, 1),
        SprintButton = ButtonOrDefault(SprintButton, 7),
        PreviousHotbarButton = ButtonOrDefault(PreviousHotbarButton, 9),
        NextHotbarButton = ButtonOrDefault(NextHotbarButton, 10),
        PrimaryTriggerAxis = AxisOrDefault(PrimaryTriggerAxis, 5),
        SecondaryTriggerAxis = AxisOrDefault(SecondaryTriggerAxis, 4),
        PrimaryTriggerNegative = PrimaryTriggerNegative,
        SecondaryTriggerNegative = SecondaryTriggerNegative
    };

    private static int ButtonOrDefault(int value, int fallback) => value is >= 0 and <= 31 ? value : fallback;
    private static int AxisOrDefault(int value, int fallback) => value is >= 0 and <= 5 ? value : fallback;
    private string[] ValidatedRadialActions()
    {
        var actions = new string[8];
        for (int i = 0; i < actions.Length; i++)
        {
            string? action = RadialActions != null && i < RadialActions.Length ? RadialActions[i] : null;
            actions[i] = action != null && Array.IndexOf(ControllerRadialDialog.Actions, action) >= 0 ? action : "none";
        }
        return actions;
    }

    private Dictionary<string, ControllerGestureMode> ValidatedModes()
    {
        var result = new Dictionary<string, ControllerGestureMode>(StringComparer.Ordinal);
        if (ActionModes != null)
            foreach (var pair in ActionModes)
                if (Enum.IsDefined(pair.Value)) result[pair.Key] = pair.Value;
        return result;
    }

    private Dictionary<string, int> ValidatedModifierBindings()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (ModifierBindings != null)
            foreach (var pair in ModifierBindings)
                if (pair.Value is >= 0 and <= 31 && pair.Value != ButtonOrDefault(ModifierButton, 9))
                    ControllerLayerBindings.Assign(result, pair.Key, pair.Value);
        return result;
    }
}

internal sealed class ControllerProfileStore
{
    public int Version { get; set; } = 3;
    public ControllerProfile Default { get; set; } = new();
    public Dictionary<string, ControllerProfile> Devices { get; set; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DeviceKey(ushort vendor, ushort product, string name, string? serial)
    {
        string identity = vendor != 0 || product != 0 ? $"{vendor:x4}:{product:x4}" : name.Trim();
        if (identity.Length == 0) identity = "unknown-gamepad";
        if (!string.IsNullOrWhiteSpace(serial))
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(serial));
            identity += ":" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }
        return identity;
    }

    public static ControllerProfile LoadDevice(string path, string key, string name)
    {
        ControllerProfileStore store = Read(path);

        if (!store.Devices.TryGetValue(key, out ControllerProfile? profile) || profile == null)
        {
            profile = store.Default.Validated();
            profile.Name = name;
            store.Devices[key] = profile;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(store, JsonOptions) + Environment.NewLine);
        }
        return profile.Validated();
    }

    public static void SaveDevice(string path, string key, ControllerProfile profile)
    {
        ControllerProfileStore store = Read(path);
        store.Devices[key] = profile.Validated();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(store, JsonOptions) + Environment.NewLine);
    }

    private static ControllerProfileStore Read(string path)
    {
        if (!File.Exists(path)) return new ControllerProfileStore();
        ControllerProfileStore store = JsonSerializer.Deserialize<ControllerProfileStore>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Controller profile file is empty");
        if (store.Version is not (1 or 2 or 3) || store.Default == null || store.Devices == null)
            throw new InvalidDataException("Unsupported controller profile format");
        if (store.Version == 1)
        {
            // Version 1 applied a hidden 0.75 multiplier to the look exponent.
            // Store the effective exponent so the tuning slider describes it.
            store.Default.LookCurveExponent = Math.Max(1f, store.Default.LookCurveExponent * 0.75f);
            store.Default.GuiSelectButton = store.Default.AcceptButton;
            store.Default.GuiBackButton = store.Default.BackButton;
            foreach (ControllerProfile profile in store.Devices.Values)
                if (profile != null)
                {
                    profile.LookCurveExponent = Math.Max(1f, profile.LookCurveExponent * 0.75f);
                    profile.GuiSelectButton = profile.AcceptButton;
                    profile.GuiBackButton = profile.BackButton;
                }
            store.Version = 2;
        }
        // Existing device entries contain explicit old defaults, so changing
        // property initializers alone would leave the user's controls reversed.
        UpgradeLegacyBindings(store.Default);
        foreach (ControllerProfile profile in store.Devices.Values)
            if (profile != null) UpgradeLegacyBindings(profile);
        if (store.Version < 3)
        {
            DisableConflictingRadial(store.Default);
            foreach (ControllerProfile profile in store.Devices.Values)
                if (profile != null) DisableConflictingRadial(profile);
            store.Version = 3;
        }
        return store;
    }

    private static void DisableConflictingRadial(ControllerProfile profile)
    {
        profile.RadialEnabled = !Array.Exists(ControllerButtonBindings.All,
            binding => binding.Code is not ("radial" or "back") && binding.Get(profile) == profile.RadialButton);
    }

    private static void UpgradeLegacyBindings(ControllerProfile profile)
    {
        // Only the former default layout is upgraded; keep user-remapped actions.
        if (profile.AcceptButton != 0 || profile.BackButton != 1 || profile.DropButton != 2 ||
            profile.InventoryButton != 3 || profile.MenuButton != 6 || profile.SettingsButton != 4 ||
            profile.SneakButton != 7 || profile.SprintButton != 8 ||
            profile.PreviousHotbarButton != 9 || profile.NextHotbarButton != 10 ||
            profile.PrimaryTriggerAxis != 4 || profile.SecondaryTriggerAxis != 5 ||
            profile.PrimaryTriggerNegative || profile.SecondaryTriggerNegative) return;
        profile.PrimaryTriggerAxis = 5;
        profile.SecondaryTriggerAxis = 4;
        profile.SneakButton = 1;
        profile.SprintButton = 7;
        profile.DropButton = 12;
        profile.CraftingButton = 2;
        profile.ToggleSneak = true;
    }
}
