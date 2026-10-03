using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Optimum;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// Polls SDL's gamepad subsystem beside the SDL-owned window and frame loop.
/// </summary>
internal sealed unsafe class SdlGamepadInput : IDisposable
{
    private const uint GamepadSubsystem = 0x00002000;
    private const int GyroSensor = 2; // SDL_SENSOR_GYRO
    private readonly VulkanClientPlatform platform;
    private readonly Dictionary<string, HeldKey> heldKeys = new();
    private readonly HashSet<EnumMouseButton> heldMouse = new();
    private readonly ControllerToggleState sneakToggle = new();
    private ControllerProfile profile = new();
    private string? profilePath;
    private string? profileKey;
    private bool profileDirty;
    private long profileWriteDue;
    private readonly List<PendingProfileWrite> pendingProfileWrites = new();
    private long pendingProfileRetryDue;
    private ControllerSettingsDialog? settingsDialog;
    private ClientMain? settingsGame;
    private ControllerKeyboardDialog? keyboardDialog;
    private ControllerKeyboardScreen? keyboardScreen;
    private ClientMain? keyboardGame;
    private bool keyboardChordWasDown;
    private bool keyboardBackConsumed;
    private bool settingsButtonWasDown;
    private readonly ControllerButtonCapture buttonCapture = new();
    private bool gyroAvailable;
    private bool gyroEnabled;
    private bool rumbleAvailable = true;
    private bool triggerRumbleAvailable = true;
    private bool rumblePlaying;
    private bool triggerRumblePlaying;
    private SyncedTreeAttribute? damageAttributes;
    private float pendingDamageRumble;
    internal float PendingDamageRumbleForTests => Volatile.Read(ref pendingDamageRumble);
    private nint gamepad;
    private int gamepadId;
    private int requestedGamepadId;
    private readonly Action<int> onGamepadActivity;
    private long lastPoll;
    private long nextGamepadScan;
    private bool initialized;
    private bool unavailable;
    private bool leftShoulderHeld;
    private bool rightShoulderHeld;
    private float lookRemainderX;
    private float lookRemainderY;
    private bool rightStickWasActive;
    private readonly bool[] dpadHeld = new bool[4];
    private readonly long[] dpadNextRepeat = new long[4];

    private readonly record struct HeldKey(int Code, bool Alt, bool Ctrl, bool Shift);
    private readonly record struct PendingProfileWrite(string Path, string Key, ControllerProfile Profile);

    public SdlGamepadInput(VulkanClientPlatform platform)
    {
        this.platform = platform;
        onGamepadActivity = id =>
        {
            if (platform.HasControllerWindow && platform.IsFocused &&
                id > 0 && id != gamepadId) requestedGamepadId = id;
        };
    }

    internal ControllerProfile CurrentProfile => profile.Validated();
    internal static string AxisName(int axis) => axis switch
    {
        0 => "Left X", 1 => "Left Y", 2 => "Right X", 3 => "Right Y",
        4 => "Left trigger", 5 => "Right trigger", _ => "Unknown"
    };
    internal string ButtonName(int button) => ControllerButtonBindings.Name(
        button, gamepad != 0 ? SDL_GetGamepadButtonLabel(gamepad, button) : 0);
    internal string? PendingBinding => buttonCapture.Action;
    internal bool BindingWaitingForRelease => buttonCapture.WaitingForRelease;

    internal void BeginBinding(string action)
    {
        ControllerButtonBindings.Find(action);
        buttonCapture.Begin(action, PressedButtonMask());
        settingsDialog?.RequestRefresh();
    }

    internal void CancelBinding(bool refresh = true)
    {
        buttonCapture.Cancel();
        if (refresh) settingsDialog?.RequestRefresh();
    }

    internal void UpdateProfile(Action<ControllerProfile> change)
    {
        ControllerProfile edited = profile.Validated();
        change(edited);
        profile = edited.Validated();
        PublishControllerHints();
        if (!profile.GyroEnabled) UpdateGyroState(false);
        gyroAvailable = gamepad != 0 && profile.GyroEnabled && SDL_GamepadHasSensor(gamepad, GyroSensor);
        profileDirty = true;
        profileWriteDue = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
    }

    internal void FlushProfile(bool force)
    {
        if (!profileDirty || profilePath == null || profileKey == null ||
            (!force && Stopwatch.GetTimestamp() < profileWriteDue)) return;
        try
        {
            ControllerProfileStore.SaveDevice(profilePath, profileKey, profile);
            profileDirty = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            platform.Logger.Warning("[VulkanStory] Controller settings could not be saved: {0}", error.Message);
            profileWriteDue = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        }
    }

