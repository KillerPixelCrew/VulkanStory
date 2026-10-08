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
    /// <summary>Device/profile display name shown by controller settings.</summary>
    public string Name { get; set; } = "Default";
    /// <summary>Movement stick's radial idle radius.</summary>
    public float MoveDeadzone { get; set; } = 0.16f;
    /// <summary>Movement stick's outer saturation band, coupled to the inner deadzone during validation.</summary>
    public float MoveOuterDeadzone { get; set; }
    /// <summary>Exponent shaping movement magnitude after radial deadzone processing.</summary>
    public float MoveCurveExponent { get; set; } = 0.5f;
    /// <summary>Digital movement activation threshold when negotiated analog walking is unavailable.</summary>
    public float MovePressThreshold { get; set; } = 0.28f;
    /// <summary>Digital movement release threshold providing hysteresis below the press threshold.</summary>
    public float MoveReleaseThreshold { get; set; } = 0.18f;
    /// <summary>Look stick's radial idle radius.</summary>
    public float LookDeadzone { get; set; } = 0.16f;
    /// <summary>Look stick's outer saturation band, coupled to the inner deadzone during validation.</summary>
    public float LookOuterDeadzone { get; set; }
    /// <summary>Whether menu cursor movement uses the movement stick and scrolling uses the look stick.</summary>
    public bool MenuCursorUsesLeftStick { get; set; } = true;
    /// <summary>Scale for integrated synthetic mouse look before the retained 1.5 multiplier.</summary>
    public float LookSensitivity { get; set; } = 820f;
    /// <summary>Menu cursor travel rate applied to the processed stick and frame duration.</summary>
    public float CursorSensitivity { get; set; } = 900f;
    /// <summary>Exponent shaping processed look-stick magnitude.</summary>
    public float LookCurveExponent { get; set; } = 1.5f;
    /// <summary>Normalized signed-axis threshold for primary and secondary trigger actions.</summary>
    public float TriggerThreshold { get; set; } = 0.5f;
    /// <summary>SDL axis used for horizontal movement.</summary>
    public int MoveXAxis { get; set; } = 0;
    /// <summary>SDL axis used for forward/back movement.</summary>
    public int MoveYAxis { get; set; } = 1;
    /// <summary>SDL axis used for horizontal look.</summary>
    public int LookXAxis { get; set; } = 2;
    /// <summary>SDL axis used for vertical look.</summary>
    public int LookYAxis { get; set; } = 3;
    /// <summary>Reverses the movement X axis.</summary>
    public bool InvertMoveX { get; set; }
    /// <summary>Reverses the movement Y axis.</summary>
    public bool InvertMoveY { get; set; }
    /// <summary>Reverses synthetic horizontal stick look.</summary>
    public bool InvertLookX { get; set; }
    /// <summary>Enables the default sneak latch when no explicit sneak gesture mode overrides it.</summary>
    public bool ToggleSneak { get; set; } = true;
    /// <summary>Reverses synthetic vertical stick look.</summary>
    public bool InvertLookY { get; set; }
    /// <summary>Requests gyro aiming when the selected SDL device provides the sensor.</summary>
    public bool GyroEnabled { get; set; }
    /// <summary>Restricts gyro aiming to samples holding the effective secondary trigger.</summary>
    public bool GyroRequireSecondaryTrigger { get; set; } = true;
    /// <summary>Scale applied to integrated gyro angular rates for synthetic look.</summary>
    public float GyroSensitivity { get; set; } = 500f;
    /// <summary>Absolute gyro angular-rate threshold below which motion is ignored.</summary>
    public float GyroDeadzone { get; set; } = 0.02f;
    /// <summary>Reverses gyro yaw contribution to synthetic look.</summary>
    public bool InvertGyroX { get; set; }
    /// <summary>Reverses gyro pitch contribution to synthetic look.</summary>
    public bool InvertGyroY { get; set; }
    /// <summary>Requests ordinary controller-motor feedback for actions and player damage.</summary>
    public bool RumbleEnabled { get; set; }
    /// <summary>Requests physical trigger-motor feedback when the bound action uses a standard trigger axis.</summary>
    public bool TriggerRumbleEnabled { get; set; }
    /// <summary>Normalized ordinary motor strength.</summary>
    public float RumbleStrength { get; set; } = 0.18f;
    /// <summary>Normalized trigger motor strength.</summary>
    public float TriggerRumbleStrength { get; set; } = 0.14f;
    /// <summary>Action feedback pulse length in milliseconds; damage feedback uses its separate bounded duration.</summary>
    public int RumbleDurationMs { get; set; } = 70;
    /// <summary>World jump/accept physical button.</summary>
    public int AcceptButton { get; set; } = 0;
    /// <summary>Legacy world/back button retained for schema migration.</summary>
    public int BackButton { get; set; } = 1;
    /// <summary>World drop-item physical button.</summary>
    public int DropButton { get; set; } = 12;
    /// <summary>World inventory-open physical button.</summary>
    public int InventoryButton { get; set; } = 3;
    /// <summary>World crafting shortcut button; routes to the game's inventory dialog.</summary>
    public int CraftingButton { get; set; } = 2;
    /// <summary>GUI half-stack/secondary-slot action button.</summary>
    public int TakeHalfButton { get; set; } = 2;
    /// <summary>GUI quick-transfer action button.</summary>
    public int QuickMoveButton { get; set; } = 3;
    /// <summary>GUI select/accept physical button.</summary>
    public int GuiSelectButton { get; set; } = 0;
    /// <summary>GUI back/close physical button.</summary>
    public int GuiBackButton { get; set; } = 1;
    /// <summary>Physical button opening/confirming the configured radial wheel.</summary>
    public int RadialButton { get; set; } = 8;
    /// <summary>Whether gameplay may open the radial action wheel.</summary>
    public bool RadialEnabled { get; set; } = true;
    /// <summary>Whether holding the modifier activates the shifted world-action layer.</summary>
    public bool ModifierEnabled { get; set; }
    /// <summary>Physical button reserved for the modifier layer.</summary>
    public int ModifierButton { get; set; } = 9;
    /// <summary>Gesture tap-duration and second-press window in milliseconds.</summary>
    public int TapThresholdMs { get; set; } = 350;
    /// <summary>Gesture long-press duration in milliseconds, validated above the tap window.</summary>
    public int LongPressThresholdMs { get; set; } = 700;
    /// <summary>Explicit gesture overrides by world action or gui-prefixed action code.</summary>
    public Dictionary<string, ControllerGestureMode> ActionModes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Explicit shifted world-action physical buttons; validation removes unsupported/reserved entries.</summary>
    public Dictionary<string, int> ModifierBindings { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Whether releasing the radial button confirms its selection rather than a later button press.</summary>
    public bool RadialHoldToOpen { get; set; } = true;
    /// <summary>Eight clockwise action tokens starting at the wheel's top sector.</summary>
    public string[] RadialActions { get; set; } = ["inventory", "menu", "settings", "drop", "previous", "next", "firstslot", "screenshot"];
    /// <summary>Gameplay pause/menu physical button.</summary>
    public int MenuButton { get; set; } = 6;
    /// <summary>Controller settings physical button.</summary>
    public int SettingsButton { get; set; } = 4;
    /// <summary>Gameplay sneak/Shift physical button.</summary>
    public int SneakButton { get; set; } = 1;
    /// <summary>Gameplay sprint/Ctrl physical button.</summary>
    public int SprintButton { get; set; } = 7;
    /// <summary>Previous hotbar slot or backward GUI tab physical button.</summary>
    public int PreviousHotbarButton { get; set; } = 9;
    /// <summary>Next hotbar slot or forward GUI tab physical button.</summary>
    public int NextHotbarButton { get; set; } = 10;
    /// <summary>SDL signed axis driving the primary mouse action.</summary>
    public int PrimaryTriggerAxis { get; set; } = 5;
    /// <summary>SDL signed axis driving the secondary mouse action.</summary>
    public int SecondaryTriggerAxis { get; set; } = 4;
    /// <summary>Whether negative primary-axis travel activates the action.</summary>
    public bool PrimaryTriggerNegative { get; set; }
    /// <summary>Whether negative secondary-axis travel activates the action.</summary>
    public bool SecondaryTriggerNegative { get; set; }

    /// <summary>Creates a bounded copy with validated axes/buttons, coupled deadzones/timing, and copied gesture/radial collections.</summary>
    /// <remarks>This does not mutate the source profile. Numeric clamping follows each setting's current persisted contract.</remarks>
    public ControllerProfile Validated() => new()
    {
        Name = Name ?? "Controller",
        MoveDeadzone = Math.Clamp(MoveDeadzone, 0f, 0.8f),
        MoveOuterDeadzone = Math.Clamp(MoveOuterDeadzone, 0f, Math.Min(0.2f, 0.95f - Math.Clamp(MoveDeadzone, 0f, 0.8f))),
        MoveCurveExponent = Math.Clamp(MoveCurveExponent, 0.5f, 3f),
        MovePressThreshold = Math.Clamp(MovePressThreshold, 0.05f, 0.95f),
        MoveReleaseThreshold = Math.Clamp(Math.Min(MoveReleaseThreshold, MovePressThreshold), 0f, 0.9f),
        LookDeadzone = Math.Clamp(LookDeadzone, 0f, 0.8f),
        LookOuterDeadzone = Math.Clamp(LookOuterDeadzone, 0f, Math.Min(0.2f, 0.95f - Math.Clamp(LookDeadzone, 0f, 0.8f))),
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
                if (pair.Value is >= 0 and <= 31 && pair.Value != ButtonOrDefault(ModifierButton, 9) &&
                    Array.Exists(ControllerButtonBindings.All, binding => binding.Code == pair.Key &&
                        binding.Code is not ("back" or "radial" or "settings")))
                    ControllerLayerBindings.Assign(result, pair.Key, pair.Value);
        return result;
    }
}

