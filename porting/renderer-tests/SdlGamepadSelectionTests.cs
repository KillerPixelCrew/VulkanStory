using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class SdlGamepadSelectionTests
{
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VirtualJoystickDesc
    {
        public uint Version;
        public ushort Type, Padding, VendorId, ProductId;
        public ushort NAxes, NButtons, NBalls, NHats, NTouchpads, NSensors;
        public fixed ushort Padding2[2];
        public uint ButtonMask, AxisMask;
        public nint Name, Touchpads, Sensors, Userdata, Update, SetPlayerIndex;
        public nint Rumble, RumbleTriggers, SetLed, SendEffect, SetSensorsEnabled, Cleanup;
    }

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_InitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe uint SDL_AttachVirtualJoystick(VirtualJoystickDesc* desc);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_DetachVirtualJoystick(uint id);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_PumpEvents();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_UpdateGamepads();

    [Theory]
    [InlineData(new[] { 11, 22 }, 11, 22, 22)]
    [InlineData(new[] { 11, 22 }, 11, 0, 11)]
    [InlineData(new[] { 11, 22 }, 11, 99, 11)]
    [InlineData(new[] { 22 }, 11, 0, 22)]
    [InlineData(new int[0], 11, 22, 0)]
    public void DeliberateActivitySwitchesPadsButIdleAndStaleRequestsDoNot(
        int[] connected, int current, int requested, int expected) =>
        Assert.Equal(expected, SdlGamepadInput.SelectGamepad(connected, current, requested));

    [SkippableFact]
    public unsafe void TwoNativeVirtualPadsSwitchTheActiveDeviceAndProfile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The native virtual-pad probe runs on Windows.");
        string previousDataPath = GamePaths.DataPath;
        string dataPath = Directory.CreateTempSubdirectory("optimum-sdl-pad-").FullName;
        uint first = 0, second = 0;
        nint firstName = 0, secondName = 0;
        bool initialized = false;
        SdlGamepadInput? input = null;
        try
        {
            GamePaths.DataPath = dataPath;
            initialized = SDL_InitSubSystem(0x00002000);
            Assert.True(initialized);
            firstName = Marshal.StringToCoTaskMemUTF8("Optimum virtual pad one");
            secondName = Marshal.StringToCoTaskMemUTF8("Optimum virtual pad two");
            VirtualJoystickDesc desc = new()
            {
                Version = (uint)sizeof(VirtualJoystickDesc), Type = 1,
                VendorId = 0x1234, ProductId = 0x1001,
                NAxes = 6, NButtons = 15,
                ButtonMask = (1u << 15) - 1, AxisMask = (1u << 6) - 1,
                Name = firstName,
            };
            first = SDL_AttachVirtualJoystick(&desc);
            Assert.NotEqual(0u, first);
            desc.ProductId = 0x1002;
            desc.Name = secondName;
            second = SDL_AttachVirtualJoystick(&desc);
            Assert.NotEqual(0u, second);
            SDL_PumpEvents();
            SDL_UpdateGamepads();

            input = new SdlGamepadInput(new VulkanClientPlatform(null!));
            MethodInfo refresh = typeof(SdlGamepadInput).GetMethod("RefreshGamepad",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo requested = typeof(SdlGamepadInput).GetField("requestedGamepadId",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo active = typeof(SdlGamepadInput).GetField("gamepadId",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            refresh.Invoke(input, new object[] { true });
            Assert.Equal((int)first, active.GetValue(input));
            requested.SetValue(input, (int)second);
            refresh.Invoke(input, new object[] { false });
            Assert.Equal((int)second, active.GetValue(input));
            string settingsPath = Path.Combine(GamePaths.ModConfig, "optimum-controllers.json");
            Assert.True(File.Exists(settingsPath));

            // A damaged settings file must not write pad two's unsaved edits
            // under pad one's key during a hand-off.
            input.UpdateProfile(profile => profile.LookSensitivity = 1333f);
            File.WriteAllText(settingsPath, "{\"Version\":99}");
            requested.SetValue(input, (int)first);
            refresh.Invoke(input, new object[] { false });
            Assert.Equal((int)first, active.GetValue(input));
            File.WriteAllText(settingsPath, "{\"Version\":1,\"Default\":{},\"Devices\":{}}");
            requested.SetValue(input, (int)second);
            refresh.Invoke(input, new object[] { false });
            Assert.Equal((int)second, active.GetValue(input));
            Assert.Equal(1333f, input.CurrentProfile.LookSensitivity);
            input.FlushProfile(true);
            Assert.Equal(1333f, ControllerProfileStore.LoadDevice(settingsPath,
                ControllerProfileStore.DeviceKey(0x1234, 0x1002,
                    "Optimum virtual pad two", null), "Optimum virtual pad two").LookSensitivity);
        }
        finally
        {
            input?.Dispose();
            if (second != 0) SDL_DetachVirtualJoystick(second);
            if (first != 0) SDL_DetachVirtualJoystick(first);
            if (initialized) SDL_QuitSubSystem(0x00002000);
            if (firstName != 0) Marshal.FreeCoTaskMem(firstName);
            if (secondName != 0) Marshal.FreeCoTaskMem(secondName);
            GamePaths.DataPath = previousDataPath;
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