    private void RememberUnsavedProfile()
    {
        if (!profileDirty || profilePath == null || profileKey == null) return;
        var pending = new PendingProfileWrite(profilePath, profileKey, profile.Validated());
        int index = pendingProfileWrites.FindIndex(item =>
            item.Path == pending.Path && item.Key == pending.Key);
        if (index < 0) pendingProfileWrites.Add(pending);
        else pendingProfileWrites[index] = pending;
        profileDirty = false;
        pendingProfileRetryDue = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
    }

    private ControllerProfile? TakeUnsavedProfile(string path, string key)
    {
        int index = pendingProfileWrites.FindIndex(item => item.Path == path && item.Key == key);
        if (index < 0) return null;
        ControllerProfile value = pendingProfileWrites[index].Profile;
        pendingProfileWrites.RemoveAt(index);
        return value;
    }

    private void FlushPendingProfiles(bool force)
    {
        if (pendingProfileWrites.Count == 0 ||
            (!force && Stopwatch.GetTimestamp() < pendingProfileRetryDue)) return;
        for (int i = pendingProfileWrites.Count - 1; i >= 0; i--)
        {
            PendingProfileWrite pending = pendingProfileWrites[i];
            try
            {
                ControllerProfileStore.SaveDevice(pending.Path, pending.Key, pending.Profile);
                pendingProfileWrites.RemoveAt(i);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                platform.Logger.Warning("[VulkanStory] Controller settings still could not be saved: {0}",
                    error.Message);
            }
        }
        pendingProfileRetryDue = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
    }