/// <summary>Versioned default/per-device controller configuration with legacy migration and temporary-file replacement writes.</summary>
internal sealed class ControllerProfileStore
{
    /// <summary>Stored schema version; current writes use three.</summary>
    public int Version { get; set; } = 3;
    /// <summary>Profile copied and named when a device is first encountered.</summary>
    public ControllerProfile Default { get; set; } = new();
    /// <summary>Device-keyed explicit profiles retained independently of default edits.</summary>
    public Dictionary<string, ControllerProfile> Devices { get; set; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Builds a device key from vendor/product or name, optionally appending a truncated hash of the serial.</summary>
    /// <remarks>The raw serial is not persisted in the key; without a serial, matching device identities share a profile.</remarks>
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

    /// <summary>Loads a validated device profile, creating and persisting a named copy of the default on first use.</summary>
    /// <remarks>Read, schema, JSON, and write failures propagate to the caller's existing error handling.</remarks>
    public static ControllerProfile LoadDevice(string path, string key, string name)
    {
        ControllerProfileStore store = Read(path);

        if (!store.Devices.TryGetValue(key, out ControllerProfile? profile) || profile == null)
        {
            profile = store.Default.Validated();
            profile.Name = name;
            store.Devices[key] = profile;
            Write(path, store);
        }
        return profile.Validated();
    }

    /// <summary>Reads/migrates the store, replaces one device entry with a validated copy, and writes the store.</summary>
    public static void SaveDevice(string path, string key, ControllerProfile profile)
    {
        ControllerProfileStore store = Read(path);
        store.Devices[key] = profile.Validated();
        Write(path, store);
    }

    /// <summary>Imports the legacy file only when the VulkanStory-owned destination does not yet exist.</summary>
    public static void ImportLegacy(string path, string legacyPath)
    {
        if (!File.Exists(path) && File.Exists(legacyPath)) Write(path, Read(legacyPath));
    }

    /// <summary>Writes serialized settings to a sibling temporary file before replacing the destination.</summary>
    private static void Write(string path, ControllerProfileStore store)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(store, JsonOptions) + Environment.NewLine);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Reads schemas one through three and upgrades legacy defaults only while the stored version requires migration.</summary>
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
        if (store.Version < 3)
        {
            UpgradeLegacyBindings(store.Default);
            foreach (ControllerProfile profile in store.Devices.Values)
                if (profile != null) UpgradeLegacyBindings(profile);
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
