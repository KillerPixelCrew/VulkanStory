using System;
using System.IO;
using System.Text.Json;
using Optimum.Render.Vulkan.Platform;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class ControllerProfileStoreTests
{
    [Fact]
    public void DeviceSettingsPersistAndAreValidatedWhenLoaded()
    {
        string path = Path.Combine(Path.GetTempPath(), $"optimum-controller-{Guid.NewGuid():N}.json");
        try
        {
            ControllerProfile first = ControllerProfileStore.LoadDevice(path, "1234:5678", "Handheld");
            Assert.Equal(820f, first.LookSensitivity);
            Assert.True(File.Exists(path));

            ControllerProfileStore saved = JsonSerializer.Deserialize<ControllerProfileStore>(File.ReadAllText(path))!;
            Assert.Equal("Handheld", saved.Devices["1234:5678"].Name);
            saved.Devices["1234:5678"].LookSensitivity = 460f;
            saved.Devices["1234:5678"].MoveDeadzone = -1f;
            saved.Devices["1234:5678"].AcceptButton = 99;
            saved.Devices["1234:5678"].GyroEnabled = true;
            saved.Devices["1234:5678"].GyroSensitivity = 5000f;
            saved.Devices["1234:5678"].GyroDeadzone = -1f;
            saved.Devices["1234:5678"].RumbleEnabled = true;
            saved.Devices["1234:5678"].RumbleStrength = 2f;
            saved.Devices["1234:5678"].RumbleDurationMs = 1000;
            saved.Devices["1234:5678"].MoveXAxis = 99;
            File.WriteAllText(path, JsonSerializer.Serialize(saved));

            ControllerProfile reloaded = ControllerProfileStore.LoadDevice(path, "1234:5678", "Handheld");
            Assert.Equal(460f, reloaded.LookSensitivity);
            Assert.Equal(0f, reloaded.MoveDeadzone);
            Assert.Equal(0, reloaded.AcceptButton);
            Assert.True(reloaded.GyroEnabled);
            Assert.Equal(3000f, reloaded.GyroSensitivity);
            Assert.Equal(0f, reloaded.GyroDeadzone);
            Assert.True(reloaded.RumbleEnabled);
            Assert.Equal(1f, reloaded.RumbleStrength);
            Assert.Equal(250, reloaded.RumbleDurationMs);
            Assert.Equal(0, reloaded.MoveXAxis);
            Assert.Equal(820f, ControllerProfileStore.LoadDevice(path, "abcd:ef01", "Other").LookSensitivity);

            reloaded.CursorSensitivity = 650f;
            reloaded.MoveXAxis = 3;
            reloaded.InvertMoveX = true;
            reloaded.PrimaryTriggerAxis = 1;
            reloaded.PrimaryTriggerNegative = true;
            ControllerProfileStore.SaveDevice(path, "1234:5678", reloaded);
            ControllerProfile remapped = ControllerProfileStore.LoadDevice(path, "1234:5678", "Handheld");
            Assert.Equal(650f, remapped.CursorSensitivity);
            Assert.Equal(3, remapped.MoveXAxis);
            Assert.True(remapped.InvertMoveX);
            Assert.Equal(1, remapped.PrimaryTriggerAxis);
            Assert.True(remapped.PrimaryTriggerNegative);
            Assert.True(JsonSerializer.Deserialize<ControllerProfileStore>(File.ReadAllText(path))!
                .Devices.ContainsKey("abcd:ef01"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DeviceKeySeparatesSerialsWithoutStoringTheirRawValue()
    {
        string first = ControllerProfileStore.DeviceKey(0x1234, 0x5678, "Handheld", "serial-A");
        string second = ControllerProfileStore.DeviceKey(0x1234, 0x5678, "Handheld", "serial-B");
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("serial-A", first);
        Assert.Equal("1234:5678", ControllerProfileStore.DeviceKey(0x1234, 0x5678, "Handheld", null));
    }

    [Fact]
    public void InvalidProfileIsPreservedForManualRepair()
    {
        string path = Path.Combine(Path.GetTempPath(), $"optimum-controller-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{invalid-json");
            Assert.Throws<JsonException>(() => ControllerProfileStore.LoadDevice(path, "1234:5678", "Handheld"));
            Assert.Equal("{invalid-json", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InGameEditsApplyToTheCurrentInputProfileImmediately()
    {
        using var input = new SdlGamepadInput(new VulkanClientPlatform(null!));
        input.UpdateProfile(profile => profile.LookSensitivity = 4500f);
        Assert.Equal(3000f, input.CurrentProfile.LookSensitivity);
        input.UpdateProfile(profile => profile.GyroEnabled = true);
        Assert.True(input.CurrentProfile.GyroEnabled);
    }

    [Fact]
    public void NegativeHalfOfAStickCanDriveARemappedTrigger()
    {
        Assert.True(SdlGamepadInput.AxisThresholdPressed(-25000, negative: true, threshold: 0.5f));
        Assert.False(SdlGamepadInput.AxisThresholdPressed(-25000, negative: false, threshold: 0.5f));
        Assert.False(SdlGamepadInput.AxisThresholdPressed(-5000, negative: true, threshold: 0.5f));
    }
}