    public void Poll(Action<SdlInputEvent>? onWindowEvent = null, Action? onInputPumped = null)
    {
        try
        {
            Prepare();
            if (unavailable && onWindowEvent == null) return;

            // One queue reader owns gamepad and window events. Keep the SDL
            // window responsive when the optional gamepad subsystem is absent.
            // The gamepad refresh precedes the input marker and event dispatch.
            bool deviceChanged = SdlEventPump.Drain(onWindowEvent, onInputPumped,
                onGamepadActivity, initialized ? SDL_UpdateGamepads : null);
            if (unavailable) return;
            long now = Stopwatch.GetTimestamp();
            FlushProfile(false);
            FlushPendingProfiles(false);
            // Event-driven hot-plug with a slow rescan if a platform drops an event.
            if (deviceChanged || requestedGamepadId != 0 || now >= nextGamepadScan)
            {
                RefreshGamepad(deviceChanged);
                nextGamepadScan = now + Stopwatch.Frequency * 2;
            }
            SyncDamageHaptics();
            ApplyDamageHaptics();
            float dt = lastPoll == 0 ? 0f : Math.Clamp((now - lastPoll) / (float)Stopwatch.Frequency, 0f, 0.05f);
            lastPoll = now;
            if (gamepad == 0 || !platform.IsFocused || ScreenManager.hotkeyManager == null || platform.keyEventHandlers.Count == 0)
            {
                ReleaseAll();
                return;
            }

            settingsDialog?.ApplyPendingRefresh();
            keyboardDialog?.ApplyPendingRefresh();
            keyboardScreen?.ApplyPendingRefresh();
            if (keyboardDialog != null && !keyboardDialog.IsOpened()) CloseKeyboard();
            if (keyboardScreen != null && !keyboardScreen.IsOpened) keyboardScreen = null;
            if (keyboardDialog?.IsOpened() == true && keyboardGame != ControllerGuiTargets.ActiveGame(platform))
                CloseKeyboard();
            if (buttonCapture.Action != null)
            {
                uint buttons = PressedButtonMask();
                if (buttons != 0) NoteControllerActivity();
                // Capture suppresses normal controller actions, so provide a
                // hardware-only escape even when no keyboard or mouse is attached.
                if (ControllerButtonCapture.CancelChordPressed(buttons))
                {
                    CancelBinding();
                    ReleaseAll();
                    settingsButtonWasDown = Button(profile.SettingsButton);
                    return;
                }
                (string Action, int Button)? captured = buttonCapture.Update(buttons);
                ReleaseAll();
                if (captured.HasValue)
                {
                    UpdateProfile(p => ControllerButtonBindings.AssignUnique(
                        p, captured.Value.Action, captured.Value.Button));
                    settingsDialog?.RequestRefresh();
                }
                settingsButtonWasDown = Button(profile.SettingsButton);
                return;
            }

            bool keyboardChord = !platform.MouseGrabbed && Button(7) && Button(8);
            if (keyboardChord && !keyboardChordWasDown)
            {
                NoteControllerActivity();
                ToggleKeyboard();
            }
            keyboardChordWasDown = keyboardChord;
            if (keyboardChord) { ReleaseAll(); return; }
            bool backDown = Button(profile.BackButton);
            if (keyboardBackConsumed)
            {
                if (!backDown) keyboardBackConsumed = false;
                else { ReleaseAll(); return; }
            }
            if ((keyboardDialog?.IsOpened() == true || keyboardScreen?.IsOpened == true) && backDown)
            {
                CloseKeyboard();
                keyboardBackConsumed = true;
                ReleaseAll();
                return;
            }

            bool settingsDown = Button(profile.SettingsButton);
            if (settingsDown && !settingsButtonWasDown)
            {
                NoteControllerActivity();
                ToggleSettings();
            }
            settingsButtonWasDown = settingsDown;

            float lx = Stick(profile.MoveXAxis, profile.MoveDeadzone) * (profile.InvertMoveX ? -1f : 1f);
            float ly = Stick(profile.MoveYAxis, profile.MoveDeadzone) * (profile.InvertMoveY ? -1f : 1f);
            platform.ControllerMoveAxes = platform.MouseGrabbed ? new Vector2(lx, ly) : Vector2.Zero;
            platform.ControllerMoveFactor = platform.MouseGrabbed
                ? Math.Clamp(MathF.Sqrt(lx * lx + ly * ly), 0f, 1f) : 1f;
            float rx = Stick(profile.LookXAxis, profile.LookDeadzone);
            float ry = Stick(profile.LookYAxis, profile.LookDeadzone);
            bool rightStickActive = MathF.Abs(rx) > 0.15f || MathF.Abs(ry) > 0.15f;
            if (rightStickActive && !rightStickWasActive) NoteControllerActivity();
            rightStickWasActive = rightStickActive;
            bool analogWalking = platform.MouseGrabbed &&
                ControllerGuiTargets.ActiveGame(platform)?.OptimumAnalogServerReady == true;
            SetKey("walkleft", Moving("walkleft", -lx, analogWalking), GlKeys.A);
            SetKey("walkright", Moving("walkright", lx, analogWalking), GlKeys.D);
            SetKey("walkforward", Moving("walkforward", -ly, analogWalking), GlKeys.W);
            SetKey("walkbackward", Moving("walkbackward", ly, analogWalking), GlKeys.S);
            bool south = Button(profile.AcceptButton);
            bool menuNavigation = !platform.MouseGrabbed &&
                (south || Button(11) || Button(12) || Button(13) || Button(14));
            List<GuiComposer>? composers = menuNavigation
                ? keyboardScreen?.IsOpened == true
                    ? new List<GuiComposer> { keyboardScreen.ElementComposer }
                    : keyboardDialog?.IsOpened() == true
                    ? keyboardDialog.Composers.Values.Where(composer => composer.Enabled).ToList()
                    : ControllerGuiTargets.ActiveComposers(platform)
                : null;
            GuiElementItemSlotGridBase? focusedGrid = menuNavigation ? FocusedSlotGrid(composers!) : null;
            GuiElementSlider? focusedSlider = menuNavigation && focusedGrid == null ? FocusedSlider(composers!) : null;
            SetKey("jump", platform.MouseGrabbed && south, GlKeys.Space); // South / A
            bool sneakPressed = Button(profile.SneakButton);
            SetKey("sneak", sneakToggle.Update(sneakPressed, profile.ToggleSneak, platform.MouseGrabbed), GlKeys.LShift);
            SetKey("sprint", platform.MouseGrabbed && Button(profile.SprintButton), GlKeys.LControl);
            SetKey("controller-shift", !platform.MouseGrabbed && sneakPressed, GlKeys.ShiftLeft);
            SetKey("controller-ctrl", !platform.MouseGrabbed && Button(profile.SprintButton), GlKeys.ControlLeft);
            SetKey("inventorydialog", Button(profile.InventoryButton), GlKeys.E);
            SetKey("escapemenudialog", Button(profile.MenuButton) || Button(profile.BackButton), GlKeys.Escape);
            SetKey("dropitem", Button(profile.DropButton), GlKeys.Q);

            SetMouse(EnumMouseButton.Left, Trigger(profile.PrimaryTriggerAxis, profile.PrimaryTriggerNegative) ||
                (!platform.MouseGrabbed && south));
            SetMouse(EnumMouseButton.Right, Trigger(profile.SecondaryTriggerAxis, profile.SecondaryTriggerNegative));
            bool leftShoulder = Button(profile.PreviousHotbarButton), rightShoulder = Button(profile.NextHotbarButton);
            if (platform.MouseGrabbed)
            {
                if (leftShoulder && !leftShoulderHeld) Scroll(-1);
                if (rightShoulder && !rightShoulderHeld) Scroll(1);
            }
            leftShoulderHeld = leftShoulder;
            rightShoulderHeld = rightShoulder;
            if (platform.MouseGrabbed)
            {
                ResetDpad();
                lookRemainderX += Curve(rx) * profile.LookSensitivity * dt * (profile.InvertLookX ? -1f : 1f);
                lookRemainderY += Curve(ry) * profile.LookSensitivity * dt * (profile.InvertLookY ? -1f : 1f);
                UpdateGyroState(profile.GyroEnabled && gyroAvailable &&
                    (!profile.GyroRequireSecondaryTrigger ||
                     Trigger(profile.SecondaryTriggerAxis, profile.SecondaryTriggerNegative)));
                if (gyroEnabled && dt > 0f)
                {
                    float* rates = stackalloc float[3];
                    if (SDL_GetGamepadSensorData(gamepad, GyroSensor, rates, 3))
                    {
                        // SDL reports angular velocity in radians/sec: X is pitch,
                        // Y is yaw. Integrate on the same frame as stick look.
                        float yaw = GyroRate(rates[1]);
                        float pitch = GyroRate(rates[0]);
                        lookRemainderX += yaw * profile.GyroSensitivity * dt * (profile.InvertGyroX ? -1f : 1f);
                        lookRemainderY += pitch * profile.GyroSensitivity * dt * (profile.InvertGyroY ? -1f : 1f);
                    }
                }
                int dx = (int)MathF.Truncate(lookRemainderX);
                int dy = (int)MathF.Truncate(lookRemainderY);
                lookRemainderX -= dx;
                lookRemainderY -= dy;
                if (dx != 0 || dy != 0)
                    foreach (MouseEventHandler handler in platform.mouseEventHandlers)
                        handler.OnMouseMove(new MouseEvent(0, 0, dx, dy));
            }
            else if (platform.HasControllerWindow)
            {
                UpdateGyroState(false);
                // A focused inventory grid already understands arrows and Enter.
                // Otherwise snap to the next active widget/slot, with a fixed
                // step as a fallback for UIs without discoverable elements.
                float step = Math.Max(1f, RuntimeEnv.GUIScale) *
                    (float)(GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding);
                Vector2 pos = platform.ControllerCursorPosition;
                bool up = DpadPulse(11, now), down = DpadPulse(12, now);
                bool left = DpadPulse(13, now), right = DpadPulse(14, now);
                List<Vector2>? targets = (up || down || left || right) && focusedGrid == null
                    ? ControllerGuiTargets.Collect(platform, composers ?? ControllerGuiTargets.ActiveComposers(platform)) : null;
                if (up) DpadMove(GlKeys.Up, new Vector2(0, -1), step, focusedGrid, focusedSlider, targets, ref pos);
                if (down) DpadMove(GlKeys.Down, new Vector2(0, 1), step, focusedGrid, focusedSlider, targets, ref pos);
                if (left) DpadMove(GlKeys.Left, new Vector2(-1, 0), step, focusedGrid, focusedSlider, targets, ref pos);
                if (right) DpadMove(GlKeys.Right, new Vector2(1, 0), step, focusedGrid, focusedSlider, targets, ref pos);
                (int width, int height) = platform.ControllerWindowSize;
                pos.X = Math.Clamp(pos.X + Curve(rx) * profile.CursorSensitivity * dt, 0, Math.Max(0, width - 1));
                pos.Y = Math.Clamp(pos.Y + Curve(ry) * profile.CursorSensitivity * dt, 0, Math.Max(0, height - 1));
                ElementBounds? keyboardBounds = keyboardScreen?.IsOpened == true
                    ? keyboardScreen.ElementComposer.Bounds
                    : keyboardDialog?.IsOpened() == true ? keyboardDialog.SingleComposer.Bounds : null;
                if (keyboardBounds != null)
                {
                    pos.X = Math.Clamp(pos.X, (float)keyboardBounds.absX + 8,
                        (float)(keyboardBounds.absX + keyboardBounds.OuterWidth - 8));
                    pos.Y = Math.Clamp(pos.Y, (float)keyboardBounds.absY + 8,
                        (float)(keyboardBounds.absY + keyboardBounds.OuterHeight - 8));
                }
                platform.ControllerCursorPosition = pos;
            }
        }
        catch (DllNotFoundException error) { Disable(error.Message); }
        catch (EntryPointNotFoundException error) { Disable(error.Message); }
    }

