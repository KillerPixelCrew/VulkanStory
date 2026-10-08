using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using VulkanStory.Platform.Sdl;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

/// <summary>
/// Polls SDL's gamepad subsystem beside the SDL-owned window and frame loop.
/// </summary>
/// <remarks>Owns the selected native gamepad, synthetic held input, per-device profile edits, controller dialogs, gyro, and haptics.</remarks>
internal sealed unsafe class SdlGamepadInput : IDisposable
{
    private const uint GamepadSubsystem = 0x00002000;
    private const int GyroSensor = 2; // SDL_SENSOR_GYRO
    private readonly IControllerPlatformHost platform;
    private readonly Dictionary<string, HeldKey> heldKeys = new();
    private readonly HashSet<EnumMouseButton> heldMouse = new();
    private readonly ControllerToggleState sneakToggle = new();
    private readonly ControllerContextTransition inputContext = new();
    private readonly Dictionary<string, ControllerGesture> gestures = new(StringComparer.Ordinal);
    private bool modifierActive;
    private bool? publishedGuiContext, publishedModifierContext;
    private uint layerBlockedButtons;
    private readonly short[] sampledAxes = new short[6];
    private uint sampledButtons, previousButtons;
    private uint supportedButtons;
    private bool sampleValid;
    private ControllerProfile profile = new();
    private string? profilePath;
    private string? profileKey;
    private bool profileDirty;
    private long profileWriteDue;
    private readonly List<PendingProfileWrite> pendingProfileWrites = new();
    private long pendingProfileRetryDue;
    private ControllerSettingsDialog? settingsDialog;
    private ControllerRadialDialog? radialDialog;
    private ClientMain? radialGame;
    private bool radialButtonWasDown;
    private ClientMain? settingsGame;
    private ControllerKeyboardDialog? keyboardDialog;
    private ControllerKeyboardScreen? keyboardScreen;
    private ClientMain? keyboardGame;
    private bool keyboardChordWasDown;
    private bool keyboardBackConsumed;
    private readonly ControllerButtonCapture buttonCapture = new();
    private bool gyroAvailable;
    private bool gyroEnabled;
    private bool rumbleAvailable = true;
    private bool triggerRumbleAvailable = true;
    private bool rumblePlaying;
    private bool triggerRumblePlaying;
    private SyncedTreeAttribute? damageAttributes;
    private ClientMain? damageGame;
    private float pendingDamageRumble;
    private nint gamepad;
    private int gamepadId;
    private string gamepadName = "none";
    /// <summary>Throttled connection/routing diagnostic text for the current controller.</summary>
    internal string Status { get; private set; } = "waiting for SDL gamepad detection";
    /// <summary>Whether the latest poll routed a nonidle controller state.</summary>
    internal bool InputActive { get; private set; }
    /// <summary>Collects the current foreground inventory geometry for semantic slot actions.</summary>
    internal List<ControllerSlotTarget> CurrentSlotTargets() =>
        ControllerGuiTargets.SlotTargets(ControllerGuiTargets.ActiveComposers(platform));
    private string? loggedRoutingState;
    private bool firstInputLogged;
    private int loggedGamepadCount = -1, failedOpenId;
    private int requestedGamepadId;
    private readonly Action<int> onGamepadActivity;
    private long lastPoll;
    private long nextGamepadScan;
    private long nextStatusUpdate;
    private long nextMenuScroll;
    private bool initialized;
    private bool unavailable;
    private float lookRemainderX;
    private float lookRemainderY;
    private bool rightStickWasActive;
    private readonly bool[] dpadHeld = new bool[4];
    private readonly long[] dpadNextRepeat = new long[4];

    /// <summary>Exact game hotkey mapping retained until its matching synthetic release is sent.</summary>
    private readonly record struct HeldKey(int Code, bool Alt, bool Ctrl, bool Shift);
    /// <summary>Unsaved per-device snapshot retained when selection moves to another controller.</summary>
    private readonly record struct PendingProfileWrite(string Path, string Key, ControllerProfile Profile);

    /// <summary>Registers the SDL import resolver and retains the session host; native subsystem setup is deferred.</summary>
    public SdlGamepadInput(IControllerPlatformHost platform)
    {
        SdlNativeLibrary.RegisterAssemblyImports(typeof(SdlGamepadInput).Assembly);
        this.platform = platform;
        onGamepadActivity = id =>
        {
            if (platform.HasControllerWindow && platform.IsFocused &&
                id > 0 && id != gamepadId) requestedGamepadId = id;
        };
    }

    /// <summary>Fresh validated profile snapshot for settings composition; callers do not edit the active profile directly.</summary>
    internal ControllerProfile CurrentProfile => profile.Validated();
    /// <summary>Readable name for a standard SDL gamepad axis, or Unknown for an unsupported index.</summary>
    internal static string AxisName(int axis) => axis switch
    {
        0 => "Left X", 1 => "Left Y", 2 => "Right X", 3 => "Right Y",
        4 => "Left trigger", 5 => "Right trigger", _ => "Unknown"
    };
    /// <summary>Readable physical-button name using SDL's device-specific face labels when connected.</summary>
    internal string ButtonName(int button) => ControllerButtonBindings.Name(
        button, gamepad != 0 ? SDL_GetGamepadButtonLabel(gamepad, button) : 0);
    /// <summary>Action currently awaiting remapping, or null when capture is idle.</summary>
    internal string? PendingBinding => buttonCapture.Action;
    /// <summary>Whether remapping is ignoring initially held buttons until release.</summary>
    internal bool BindingWaitingForRelease => buttonCapture.WaitingForRelease;

