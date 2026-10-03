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
    public float MovePressThreshold { get; set; } = 0.28f;
    public float MoveReleaseThreshold { get; set; } = 0.18f;
    public float LookDeadzone { get; set; } = 0.16f;
    public float LookSensitivity { get; set; } = 820f;
    public float CursorSensitivity { get; set; } = 900f;
    public float LookCurveExponent { get; set; } = 2f;
    public float TriggerThreshold { get; set; } = 0.5f;
    public int MoveXAxis { get; set; } = 0;
    public int MoveYAxis { get; set; } = 1;
    public int LookXAxis { get; set; } = 2;
    public int LookYAxis { get; set; } = 3;
    public bool InvertMoveX { get; set; }
    public bool InvertMoveY { get; set; }
    public bool InvertLookX { get; set; }
    public bool ToggleSneak { get; set; }
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
    public int DropButton { get; set; } = 2;
    public int InventoryButton { get; set; } = 3;
    public int MenuButton { get; set; } = 6;
    public int SettingsButton { get; set; } = 4;
    public int SneakButton { get; set; } = 7;
    public int SprintButton { get; set; } = 8;
    public int PreviousHotbarButton { get; set; } = 9;
    public int NextHotbarButton { get; set; } = 10;
    public int PrimaryTriggerAxis { get; set; } = 4;
    public int SecondaryTriggerAxis { get; set; } = 5;
    public bool PrimaryTriggerNegative { get; set; }
    public bool SecondaryTriggerNegative { get; set; }

    public ControllerProfile Validated() => new()
    {
        Name = Name ?? "Controller",
        MoveDeadzone = Math.Clamp(MoveDeadzone, 0f, 0.8f),
        MovePressThreshold = Math.Clamp(MovePressThreshold, 0.05f, 0.95f),
        MoveReleaseThreshold = Math.Clamp(Math.Min(MoveReleaseThreshold, MovePressThreshold), 0f, 0.9f),
        LookDeadzone = Math.Clamp(LookDeadzone, 0f, 0.8f),
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
        DropButton = ButtonOrDefault(DropButton, 2),
        InventoryButton = ButtonOrDefault(InventoryButton, 3),
        MenuButton = ButtonOrDefault(MenuButton, 6),
        SettingsButton = ButtonOrDefault(SettingsButton, 4),
        SneakButton = ButtonOrDefault(SneakButton, 7),
        SprintButton = ButtonOrDefault(SprintButton, 8),
        PreviousHotbarButton = ButtonOrDefault(PreviousHotbarButton, 9),
        NextHotbarButton = ButtonOrDefault(NextHotbarButton, 10),
        PrimaryTriggerAxis = AxisOrDefault(PrimaryTriggerAxis, 4),
        SecondaryTriggerAxis = AxisOrDefault(SecondaryTriggerAxis, 5),
        PrimaryTriggerNegative = PrimaryTriggerNegative,
        SecondaryTriggerNegative = SecondaryTriggerNegative
    };

    private static int ButtonOrDefault(int value, int fallback) => value is >= 0 and <= 31 ? value : fallback;
    private static int AxisOrDefault(int value, int fallback) => value is >= 0 and <= 5 ? value : fallback;
}

internal sealed class ControllerProfileStore
{
    public int Version { get; set; } = 1;
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
        if (store.Version != 1 || store.Default == null || store.Devices == null)
            throw new InvalidDataException("Unsupported controller profile format");
        return store;
    }
}