    // The SDL client calls this before AMD's INPUT stage so first-use subsystem
    // setup and mapping-file I/O are outside the keyed input-processing window.
    internal void Prepare()
    {
        if (unavailable || initialized) return;
        try
        {
            if (!SDL_InitSubSystem(GamepadSubsystem))
            {
                Disable("SDL gamepad initialization failed: " + Error());
                return;
            }
            initialized = true;
            LoadGamepadMappings();
        }
        catch (DllNotFoundException error) { Disable(error.Message); }
        catch (EntryPointNotFoundException error) { Disable(error.Message); }
    }

    internal static int SelectGamepad(ReadOnlySpan<int> ids, int current, int requested)
    {
        foreach (int id in ids) if (id == requested && requested != 0) return id;
        foreach (int id in ids) if (id == current && current != 0) return id;
        return ids.IsEmpty ? 0 : ids[0];
    }

    private void RefreshGamepad(bool deviceChanged)
    {
        int count;
        nint ids = SDL_GetGamepads(out count);
        int chosen = 0;
        int requested = requestedGamepadId;
        requestedGamepadId = 0;
        try
        {
            if (ids != 0 && count > 0)
            {
                int* entries = (int*)ids;
                chosen = SelectGamepad(new ReadOnlySpan<int>(entries, count), gamepadId, requested);
            }
        }
        finally { if (ids != 0) SDL_free(ids); }
        if (chosen == gamepadId && (chosen == 0 || gamepad != 0))
        {
            if (deviceChanged && gamepad != 0)
            {
                PublishControllerHints();
                settingsDialog?.RequestRefresh();
            }
            return;
        }
        FlushProfile(true);
        if (chosen != 0) RememberUnsavedProfile();
        ReleaseAll();
        settingsDialog?.TryClose();
        CloseKeyboard();
        buttonCapture.Cancel();
        settingsDialog = null;
        settingsGame = null;
        if (gamepad != 0) SDL_CloseGamepad(gamepad);
        gamepad = 0;
        gamepadId = chosen;
        if (chosen == 0) { PublishControllerHints(); return; }
        gamepad = SDL_OpenGamepad(chosen);
        if (gamepad != 0)
        {
            string name = Marshal.PtrToStringUTF8(SDL_GetGamepadName(gamepad)) ?? chosen.ToString();
            string? serial = Marshal.PtrToStringUTF8(SDL_GetGamepadSerial(gamepad));
            string key = ControllerProfileStore.DeviceKey(SDL_GetGamepadVendor(gamepad), SDL_GetGamepadProduct(gamepad), name, serial);
            profilePath = Path.Combine(GamePaths.ModConfig, "optimum-controllers.json");
            profileKey = key;
            if (TakeUnsavedProfile(profilePath, key) is { } unsaved)
            {
                profile = unsaved.Validated();
                profileDirty = true;
                profileWriteDue = Stopwatch.GetTimestamp();
            }
            else try
            {
                profile = ControllerProfileStore.LoadDevice(profilePath, key, name);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                profile = new ControllerProfile();
                platform.Logger.Warning("[VulkanStory] Controller profile could not be loaded: {0}", error.Message);
            }
            gyroAvailable = profile.GyroEnabled && SDL_GamepadHasSensor(gamepad, GyroSensor);
            gyroEnabled = false;
            rumbleAvailable = triggerRumbleAvailable = true;
            if (profile.GyroEnabled && !gyroAvailable)
                platform.Logger.Warning("[VulkanStory] {0} has no SDL3 gyroscope; stick look remains active", name);
            platform.Logger.Notification("[VulkanStory] SDL3 gamepad connected: {0} (profile {1})", name, key);
        }
        PublishControllerHints();
    }