    /// <summary>Validates the action, begins release-before-press capture, and requests settings recomposition.</summary>
    internal void BeginBinding(string action)
    {
        ControllerButtonBindings.Find(action);
        buttonCapture.Begin(action, PressedButtonMask());
        settingsDialog?.RequestRefresh();
    }

    /// <summary>Ends remapping capture and optionally requests settings recomposition.</summary>
    internal void CancelBinding(bool refresh = true)
    {
        buttonCapture.Cancel();
        if (refresh) settingsDialog?.RequestRefresh();
    }

    /// <summary>Applies an edit to a validated copy, updates prompts/gyro, and schedules persistence after a half-second debounce.</summary>
    /// <param name="change">Mutation applied to the copy before it is validated again.</param>
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

    /// <summary>Persists a dirty device profile when due, logging supported I/O/JSON failures and delaying retry.</summary>
    /// <param name="force">Whether to bypass the debounce deadline.</param>
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
            platform.Original.Logger.Warning("[VulkanStory] Controller settings could not be saved: {0}", error.Message);
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
                platform.Original.Logger.Warning("[VulkanStory] Controller settings still could not be saved: {0}",
                    error.Message);
            }
        }
        pendingProfileRetryDue = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
    }

    /// <summary>Pumps SDL and processes one controller sample when this object owns the event-drain boundary.</summary>
    /// <param name="onWindowEvent">Optional neutral window/input event consumer.</param>
    /// <param name="onInputPumped">Optional callback at the completed input-pump boundary.</param>
    /// <remarks>The process adapter uses PrepareForEventPump/PollAfterEventPump to avoid a second queue drain.</remarks>
    public void Poll(Action<SdlInputEvent>? onWindowEvent = null, Action? onInputPumped = null)
        => PollCore(onWindowEvent, onInputPumped, false, false);

    // The process adapter owns the queue. These phases preserve the retained
    // native-update/input-marker ordering without draining SDL a second time.
    /// <summary>Prepares the subsystem and refreshes SDL gamepad state before the process-owned queue drain.</summary>
    internal void PrepareForEventPump()
    {
        Prepare();
        if (initialized && !unavailable) SDL_UpdateGamepads();
    }
    /// <summary>Requests an active-device switch for a focused, live window when another gamepad emits activity.</summary>
    internal void NoteGamepadActivity(int id) => onGamepadActivity(id);
    /// <summary>Processes controller state after the process adapter already drained SDL, propagating its hotplug flag.</summary>
    internal void PollAfterEventPump(bool deviceChanged) => PollCore(null, null, true, deviceChanged);

    /// <summary>Samples input, manages context/dialog ownership, and dispatches gameplay or GUI actions for one frame.</summary>
    /// <remarks>SDL missing-library/entry failures disable this optional path; synthetic controls release on context loss.</remarks>
    private void PollCore(Action<SdlInputEvent>? onWindowEvent, Action? onInputPumped,
        bool alreadyPumped, bool externalDeviceChanged)
    {
        InputActive = false;
        sampleValid = false;
        try
        {
            Prepare();
            if (unavailable && onWindowEvent == null) return;

            // One queue reader owns gamepad and window events. Keep the SDL
            // window responsive when the optional gamepad subsystem is absent.
            // The gamepad refresh precedes the input marker and event dispatch.
            bool deviceChanged = alreadyPumped ? externalDeviceChanged : SdlEventPump.Drain(onWindowEvent, onInputPumped,
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
            LogFirstInput();
            sampledButtons = ReadButtonMask();
            for (int axis = 0; axis < sampledAxes.Length; axis++) sampledAxes[axis] = gamepad == 0 ? (short)0 : SDL_GetGamepadAxis(gamepad, axis);
            sampleValid = true;
            bool inWorld = platform.IsFocused && platform.MouseGrabbed;
            Vector2 rawMoveStick = ReadStick(profile.MoveXAxis, profile.MoveYAxis, profile.MoveDeadzone, profile.MoveOuterDeadzone);
            rawMoveStick *= new Vector2(profile.InvertMoveX ? -1f : 1f, profile.InvertMoveY ? -1f : 1f);
            Vector2 movementStick = ControllerStickProcessing.Process(rawMoveStick, 0f, 0f, profile.MoveCurveExponent);
            Vector2 lookStick = ReadStick(profile.LookXAxis, profile.LookYAxis, profile.LookDeadzone, profile.LookOuterDeadzone);
            object? foreground = inWorld ? null : ControllerGuiTargets.ForegroundOwner(platform);
            if (inputContext.Update(inWorld, foreground, sampledButtons,
                RawTrigger(profile.PrimaryTriggerAxis, profile.PrimaryTriggerNegative),
                RawTrigger(profile.SecondaryTriggerAxis, profile.SecondaryTriggerNegative), movementStick, lookStick))
            {
                ReleaseAll();
                nextMenuScroll = 0;
            }
            movementStick = inputContext.Movement(movementStick);
            lookStick = inputContext.Look(lookStick);
            bool modifier = profile.ModifierEnabled && inWorld &&
                (inputContext.Buttons(sampledButtons) & (1u << profile.ModifierButton)) != 0;
            if (modifier != modifierActive)
            {
                uint formerlyHeld = previousButtons;
                ReleaseAll();
                layerBlockedButtons |= formerlyHeld & sampledButtons & ~(1u << profile.ModifierButton);
                modifierActive = modifier;
            }
            layerBlockedButtons &= sampledButtons;
            if (publishedGuiContext != !inWorld || publishedModifierContext != modifierActive) PublishControllerHints();
            float dt = lastPoll == 0 ? 0f : Math.Clamp((now - lastPoll) / (float)Stopwatch.Frequency, 0f, 0.05f);
            lastPoll = now;
            settingsDialog?.ApplyPendingRefresh();
            if (gamepad == 0 || !platform.IsFocused || ScreenManager.hotkeyManager == null || platform.Original.keyEventHandlers.Count == 0)
            {
                Status = gamepad == 0 ? "no SDL gamepad connected" : gamepadName + ": " +
                    (!platform.IsFocused ? "window not focused" : ScreenManager.hotkeyManager == null
                        ? "game hotkeys unavailable" : "game key handlers unavailable");
                LogRoutingState(Status);
                ReleaseAll();
                return;
            }
            LogRoutingState(gamepadName + ": routing to " + (platform.MouseGrabbed ? "world" : "menu"));

            if (radialDialog != null)
            {
                if (!radialDialog.IsOpened() || !ReferenceEquals(ControllerGuiTargets.ActiveGame(platform), radialGame))
                {
                    CloseRadial();
                    ReleaseAll();
                    return;
                }
                radialDialog.Select(ReadStick(profile.LookXAxis, profile.LookYAxis, profile.LookDeadzone, profile.LookOuterDeadzone));
                bool rawDown = (sampledButtons & (1u << profile.RadialButton)) != 0;
                bool cancel = Pressed(profile.GuiBackButton) || Button(profile.MenuButton);
                bool confirm = Pressed(profile.GuiSelectButton) || (profile.RadialHoldToOpen ? !rawDown : rawDown && !radialButtonWasDown);
                radialButtonWasDown = rawDown;
                string? action = !cancel && confirm ? radialDialog.SelectedAction : null;
                if (cancel || confirm)
                {
                    CloseRadial();
                    ReleaseAll();
                    if (action != null) ExecuteRadialAction(action);
                }
                else ReleaseAll();
                return;
            }
            if (profile.RadialEnabled && inWorld && Pressed(profile.RadialButton) && !Button(profile.MenuButton))
            {
                if (ControllerGuiTargets.ActiveGame(platform)?.api is { } radialApi)
                {
                    OpenRadial(ControllerGuiTargets.ActiveGame(platform)!);
                    NoteControllerActivity();
                    ReleaseAll();
                    return;
                }
            }

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
            bool backDown = Button(profile.GuiBackButton);
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

            bool settingsDown = ActionPulse("settings", profile.SettingsButton, ControllerGestureMode.Press);
            if (settingsDown)
            {
                NoteControllerActivity();
                ToggleSettings();
                ReleaseAll();
                inputContext.Reset();
                return;
            }

            float lx = movementStick.X, ly = movementStick.Y;
            platform.ControllerMoveAxes = inWorld ? movementStick : Vector2.Zero;
            platform.ControllerMoveFactor = inWorld
                ? Math.Clamp(MathF.Sqrt(lx * lx + ly * ly), 0f, 1f) : 1f;
            float rx = lookStick.X, ry = lookStick.Y;
            bool rightStickActive = MathF.Abs(rx) > 0.15f || MathF.Abs(ry) > 0.15f;
            if (rightStickActive && !rightStickWasActive) NoteControllerActivity();
            rightStickWasActive = rightStickActive;
            bool analogWalking = inWorld &&
                platform.AnalogServerReady;
            SetKey("walkleft", inWorld && Moving("walkleft", -lx, analogWalking), GlKeys.A);
            SetKey("walkright", inWorld && Moving("walkright", lx, analogWalking), GlKeys.D);
            SetKey("walkforward", inWorld && Moving("walkforward", -ly, analogWalking), GlKeys.W);
            SetKey("walkbackward", inWorld && Moving("walkbackward", ly, analogWalking), GlKeys.S);
            bool south = inWorld && ActionDown("accept", profile.AcceptButton);
            bool guiSelect = !inWorld && ActionPulse("gui:select", profile.GuiSelectButton, ControllerGestureMode.Press);
            bool guiHalf = !inWorld && ActionPulse("gui:takehalf", profile.TakeHalfButton, ControllerGestureMode.Press);
            bool guiQuick = !inWorld && ActionPulse("gui:quickmove", profile.QuickMoveButton, ControllerGestureMode.Press);
            bool guiCancel = !inWorld && ActionPulse("gui:cancel", profile.GuiBackButton, ControllerGestureMode.Press);
            // Diagnostic text is read by the settings panel, not the input consumer.
            // Avoid formatting four floats and allocating strings every render frame.
            if (now >= nextStatusUpdate)
            {
                nextStatusUpdate = now + Stopwatch.Frequency / 4;
                Status = gamepadName + ": SDL move " + lx.ToString("0.00") + ", " + ly.ToString("0.00") +
                    "; look " + rx.ToString("0.00") + ", " + ry.ToString("0.00") +
                    "; A " + (south ? "down" : "up") + "; routing " + (platform.MouseGrabbed ? "world" : "menu");
            }
            bool menuNavigation = !inWorld &&
                (guiSelect || guiHalf || guiQuick || Button(profile.GuiSelectButton) ||
                 Button(profile.TakeHalfButton) || Button(profile.QuickMoveButton) ||
                 Button(11) || Button(12) || Button(13) || Button(14));
            List<GuiComposer>? composers = menuNavigation
                ? keyboardScreen?.IsOpened == true
                    ? new List<GuiComposer> { keyboardScreen.ElementComposer }
                    : keyboardDialog?.IsOpened() == true
                    ? keyboardDialog.Composers.Values.Where(composer => composer.Enabled).ToList()
                    : ControllerGuiTargets.ActiveComposers(platform)
                : null;
            GuiElementItemSlotGridBase? focusedGrid = menuNavigation ? FocusedSlotGrid(composers!) : null;
            GuiElementSlider? focusedSlider = menuNavigation && focusedGrid == null ? FocusedSlider(composers!) : null;
            List<ControllerSlotTarget>? slotTargets = menuNavigation ? ControllerGuiTargets.SlotTargets(composers!) : null;
            bool overSlot = slotTargets?.Exists(target => target.Bounds.PointInside(
                platform.ControllerCursorPosition.X, platform.ControllerCursorPosition.Y)) == true;
            SetKey("jump", inWorld && south, GlKeys.Space); // South / A
            bool sneakPressed = inWorld && ActionDown("sneak", profile.SneakButton);
            SetKey("sneak", profile.ActionModes.ContainsKey("sneak") ? sneakPressed :
                sneakToggle.Update(sneakPressed, profile.ToggleSneak, inWorld), GlKeys.LShift);
            SetKey("sprint", inWorld && ActionDown("sprint", profile.SprintButton), GlKeys.LControl);
            // Vintage Story keeps crafting in the inventory, so X and Y open the same dialog.
            bool inventoryAction = inWorld && ActionDown("inventory", profile.InventoryButton, ControllerGestureMode.Press);
            bool craftingAction = inWorld && ActionDown("crafting", profile.CraftingButton, ControllerGestureMode.Press);
            SetKey("inventorydialog", inventoryAction || craftingAction, GlKeys.E);
            SetKey("escapemenudialog", ActionDown("menu", profile.MenuButton, ControllerGestureMode.Press) || guiCancel, GlKeys.Escape);
            SetKey("dropitem", inWorld && ActionDown("drop", profile.DropButton, ControllerGestureMode.Press), GlKeys.Q);
            // Opening/closing a dialog can change context synchronously inside
            // key dispatch. Stop this sample before it becomes an action there.
            if (platform.MouseGrabbed != inWorld || (!inWorld &&
                !ReferenceEquals(foreground, ControllerGuiTargets.ForegroundOwner(platform))))
            {
                ReleaseAll();
                return;
            }

            if (!inWorld && overSlot && ControllerGuiTargets.ActiveGame(platform)?.api is { } inventoryApi)
            {
                Vector2 cursor = platform.ControllerCursorPosition;
                if (guiSelect) ControllerInventoryActions.TryClick(inventoryApi, slotTargets!, cursor, ControllerInventoryAction.Select);
                else if (guiHalf) ControllerInventoryActions.TryClick(inventoryApi, slotTargets!, cursor, ControllerInventoryAction.TakeHalf);
                else if (guiQuick) ControllerInventoryActions.TryClick(inventoryApi, slotTargets!, cursor, ControllerInventoryAction.QuickMove);
            }

            SetMouse(EnumMouseButton.Left, (inWorld && Trigger(profile.PrimaryTriggerAxis, profile.PrimaryTriggerNegative)) ||
                (!inWorld && !overSlot && (profile.ActionModes.ContainsKey("gui:select") ? guiSelect : Button(profile.GuiSelectButton))));
            SetMouse(EnumMouseButton.Right, (inWorld && Trigger(profile.SecondaryTriggerAxis, profile.SecondaryTriggerNegative)) ||
                (!inWorld && !overSlot && (profile.ActionModes.ContainsKey("gui:takehalf") ? guiHalf : Button(profile.TakeHalfButton))));
            bool leftShoulder = Button(profile.PreviousHotbarButton), rightShoulder = Button(profile.NextHotbarButton);
            InputActive = lx != 0f || ly != 0f || rx != 0f || ry != 0f ||
                heldKeys.Count != 0 || heldMouse.Count != 0 || leftShoulder || rightShoulder ||
                Button(11) || Button(12) || Button(13) || Button(14);
            if (inWorld)
            {
                // The game's wheel-up direction selects the previous hotbar slot.
                if (ActionPulse("previous", profile.PreviousHotbarButton, ControllerGestureMode.Press)) Scroll(1);
                if (ActionPulse("next", profile.NextHotbarButton, ControllerGestureMode.Press)) Scroll(-1);
            }
            if (inWorld)
            {
                ResetDpad();
                Vector2 curvedLook = ControllerStickProcessing.Process(lookStick, 0f, 0f,
                    profile.LookCurveExponent);
                lookRemainderX += curvedLook.X * profile.LookSensitivity * 1.5f * dt * (profile.InvertLookX ? -1f : 1f);
                lookRemainderY += curvedLook.Y * profile.LookSensitivity * 1.5f * dt * (profile.InvertLookY ? -1f : 1f);
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
                    foreach (MouseEventHandler handler in platform.Original.mouseEventHandlers)
                        handler.OnMouseMove(new MouseEvent(0, 0, dx, dy));
            }
            else if (platform.HasControllerWindow)
            {
                UpdateGyroState(false);
                if (Pressed(profile.PreviousHotbarButton) || Pressed(profile.NextHotbarButton))
                {
                    var tab = new HeldKey((int)GlKeys.Tab, false, false, Pressed(profile.PreviousHotbarButton));
                    SendKey(tab, true);
                    SendKey(tab, false);
                }
                // A focused inventory grid already understands arrows and Enter.
                // Otherwise snap to the next active widget/slot, with a fixed
                // step as a fallback for UIs without discoverable elements.
                float step = Math.Max(1f, RuntimeEnv.GUIScale) *
                    (float)(GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding);
                Vector2 pos = platform.ControllerCursorPosition;
                Vector2 originalPos = pos;
                bool up = DpadPulse(11, now), down = DpadPulse(12, now);
                bool left = DpadPulse(13, now), right = DpadPulse(14, now);
                List<Vector2>? targets = (up || down || left || right)
                    ? focusedGrid != null && slotTargets is { Count: > 0 }
                        ? slotTargets.Select(target => target.Center).ToList()
                        : ControllerGuiTargets.Collect(platform, composers ?? ControllerGuiTargets.ActiveComposers(platform)) : null;
                if (up) DpadMove(GlKeys.Up, new Vector2(0, -1), step, focusedGrid, focusedSlider, targets, ref pos);
                if (down) DpadMove(GlKeys.Down, new Vector2(0, 1), step, focusedGrid, focusedSlider, targets, ref pos);
                if (left) DpadMove(GlKeys.Left, new Vector2(-1, 0), step, focusedGrid, focusedSlider, targets, ref pos);
                if (right) DpadMove(GlKeys.Right, new Vector2(1, 0), step, focusedGrid, focusedSlider, targets, ref pos);
                (int width, int height) = platform.ControllerWindowSize;
                Vector2 cursorStick = profile.MenuCursorUsesLeftStick ? inputContext.Movement(rawMoveStick) : lookStick;
                Vector2 wheelStick = profile.MenuCursorUsesLeftStick ? lookStick : inputContext.Movement(rawMoveStick);
                pos.X = Math.Clamp(pos.X + cursorStick.X * profile.CursorSensitivity * dt, 0, Math.Max(0, width - 1));
                pos.Y = Math.Clamp(pos.Y + cursorStick.Y * profile.CursorSensitivity * dt, 0, Math.Max(0, height - 1));
                if (Math.Abs(wheelStick.Y) < 0.5f) nextMenuScroll = 0;
                else if (now >= nextMenuScroll)
                {
                    Scroll(wheelStick.Y < 0 ? 1 : -1);
                    nextMenuScroll = now + Stopwatch.Frequency / 5;
                }
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
                // SDL can emit mouse motion even for a warp to the same point.
                // An idle controller must not drive GUI hover/texture work every frame.
                if (pos != originalPos) platform.ControllerCursorPosition = pos;
            }
            previousButtons = inputContext.Buttons(sampledButtons);
        }
        catch (DllNotFoundException error) { Disable(error.Message); }
        catch (EntryPointNotFoundException error) { Disable(error.Message); }
    }

    // The SDL client calls this before AMD's INPUT stage so first-use subsystem
    // setup and mapping-file I/O are outside the keyed input-processing window.
    /// <summary>Initializes the optional gamepad subsystem and loads bundled/custom mappings once, disabling it on native-load failure.</summary>
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

    /// <summary>Selects a connected requested device, then the current device, then the first device, or zero for none.</summary>
    internal static int SelectGamepad(ReadOnlySpan<int> ids, int current, int requested)
    {
        foreach (int id in ids) if (id == requested && requested != 0) return id;
        foreach (int id in ids) if (id == current && current != 0) return id;
        return ids.IsEmpty ? 0 : ids[0];
    }

    private void LogRoutingState(string state)
    {
        if (state == loggedRoutingState) return;
        loggedRoutingState = state;
        platform.Original.Logger.Notification("[VulkanStory] Controller input: {0}", state);
    }

    private void LogFirstInput()
    {
        if (gamepad == 0 || firstInputLogged) return;
        uint buttons = PressedButtonMask();
        short lx = SDL_GetGamepadAxis(gamepad, profile.MoveXAxis), ly = SDL_GetGamepadAxis(gamepad, profile.MoveYAxis);
        short rx = SDL_GetGamepadAxis(gamepad, profile.LookXAxis), ry = SDL_GetGamepadAxis(gamepad, profile.LookYAxis);
        if (buttons == 0 && Math.Abs((int)lx) < 8192 && Math.Abs((int)ly) < 8192 &&
            Math.Abs((int)rx) < 8192 && Math.Abs((int)ry) < 8192 &&
            SDL_GetGamepadAxis(gamepad, profile.PrimaryTriggerAxis) < 16384 &&
            SDL_GetGamepadAxis(gamepad, profile.SecondaryTriggerAxis) < 16384) return;
        firstInputLogged = true;
        platform.Original.Logger.Notification("[VulkanStory] First SDL controller input: {0}; buttons=0x{1}; move={2},{3}; look={4},{5}; focused={6}",
            gamepadName, buttons.ToString("X"), lx, ly, rx, ry, platform.IsFocused);
    }

    /// <summary>Reconciles connected devices, retires the previous device's UI/input, and loads the selected device's profile.</summary>
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
        if (count != loggedGamepadCount)
        {
            loggedGamepadCount = count;
            platform.Original.Logger.Notification("[VulkanStory] SDL gamepad scan: {0} devices; selected ID {1}", count, chosen);
        }
        if (chosen == gamepadId && (chosen == 0 || gamepad != 0))
        {
            if (deviceChanged && gamepad != 0)
            {
                ReadSupportedButtons();
                PublishControllerHints();
                settingsDialog?.RequestRefresh();
            }
            return;
        }
        FlushProfile(true);
        if (chosen != 0) RememberUnsavedProfile();
        ReleaseAll();
        inputContext.Reset();
        previousButtons = 0;
        CloseRadial();
        CloseSettings();
        CloseKeyboard();
        buttonCapture.Cancel();
        if (gamepad != 0) SDL_CloseGamepad(gamepad);
        gamepad = 0;
        gamepadId = chosen;
        supportedButtons = 0;
        firstInputLogged = false;
        nextStatusUpdate = 0;
        if (chosen == 0) { gamepadName = "none"; Status = "no SDL gamepad connected"; PublishControllerHints(); return; }
        gamepad = SDL_OpenGamepad(chosen);
        if (gamepad != 0)
        {
            failedOpenId = 0;
            ReadSupportedButtons();
            string name = Marshal.PtrToStringUTF8(SDL_GetGamepadName(gamepad)) ?? chosen.ToString();
            gamepadName = name;
            Status = name + ": opened by SDL";
            string? serial = Marshal.PtrToStringUTF8(SDL_GetGamepadSerial(gamepad));
            string key = ControllerProfileStore.DeviceKey(SDL_GetGamepadVendor(gamepad), SDL_GetGamepadProduct(gamepad), name, serial);
            profilePath = Path.Combine(GamePaths.ModConfig, "vulkanstory-controllers.json");
            profileKey = key;
            if (TakeUnsavedProfile(profilePath, key) is { } unsaved)
            {
                profile = unsaved.Validated();
                profileDirty = true;
                profileWriteDue = Stopwatch.GetTimestamp();
            }
            else try
            {
                ControllerProfileStore.ImportLegacy(profilePath, Path.Combine(GamePaths.ModConfig, "optimum-controllers.json"));
                profile = ControllerProfileStore.LoadDevice(profilePath, key, name);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                profile = new ControllerProfile();
                platform.Original.Logger.Warning("[VulkanStory] Controller profile could not be loaded: {0}", error.Message);
            }
            gyroAvailable = profile.GyroEnabled && SDL_GamepadHasSensor(gamepad, GyroSensor);
            gyroEnabled = false;
            rumbleAvailable = triggerRumbleAvailable = true;
            if (profile.GyroEnabled && !gyroAvailable)
                platform.Original.Logger.Warning("[VulkanStory] {0} has no SDL3 gyroscope; stick look remains active", name);
            platform.Original.Logger.Notification("[VulkanStory] SDL3 gamepad connected: {0} (profile {1})", name, key);
        }
        else if (failedOpenId != chosen)
        {
            failedOpenId = chosen;
            platform.Original.Logger.Warning("[VulkanStory] SDL_OpenGamepad failed for ID {0}: {1}", chosen, Error());
        }
        PublishControllerHints();
    }

    private void PublishControllerHints()
    {
        bool gui = !platform.IsFocused || !platform.MouseGrabbed;
        bool modifier = !gui && modifierActive;
        publishedGuiContext = gui;
        publishedModifierContext = modifier;
        ControllerHints.Publish(gamepad == 0 ? null : ControllerGlyphs.Build(profile, ButtonName, gui, modifier));
        ScreenManager.GuiComposers?.MarkAllDialogsForRecompose();
    }

    private void NoteControllerActivity()
    {
        if (gamepad != 0 && ControllerHints.SetControllerActive(true))
            ScreenManager.GuiComposers?.MarkAllDialogsForRecompose();
    }

    private Vector2 ReadStick(int xAxis, int yAxis, float inner, float outer)
    {
        return ControllerStickProcessing.Process(new Vector2(Axis(xAxis) / 32767f, Axis(yAxis) / 32767f), inner, outer);
    }

    private short Axis(int axis) => sampleValid ? sampledAxes[axis] : gamepad == 0 ? (short)0 : SDL_GetGamepadAxis(gamepad, axis);

    private bool Moving(string action, float magnitude, bool analogWalking) => magnitude >
        (heldKeys.ContainsKey(action)
            ? (analogWalking ? 0.01f : profile.MoveReleaseThreshold)
            : (analogWalking ? 0.02f : profile.MovePressThreshold));

    private bool Button(int button) => button is >= 0 and < 32 &&
        (inputContext.Buttons(sampleValid ? sampledButtons : ReadButtonMask()) & ~layerBlockedButtons &
         ~(profile.ModifierEnabled && modifierActive ? 1u << profile.ModifierButton : 0u) & (1u << button)) != 0;

    private bool ActionDown(string action, int button, ControllerGestureMode fallback = ControllerGestureMode.Hold)
    {
        if (modifierActive) button = ControllerLayerBindings.Resolve(profile.ModifierBindings, action, button);
        if (!gestures.TryGetValue(action, out ControllerGesture? gesture)) gestures[action] = gesture = new ControllerGesture();
        ControllerGestureMode mode = profile.ActionModes.GetValueOrDefault(action, fallback);
        return gesture.Update(Button(button), mode, Environment.TickCount64, profile.TapThresholdMs, profile.LongPressThresholdMs);
    }

    private bool ActionPulse(string action, int button, ControllerGestureMode fallback)
    {
        bool previous = gestures.TryGetValue(action, out ControllerGesture? gesture) && gesture.Output;
        return ActionDown(action, button, fallback) && !previous;
    }
    private bool Pressed(int button) => Button(button) && (previousButtons & (1u << button)) == 0;
    private uint PressedButtonMask() => sampleValid ? sampledButtons : ReadButtonMask();
    private uint ReadButtonMask()
    {
        if (gamepad == 0) return 0;
        uint mask = 0;
        for (int button = 0; button < 32; button++)
            if ((supportedButtons & (1u << button)) != 0 && SDL_GetGamepadButton(gamepad, button)) mask |= 1u << button;
        return mask;
    }

    private void ReadSupportedButtons()
    {
        supportedButtons = 0;
        for (int button = 0; button < 32; button++)
            if (SDL_GamepadHasButton(gamepad, button)) supportedButtons |= 1u << button;
    }
    private bool RawTrigger(int axis, bool negative) => AxisThresholdPressed(Axis(axis), negative, profile.TriggerThreshold);
    private bool Trigger(int axis, bool negative)
    {
        bool raw = RawTrigger(axis, negative);
        if (axis == profile.PrimaryTriggerAxis && negative == profile.PrimaryTriggerNegative) return inputContext.Primary(raw);
        if (axis == profile.SecondaryTriggerAxis && negative == profile.SecondaryTriggerNegative) return inputContext.Secondary(raw);
        return raw;
    }

    /// <summary>Tests a signed raw SDL axis against a normalized threshold using the requested polarity.</summary>
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
            platform.Original.Logger.Warning("[VulkanStory] SDL3 gyro could not be enabled: {0}", Error());
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
            int axis = trigger == EnumMouseButton.Left ? profile.PrimaryTriggerAxis :
                trigger == EnumMouseButton.Right ? profile.SecondaryTriggerAxis : -1;
            ushort left = axis == 4 ? strength : (ushort)0;
            ushort right = axis == 5 ? strength : (ushort)0;
            if (left == 0 && right == 0) return;
            triggerRumbleAvailable = SDL_RumbleGamepadTriggers(gamepad, left, right, duration);
            triggerRumblePlaying = triggerRumbleAvailable;
        }
    }

    private void SyncDamageHaptics()
    {
        ClientMain? currentGame = gamepad != 0 ? ControllerGuiTargets.ActiveGame(platform) : null;
        BindDamageAttributes(currentGame?.EntityPlayer?.WatchedAttributes);
        damageGame = damageAttributes != null ? currentGame : null;
    }

    /// <summary>Moves the hurt listener to the current player's attributes and discards any previous pending haptic.</summary>
    internal void BindDamageAttributes(SyncedTreeAttribute? current)
    {
        if (current == null) damageGame = null;
        if (ReferenceEquals(current, damageAttributes)) return;
        damageAttributes?.UnregisterListener(OnPlayerHurt);
        damageAttributes = current;
        damageGame = null;
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
        // Share the physical wheel's cumulative position; a fixed +/-1 value
        // makes consumers comparing successive positions jump or stop stepping.
        platform.InjectControllerMouseWheel(direction);
    }

    private void ToggleSettings()
    {
        if (settingsDialog?.IsOpened() == true) { settingsDialog.TryClose(); return; }
        OpenSettings();
    }

    /// <summary>Opens controller settings for the active game, closing the keyboard and reusing only a matching game-owned dialog.</summary>
    /// <returns>False without a connected gamepad/game API or when the dialog refuses to open.</returns>
    /// <remarks>Dialog construction failures are logged and rethrown.</remarks>
    internal bool OpenSettings()
    {
        CloseKeyboard();
        ClientMain? game = ControllerGuiTargets.ActiveGame(platform);
        if (gamepad == 0 || game?.api == null)
        {
            platform.Original.Logger.Warning("[VulkanStory] Controller settings cannot open: {0}; current screen={1}",
                gamepad == 0 ? "no open SDL gamepad" : "active game/API not found",
                platform.ControllerCurrentScreen()?.GetType().Name ?? "none");
            return false;
        }
        if (settingsGame != game)
        {
            CloseSettings();
            settingsGame = game;
        }
        try
        {
            settingsDialog ??= new ControllerSettingsDialog(game.api, this);
            bool opened = settingsDialog.IsOpened() || settingsDialog.TryOpen();
            platform.Original.Logger.Notification("[VulkanStory] Controller settings open: {0}", opened);
            return opened;
        }
        catch (Exception error)
        {
            platform.Original.Logger.Warning("[VulkanStory] Controller settings open failed: {0}", error);
            throw;
        }
    }
    /// <summary>Returns the open settings dialog only when its owning client matches by reference.</summary>
    internal ControllerSettingsDialog? OpenedSettings(ClientMain owner) =>
        ReferenceEquals(settingsGame, owner) && settingsDialog?.IsOpened() == true ? settingsDialog : null;

    /// <summary>Creates an explicitly selected controller page for the isolated headless UI harness.</summary>
    /// <exception cref="InvalidOperationException">The harness is disabled or the new dialog cannot open.</exception>
    internal void OpenDiagnosticSettings(ClientMain game, int page)
    {
        if (!HeadlessHarnessOptions.Enabled) throw new InvalidOperationException("Controller UI diagnostics require the isolated harness.");
        CloseSettings();
        settingsGame = game;
        settingsDialog = new ControllerSettingsDialog(game.api, this);
        settingsDialog.ShowDiagnosticPage(page);
        if (!settingsDialog.TryOpen()) throw new InvalidOperationException("Controller settings diagnostic could not open.");
    }
    /// <summary>Whether both client and dialog identify the current settings ownership.</summary>
    internal bool OwnsSettings(ClientMain owner, ControllerSettingsDialog dialog) =>
        ReferenceEquals(settingsGame, owner) && ReferenceEquals(settingsDialog, dialog);

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

    private void CloseSettings()
    {
        ControllerSettingsDialog? dialog = settingsDialog;
        ClientMain? game = settingsGame;
        settingsDialog = null;
        settingsGame = null;
        if (dialog == null) return;
        try { dialog.TryClose(); }
        finally
        {
            try { game?.UnregisterDialog(dialog); }
            finally { dialog.Dispose(); }
        }
    }

    private void CloseRadial()
    {
        ControllerRadialDialog? dialog = radialDialog;
        ClientMain? game = radialGame;
        radialDialog = null;
        radialGame = null;
        radialButtonWasDown = false;
        inputContext.Reset();
        if (dialog == null) return;
        try { dialog.TryClose(); }
        finally { try { game?.UnregisterDialog(dialog); } finally { dialog.Dispose(); } }
    }

    /// <summary>Replaces any old wheel with a new game-owned wheel using the active profile's eight actions.</summary>
    /// <returns>Whether the dialog opened; an unsuccessful wheel is closed and disposed.</returns>
    internal bool OpenRadial(ClientMain game)
    {
        CloseRadial();
        radialGame = game;
        radialDialog = new ControllerRadialDialog(game.api, profile.RadialActions, ButtonName(profile.GuiBackButton));
        radialButtonWasDown = true;
        if (radialDialog.TryOpen()) return true;
        CloseRadial();
        return false;
    }

    /// <summary>Returns the open wheel only for its owning client.</summary>
    internal ControllerRadialDialog? OpenedRadial(ClientMain game) =>
        ReferenceEquals(radialGame, game) && radialDialog?.IsOpened() == true ? radialDialog : null;

    private void ExecuteRadialAction(string action)
    {
        switch (action)
        {
            case "inventory": PulseKey("inventorydialog", GlKeys.E); break;
            case "menu": PulseKey("escapemenudialog", GlKeys.Escape); break;
            case "settings": OpenSettings(); break;
            case "drop": PulseKey("dropitem", GlKeys.Q); break;
            case "previous": Scroll(1); break;
            case "next": Scroll(-1); break;
            case "firstslot":
                if (ControllerGuiTargets.ActiveGame(platform)?.api?.World?.Player is { } player)
                    player.InventoryManager.ActiveHotbarSlotNumber = 0;
                break;
            case "screenshot": PulseKey("screenshot", GlKeys.F12); break;
        }
    }

    private void PulseKey(string action, GlKeys fallback)
    {
        SetKey(action, true, fallback);
        SetKey(action, false, fallback);
    }

    /// <summary>Retires the departing world's dialogs/hurt listener and releases controls unless a new world already owns input.</summary>
    internal void WorldLeaving(ClientMain game)
    {
        if (ReferenceEquals(radialGame, game)) CloseRadial();
        if (ReferenceEquals(settingsGame, game)) CloseSettings();
        if (ReferenceEquals(keyboardGame, game)) CloseKeyboard();
        if (ReferenceEquals(damageGame, game) || ReferenceEquals(damageAttributes, game.EntityPlayer?.WatchedAttributes))
            BindDamageAttributes(null);
        // The UI can already belong to a new world before its first scene capture.
        // Retire old-world associations without releasing the new world's controls.
        if (ControllerGuiTargets.ActiveGame(platform) is { } current && !ReferenceEquals(current, game)) return;
        buttonCapture.Cancel();
        ReleaseAll();
        inputContext.Reset();
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
            dpadNextRepeat[index] = now + Stopwatch.Frequency / 2;
            return true;
        }
        if (now < dpadNextRepeat[index]) return false;
        dpadNextRepeat[index] = now + Stopwatch.Frequency / 10;
        return true;
    }

    private void DpadMove(GlKeys key, Vector2 direction, float step, GuiElementItemSlotGridBase? grid,
        GuiElementSlider? slider,
        IReadOnlyList<Vector2>? targets, ref Vector2 cursor)
    {
        NoteControllerActivity();
        if (grid != null)
        {
            if (targets != null && ControllerCursorNavigation.TryNext(cursor, direction, targets, out Vector2 next)) cursor = next;
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

    /// <summary>Closes owned controller UI, releases synthetic controls/haptics, unbinds hurt state, and flushes profile writes.</summary>
    internal void SuspendInput()
    {
        CloseSettings();
        CloseKeyboard();
        buttonCapture.Cancel();
        OnFocusLost();
        BindDamageAttributes(null);
        lastPoll = 0;
        FlushProfile(true);
        FlushPendingProfiles(true);
    }

    // Retry pending profile writes while disabled without polling controller input.
    /// <summary>Retries due profile writes while controller processing is disabled.</summary>
    internal void PollSuspended()
    {
        LogRoutingState("disabled in VulkanStory settings");
        FlushProfile(false);
        FlushPendingProfiles(false);
    }
    /// <summary>Closes the radial wheel, cancels requested device switching, and releases synthetic input until physical release.</summary>
    internal void OnFocusLost()
    {
        CloseRadial();
        requestedGamepadId = 0;
        ReleaseAll();
        inputContext.Reset();
    }

    /// <summary>Releases owned synthetic keys/mouse buttons, resets movement/gesture/repeat state, and stops gyro/haptics.</summary>
    private void ReleaseAll()
    {
        gestures.Clear();
        InputActive = false;
        previousButtons = inputContext.Buttons(sampledButtons);
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
        lookRemainderX = lookRemainderY = 0;
        rightStickWasActive = false;
        ResetDpad();
    }

    private static string Error() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "unknown error";
    private void LoadGamepadMappings()
    {
        string assemblyDirectory = Path.GetDirectoryName(typeof(SdlGamepadInput).Assembly.Location)!;
        string bundled = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "assets", "gamecontrollerdb.txt"));
        string custom = Path.Combine(GamePaths.ModConfig, "gamecontrollerdb.txt");
        foreach (string path in new[] { bundled, custom })
        {
            if (!File.Exists(path)) continue;
            int added = SDL_AddGamepadMappingsFromFile(path);
            if (added < 0)
                platform.Original.Logger.Warning("[VulkanStory] SDL3 gamepad mappings failed from {0}: {1}",
                    path, Error());
            else
                platform.Original.Logger.Notification("[VulkanStory] SDL3 gamepad mappings: {0} from {1}",
                    added, path);
        }
    }
    private void Disable(string reason)
    {
        unavailable = true;
        ReleaseAll();
        ControllerHints.Publish(null);
        ScreenManager.GuiComposers?.MarkAllDialogsForRecompose();
        platform.Original.Logger.Warning("[VulkanStory] SDL3 gamepad input unavailable: {0}", reason);
    }

    /// <summary>Closes owned UI/native gamepad, releases synthetic input and listeners, flushes profiles, and quits the subsystem.</summary>
    public void Dispose()
    {
        CloseRadial();
        CloseSettings();
        CloseKeyboard();
        ControllerHints.Publish(null);
        BindDamageAttributes(null);
        FlushProfile(true);
        FlushPendingProfiles(true);
        buttonCapture.Cancel();
        ReleaseAll();
        inputContext.Reset();
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
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GamepadHasButton(nint gamepad, int button);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_GetGamepadButtonLabel(nint gamepad, int button);
}