    private void PublishControllerHints()
    {
        OptimumControllerHints.Publish(gamepad == 0 ? null : ControllerGlyphs.Build(profile, ButtonName));
        ScreenManager.GuiComposers?.MarkAllDialogsForRecompose();
    }

    private void NoteControllerActivity()
    {
        if (gamepad != 0 && OptimumControllerHints.SetControllerActive(true))
            ScreenManager.GuiComposers?.MarkAllDialogsForRecompose();
    }

    private float Stick(int axis, float deadzone)
    {
        float value = Math.Clamp(SDL_GetGamepadAxis(gamepad, axis) / 32767f, -1f, 1f);
        return MathF.Abs(value) < deadzone ? 0f : MathF.CopySign((MathF.Abs(value) - deadzone) / (1f - deadzone), value);
    }

    private bool Moving(string action, float magnitude, bool analogWalking) => magnitude >
        (heldKeys.ContainsKey(action)
            ? (analogWalking ? 0.01f : profile.MoveReleaseThreshold)
            : (analogWalking ? 0.02f : profile.MovePressThreshold));

    private float Curve(float value) => MathF.CopySign(MathF.Pow(MathF.Abs(value), profile.LookCurveExponent), value);
    private bool Button(int button) => SDL_GetGamepadButton(gamepad, button);
    private uint PressedButtonMask()
    {
        if (gamepad == 0) return 0;
        uint mask = 0;
        for (int button = 0; button < 32; button++)
            if (Button(button)) mask |= 1u << button;
        return mask;
    }
    private bool Trigger(int axis, bool negative) => AxisThresholdPressed(
        SDL_GetGamepadAxis(gamepad, axis), negative, profile.TriggerThreshold);

    internal static bool AxisThresholdPressed(short value, bool negative, float threshold) =>
        (negative ? -(int)value : value) > threshold * 32767f;

    private float GyroRate(float rate) => float.IsFinite(rate) && MathF.Abs(rate) >= profile.GyroDeadzone
        ? Math.Clamp(rate, -20f, 20f) : 0f;

    private void UpdateGyroState(bool wanted)
    {
        if (gamepad == 0 || gyroEnabled == wanted) return;
        if (SDL_SetGamepadSensorEnabled(gamepad, GyroSensor, wanted))
        {
            gyroEnabled = wanted;
        }
        else if (wanted)
        {
            gyroAvailable = false;
            platform.Logger.Warning("[VulkanStory] SDL3 gyro could not be enabled: {0}", Error());
        }
    }

    private void SetKey(string action, bool pressed, GlKeys fallback)
    {
        HeldKey mapping = new((int)fallback, false, false, false);
        var hotkeys = ScreenManager.hotkeyManager?.HotKeys;
        if (hotkeys != null && hotkeys.ContainsKey(action))
        {
            var key = hotkeys[action].CurrentMapping;
            if (key != null) mapping = new(key.KeyCode, key.Alt, key.Ctrl, key.Shift);
        }
        if (heldKeys.TryGetValue(action, out HeldKey previous))
        {
            if (pressed && previous == mapping) return;
            SendKey(previous, false);
            heldKeys.Remove(action);
        }
        if (!pressed || mapping.Code < 0 || mapping.Code >= 240) return;
        NoteControllerActivity();
        SendKey(mapping, true);
        heldKeys[action] = mapping;
    }

    private void SendKey(HeldKey key, bool down)
    {
        platform.InjectControllerKey(new KeyEvent
        {
            KeyCode = key.Code,
            AltPressed = key.Alt,
            CtrlPressed = key.Ctrl,
            ShiftPressed = key.Shift
        }, down);
    }

    private void SetMouse(EnumMouseButton button, bool pressed)
    {
        if (pressed == heldMouse.Contains(button)) return;
        if (pressed) NoteControllerActivity();
        if (pressed) heldMouse.Add(button); else heldMouse.Remove(button);
        platform.InjectControllerMouseButton(button, pressed);
        if (pressed) PulseFeedback(button);
    }

    private void PulseFeedback(EnumMouseButton? trigger)
    {
        if (gamepad == 0) return;
        uint duration = (uint)profile.RumbleDurationMs;
        if (profile.RumbleEnabled && profile.RumbleStrength > 0f && rumbleAvailable)
        {
            ushort strength = (ushort)MathF.Round(profile.RumbleStrength * ushort.MaxValue);
            rumbleAvailable = SDL_RumbleGamepad(gamepad, strength, strength, duration);
            rumblePlaying = rumbleAvailable;
        }
        if (trigger.HasValue && profile.TriggerRumbleEnabled && profile.TriggerRumbleStrength > 0f && triggerRumbleAvailable)
        {
            ushort strength = (ushort)MathF.Round(profile.TriggerRumbleStrength * ushort.MaxValue);
            ushort left = trigger == EnumMouseButton.Left ? strength : (ushort)0;
            ushort right = trigger == EnumMouseButton.Right ? strength : (ushort)0;
            if (left == 0 && right == 0) return;
            triggerRumbleAvailable = SDL_RumbleGamepadTriggers(gamepad, left, right, duration);
            triggerRumblePlaying = triggerRumbleAvailable;
        }
    }

    private void SyncDamageHaptics()
    {
        SyncedTreeAttribute? current = gamepad != 0
            ? ControllerGuiTargets.ActiveGame(platform)?.EntityPlayer?.WatchedAttributes
            : null;
        BindDamageAttributes(current);
    }

    internal void BindDamageAttributes(SyncedTreeAttribute? current)
    {
        if (ReferenceEquals(current, damageAttributes)) return;
        damageAttributes?.UnregisterListener(OnPlayerHurt);
        damageAttributes = current;
        Interlocked.Exchange(ref pendingDamageRumble, 0f);
        damageAttributes?.RegisterModifiedListener("onHurt", OnPlayerHurt);
    }

    private void OnPlayerHurt()
    {
        float damage = damageAttributes?.GetFloat("onHurt") ?? 0;
        if (float.IsFinite(damage) && damage > 0)
            Interlocked.Exchange(ref pendingDamageRumble, damage);
    }

    private void ApplyDamageHaptics()
    {
        float damage = Interlocked.Exchange(ref pendingDamageRumble, 0f);
        if (gamepad == 0 || !platform.IsFocused || !profile.RumbleEnabled ||
            profile.RumbleStrength <= 0 || !rumbleAvailable) return;
        if (damage <= 0) return;
        float strength = Math.Clamp(0.35f + damage / 20f, 0.35f, 1f) * profile.RumbleStrength;
        ushort low = (ushort)MathF.Round(strength * ushort.MaxValue);
        ushort high = (ushort)MathF.Round(strength * 0.7f * ushort.MaxValue);
        uint duration = (uint)Math.Clamp(120 + (int)(damage * 18), 120, 450);
        rumbleAvailable = SDL_RumbleGamepad(gamepad, low, high, duration);
        rumblePlaying = rumbleAvailable;
    }

    private void Scroll(int direction)
    {
        NoteControllerActivity();
        foreach (MouseEventHandler handler in platform.mouseEventHandlers)
            handler.OnMouseWheel(new MouseWheelEventArgs
            {
                delta = direction,
                deltaPrecise = direction,
                value = direction,
                valuePrecise = direction
            });
    }

    private void ToggleSettings()
    {
        CloseKeyboard();
        ClientMain? game = ControllerGuiTargets.ActiveGame(platform);
        if (game?.api == null) return;
        if (settingsGame != game)
        {
            settingsDialog = null;
            settingsGame = game;
        }
        settingsDialog ??= new ControllerSettingsDialog(game.api, this);
        if (settingsDialog.IsOpened()) settingsDialog.TryClose();
        else settingsDialog.TryOpen();
    }

    private void ToggleKeyboard()
    {
        if (keyboardDialog?.IsOpened() == true || keyboardScreen?.IsOpened == true)
        { CloseKeyboard(); return; }
        CloseKeyboard();
        ClientMain? game = ControllerGuiTargets.ActiveGame(platform);
        GuiElementEditableTextBase? target = platform.ControllerFocusedEditableText();
        if (target == null) return;
        ElementBounds bounds;
        if (game?.api != null)
        {
            keyboardGame = game;
            keyboardDialog = new ControllerKeyboardDialog(game.api, target);
            keyboardDialog.TryOpen(withFocus: false);
            bounds = keyboardDialog.SingleComposer.Bounds;
        }
        else if (platform.ControllerCurrentScreen() is GuiScreen screen &&
            ClientProgram.screenManager is { } manager)
        {
            keyboardScreen = new ControllerKeyboardScreen(manager, screen, target);
            manager.LoadScreen(keyboardScreen);
            bounds = keyboardScreen.ElementComposer.Bounds;
        }
        else return;
        platform.ControllerCursorPosition = new Vector2(
            (float)(bounds.absX + bounds.OuterWidth / 2),
            (float)(bounds.absY + bounds.OuterHeight / 2));
    }

    private void CloseKeyboard()
    {
        if (keyboardScreen != null)
        {
            keyboardScreen.Close();
            keyboardScreen = null;
        }
        if (keyboardDialog == null) return;
        keyboardDialog.TryClose();
        keyboardGame?.UnregisterDialog(keyboardDialog);
        keyboardDialog.Dispose();
        keyboardDialog = null;
        keyboardGame = null;
    }

    private GuiElementItemSlotGridBase? FocusedSlotGrid(IReadOnlyList<GuiComposer> composers)
    {
        if (!platform.HasControllerWindow) return null;
        Vector2 pos = platform.ControllerCursorPosition;
        foreach (GuiComposer composer in composers)
        {
            if (composer.Enabled && composer.CurrentTabIndexElement is GuiElementItemSlotGridBase grid &&
                grid.Bounds.PointInside(pos.X, pos.Y))
            {
                // Mouse focus does not initialize the grid's keyboard selection.
                // Seed it from the hovered slot before arrows/Enter take over.
                if (grid.tabbedSlotId < 0 && grid.SlotBounds != null)
                {
                    int count = Math.Min(grid.SlotBounds.Length, grid.renderedSlots.Count);
                    for (int i = 0; i < count; i++)
                    {
                        if (!grid.SlotBounds[i].PointInside(pos.X, pos.Y)) continue;
                        grid.tabbedSlotId = i;
                        grid.highlightSlotId = grid.renderedSlots.GetKeyAtIndex(i);
                        break;
                    }
                }
                return grid;
            }
        }
        return null;
    }

    private GuiElementSlider? FocusedSlider(IReadOnlyList<GuiComposer> composers)
    {
        if (!platform.HasControllerWindow) return null;
        Vector2 pos = platform.ControllerCursorPosition;
        foreach (GuiComposer composer in composers)
            if (composer.Enabled && composer.CurrentTabIndexElement is GuiElementSlider slider &&
                slider.Bounds.PointInside(pos.X, pos.Y)) return slider;
        return null;
    }

    private bool DpadPulse(int button, long now)
    {
        int index = button - 11;
        if (!Button(button))
        {
            dpadHeld[index] = false;
            return false;
        }
        if (!dpadHeld[index])
        {
            dpadHeld[index] = true;
            dpadNextRepeat[index] = now + Stopwatch.Frequency * 300 / 1000;
            return true;
        }
        if (now < dpadNextRepeat[index]) return false;
        dpadNextRepeat[index] = now + Stopwatch.Frequency * 90 / 1000;
        return true;
    }

    private void DpadMove(GlKeys key, Vector2 direction, float step, GuiElementItemSlotGridBase? grid,
        GuiElementSlider? slider,
        IReadOnlyList<Vector2>? targets, ref Vector2 cursor)
    {
        NoteControllerActivity();
        if (grid != null)
        {
            var held = new HeldKey((int)key, false, false, false);
            SendKey(held, true);
            SendKey(held, false);
            int index = grid.tabbedSlotId;
            if (grid.SlotBounds != null && index >= 0 && index < grid.SlotBounds.Length)
            {
                ElementBounds bounds = grid.SlotBounds[index];
                cursor = new Vector2((float)(bounds.absX + bounds.OuterWidth / 2),
                    (float)(bounds.absY + bounds.OuterHeight / 2));
            }
        }
        else if (slider != null && (key == GlKeys.Left || key == GlKeys.Right))
        {
            var held = new HeldKey((int)key, false, false, false);
            SendKey(held, true);
            SendKey(held, false);
        }
        else
        {
            if (targets != null && ControllerCursorNavigation.TryNext(cursor, direction, targets, out Vector2 next))
                cursor = next;
            else
                cursor += direction * step;
        }
    }

    private void ResetDpad()
    {
        Array.Clear(dpadHeld, 0, dpadHeld.Length);
        Array.Clear(dpadNextRepeat, 0, dpadNextRepeat.Length);
    }

    internal void OnFocusLost()
    {
        requestedGamepadId = 0;
        ReleaseAll();
    }

    private void ReleaseAll()
    {
        platform.ControllerMoveFactor = 1f;
        platform.ControllerMoveAxes = Vector2.Zero;
        sneakToggle.Reset(gamepad != 0 && Button(profile.SneakButton));
        UpdateGyroState(false);
        if (gamepad != 0 && rumblePlaying) SDL_RumbleGamepad(gamepad, 0, 0, 0);
        if (gamepad != 0 && triggerRumblePlaying) SDL_RumbleGamepadTriggers(gamepad, 0, 0, 0);
        rumblePlaying = triggerRumblePlaying = false;
        foreach (HeldKey key in heldKeys.Values) SendKey(key, false);
        heldKeys.Clear();
        foreach (EnumMouseButton button in heldMouse.ToArray()) SetMouse(button, false);
        leftShoulderHeld = rightShoulderHeld = false;
        settingsButtonWasDown = false;
        lookRemainderX = lookRemainderY = 0;
        rightStickWasActive = false;
        ResetDpad();
    }

    private static string Error() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "unknown error";
    private void LoadGamepadMappings()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "gamecontrollerdb.txt");
        string custom = Path.Combine(GamePaths.ModConfig, "gamecontrollerdb.txt");
        foreach (string path in new[] { bundled, custom })
        {
            if (!File.Exists(path)) continue;
            int added = SDL_AddGamepadMappingsFromFile(path);
            if (added < 0)
                platform.Logger.Warning("[VulkanStory] SDL3 gamepad mappings failed from {0}: {1}",
                    path, Error());
            else
                platform.Logger.Notification("[VulkanStory] SDL3 gamepad mappings: {0} from {1}",
                    added, path);
        }
    }
    private void Disable(string reason)
    {
        unavailable = true;
        ReleaseAll();
        OptimumControllerHints.Publish(null);
        ScreenManager.GuiComposers?.MarkAllDialogsForRecompose();
        platform.Logger.Warning("[VulkanStory] SDL3 gamepad input unavailable: {0}", reason);
    }

    public void Dispose()
    {
        CloseKeyboard();
        OptimumControllerHints.Publish(null);
        damageAttributes?.UnregisterListener(OnPlayerHurt);
        damageAttributes = null;
        Interlocked.Exchange(ref pendingDamageRumble, 0f);
        FlushProfile(true);
        FlushPendingProfiles(true);
        buttonCapture.Cancel();
        ReleaseAll();
        if (gamepad != 0) SDL_CloseGamepad(gamepad);
        gamepad = 0;
        gamepadId = 0;
        requestedGamepadId = 0;
        if (initialized) SDL_QuitSubSystem(GamepadSubsystem);
        initialized = false;
        nextGamepadScan = 0;
    }

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_InitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetError();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_AddGamepadMappingsFromFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_UpdateGamepads();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetGamepads(out int count);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_free(nint memory);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_OpenGamepad(int instanceId);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_CloseGamepad(nint gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetGamepadName(nint gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern ushort SDL_GetGamepadVendor(nint gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern ushort SDL_GetGamepadProduct(nint gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetGamepadSerial(nint gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GamepadHasSensor(nint gamepad, int sensorType);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetGamepadSensorEnabled(nint gamepad, int sensorType, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetGamepadSensorData(nint gamepad, int sensorType, float* data, int count);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_RumbleGamepad(nint gamepad, ushort lowFrequency, ushort highFrequency, uint durationMs);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_RumbleGamepadTriggers(nint gamepad, ushort left, ushort right, uint durationMs);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern short SDL_GetGamepadAxis(nint gamepad, int axis);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetGamepadButton(nint gamepad, int button);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_GetGamepadButtonLabel(nint gamepad, int button);
}
