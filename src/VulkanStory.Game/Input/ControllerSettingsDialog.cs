using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

/// <summary>Small in-game controller panel; device-specific edits apply immediately.</summary>
internal sealed class ControllerSettingsDialog : GuiDialog
{
    private readonly SdlGamepadInput input;
    private int page;
    private bool refreshRequested;
    private float scrollOffset;
    private (int Width, int Height, float Scale) geometry;
    private int composedPage = -1;

    /// <summary>Creates the device-specific controller settings host and composes its initial page.</summary>
    public ControllerSettingsDialog(ICoreClientAPI api, SdlGamepadInput input) : base(api)
    {
        this.input = input;
        ComposePanel();
    }

    /// <inheritdoc />
    public override bool DisableMouseGrab => true;
    /// <inheritdoc />
    public override string ToggleKeyCombinationCode => null!;
    /// <inheritdoc />
    public override double DrawOrder => 0.95;
    /// <inheritdoc />
    public override double InputOrder => 0.05;

    /// <summary>Defers recomposition until the controller poll's UI refresh boundary.</summary>
    public void RequestRefresh() => refreshRequested = true;
    /// <summary>Selects and immediately composes a page for the isolated UI diagnostic driver.</summary>
    internal void ShowDiagnosticPage(int index) { page = index; ComposePanel(); }

    /// <summary>Consumes a requested recomposition only while the dialog remains open.</summary>
    public void ApplyPendingRefresh()
    {
        if (!IsOpened() || (!refreshRequested && geometry ==
            (capi.Render.FrameWidth, capi.Render.FrameHeight, RuntimeEnv.GUIScale))) return;
        refreshRequested = false;
        ComposePanel();
    }

    /// <summary>Rebuilds the selected controller page and initializes widgets from a validated profile snapshot.</summary>
    private void ComposePanel()
    {
        ClearComposers();
        ControllerProfile profile = input.CurrentProfile;
        geometry = (capi.Render.FrameWidth, capi.Render.FrameHeight, RuntimeEnv.GUIScale);
        double scale = Math.Max(.1, RuntimeEnv.GUIScale), padding = GuiStyle.ElementToDialogPadding;
        double width = Math.Max(180, Math.Min(510, capi.Render.FrameWidth / scale - 2 * padding - 20));
        double height = Math.Max(108, Math.Min(680, capi.Render.FrameHeight / scale - 2 * padding - 30));
        double contentWidth = width - 25, rowScale = contentWidth / 485;
        double visibleHeight = height - 82;
        if (page != composedPage) scrollOffset = 0;
        composedPage = page;
        ElementBounds background = ElementBounds.Fixed(0, 0, width, height).WithFixedPadding(padding);
        GuiComposer composer = capi.Gui.CreateCompo("vulkanstory-controller-settings", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar("Controller: " + profile.Name, OnTitleBarClose)
            .BeginChildElements(background);
        var initialize = new List<Action>();

        string[] pages = ["Tuning", "Actions", "More", "Axes", "Inventory", "Radial menu", "Gameplay gestures", "Inventory gestures", "Modifier"];
        composer.AddDropDown(pages, pages, page, (index, _) => { page = index; RequestRefresh(); }, ElementBounds.Fixed(0, 0, contentWidth, 28));
        var clip = ElementBounds.Fixed(0, 42, contentWidth, visibleHeight);
        var body = ElementBounds.Fixed(0, 0, contentWidth, 1).WithParent(clip);
        composer.AddVerticalScrollbar(value =>
        {
            if (!ReferenceEquals(SingleComposer, composer)) return;
            scrollOffset = value; body.fixedY = -value;
            body.MarkDirtyRecursive(); body.CalcWorldBounds();
        }, ElementBounds.Fixed(width - 20, 42, 20, visibleHeight), "controller-scroll")
            .BeginClip(clip).BeginChildElements(body);
        double y = 0;
        if (page == 0)
        {
            Slider("Movement deadzone", "moveDeadzone", value => { input.UpdateProfile(p => p.MoveDeadzone = value / 100f); RequestRefresh(); return true; }, (int)MathF.Round(profile.MoveDeadzone * 100), 0, 80, 1);
            Slider("Movement outer deadzone", "moveOuter", value => { input.UpdateProfile(p => p.MoveOuterDeadzone = value / 100f); return true; }, (int)MathF.Round(profile.MoveOuterDeadzone * 100), 0, (int)MathF.Round(MathF.Min(.2f, .95f - profile.MoveDeadzone) * 100), 1);
            Slider("Movement response curve", "moveCurve", value => { input.UpdateProfile(p => p.MoveCurveExponent = value / 100f); return true; }, (int)MathF.Round(profile.MoveCurveExponent * 100), 50, 300, 10);
            Slider("Look deadzone", "lookDeadzone", value => { input.UpdateProfile(p => p.LookDeadzone = value / 100f); RequestRefresh(); return true; }, (int)MathF.Round(profile.LookDeadzone * 100), 0, 80, 1);
            Slider("Look outer deadzone", "lookOuter", value => { input.UpdateProfile(p => p.LookOuterDeadzone = value / 100f); return true; }, (int)MathF.Round(profile.LookOuterDeadzone * 100), 0, (int)MathF.Round(MathF.Min(.2f, .95f - profile.LookDeadzone) * 100), 1);
            Slider("Look response curve", "lookCurve", value => { input.UpdateProfile(p => p.LookCurveExponent = value / 100f); return true; }, (int)MathF.Round(profile.LookCurveExponent * 100), 100, 300, 10);
            Slider("Look sensitivity", "lookSensitivity", value => { input.UpdateProfile(p => p.LookSensitivity = value); return true; }, (int)profile.LookSensitivity, 20, 3000, 20);
            Slider("Menu cursor speed", "cursorSensitivity", value => { input.UpdateProfile(p => p.CursorSensitivity = value); return true; }, (int)profile.CursorSensitivity, 20, 3000, 20);
            Slider("Gyro sensitivity", "gyroSensitivity", value => { input.UpdateProfile(p => p.GyroSensitivity = value); return true; }, (int)profile.GyroSensitivity, 20, 3000, 20);
            Switch("Invert look Y", "invertLookY", profile.InvertLookY, value => input.UpdateProfile(p => p.InvertLookY = value));
            Switch("Left stick moves menu cursor", "leftMenuCursor", profile.MenuCursorUsesLeftStick, value => input.UpdateProfile(p => p.MenuCursorUsesLeftStick = value));
            Switch("Gyro aiming", "gyroEnabled", profile.GyroEnabled, value => input.UpdateProfile(p => p.GyroEnabled = value));
            Switch("Gyro only with use trigger", "gyroAimOnly", profile.GyroRequireSecondaryTrigger,
                value => input.UpdateProfile(p => p.GyroRequireSecondaryTrigger = value));
            Switch("Controller rumble", "rumbleEnabled", profile.RumbleEnabled, value => input.UpdateProfile(p => p.RumbleEnabled = value));
            Switch("Trigger rumble", "triggerRumbleEnabled", profile.TriggerRumbleEnabled,
                value => input.UpdateProfile(p => p.TriggerRumbleEnabled = value));
        }
        else if (page is 6 or 7)
        {
            composer.AddSmallButton("Gameplay", () => { page = 6; RequestRefresh(); return true; }, RowBounds(0, y, 160, 28))
                .AddSmallButton("Inventory", () => { page = 7; RequestRefresh(); return true; }, RowBounds(170, y, 160, 28));
            y += 40;
            Slider("Tap / double-press window", "tapWindow", value => { input.UpdateProfile(p => p.TapThresholdMs = value); RequestRefresh(); return true; }, profile.TapThresholdMs, 100, 1000, 50);
            Slider("Long-press duration", "longWindow", value => { input.UpdateProfile(p => p.LongPressThresholdMs = value); return true; }, profile.LongPressThresholdMs, profile.TapThresholdMs + 50, 2000, 50);
            foreach (ControllerButtonBinding binding in page == 6 ? ControllerButtonBindings.All : ControllerButtonBindings.Inventory)
            {
                if (binding.Code is "radial" or "back") continue;
                string action = page == 7 ? "gui:" + binding.Code : binding.Code;
                ControllerGestureMode fallback = page == 7 || binding.Code is not ("accept" or "sneak" or "sprint")
                    ? ControllerGestureMode.Press : ControllerGestureMode.Hold;
                bool configured = profile.ActionModes.TryGetValue(action, out ControllerGestureMode mode);
                if (!configured) mode = fallback;
                composer.AddStaticText(binding.Label, CairoFont.WhiteSmallishText(), RowBounds(0, y, 205, 28))
                    .AddSmallButton(configured ? mode.ToString() : "Default", () =>
                    {
                        input.UpdateProfile(p =>
                        {
                            if (configured && mode == ControllerGestureMode.DoubleToggle) p.ActionModes.Remove(action);
                            else p.ActionModes[action] = configured ? (ControllerGestureMode)((int)mode + 1) : ControllerGestureMode.Hold;
                        });
                        RequestRefresh();
                        return true;
                    }, RowBounds(215, y, 270, 28));
                y += 36;
            }
        }
        else if (page == 8)
        {
            Switch("Enable modifier layer", "modifierEnabled", profile.ModifierEnabled, value => input.UpdateProfile(p => p.ModifierEnabled = value));
            composer.AddStaticText("Hold modifier", CairoFont.WhiteSmallishText(), RowBounds(0, y, 205, 28))
                .AddSmallButton(input.ButtonName(profile.ModifierButton), () => { input.UpdateProfile(p => p.ModifierButton = (p.ModifierButton + 1) % 32); RequestRefresh(); return true; }, RowBounds(215, y, 270, 28));
            y += 40;
            foreach (ControllerButtonBinding binding in ControllerButtonBindings.All)
            {
                if (binding.Code is "back" or "radial" or "settings") continue;
                string action = binding.Code;
                int button = profile.ModifierBindings.GetValueOrDefault(action, -1);
                composer.AddStaticText(binding.Label, CairoFont.WhiteSmallishText(), RowBounds(0, y, 205, 28))
                    .AddSmallButton(button < 0 ? "Use main binding" : input.ButtonName(button), () =>
                    {
                        input.UpdateProfile(p =>
                        {
                            int next = button + 1;
                            if (next == p.ModifierButton) next++;
                            ControllerLayerBindings.Assign(p.ModifierBindings, action, next > 31 ? -1 : next);
                        });
                        RequestRefresh();
                        return true;
                    }, RowBounds(215, y, 270, 28));
                y += 36;
            }
        }
        else if (page == 5)
        {
            Switch("Enable radial action menu", "radialEnabled", profile.RadialEnabled, value => input.UpdateProfile(p => p.RadialEnabled = value));
            Switch("Hold to open radial menu", "radialHold", profile.RadialHoldToOpen, value => input.UpdateProfile(p => p.RadialHoldToOpen = value));
            for (int i = 0; i < 8; i++)
            {
                int slot = i;
                string action = profile.RadialActions[slot];
                composer.AddStaticText("Slot " + (slot + 1), CairoFont.WhiteSmallishText(), RowBounds(0, y, 160, 28))
                    .AddSmallButton(ControllerRadialDialog.Label(action), () =>
                    {
                        int next = (Array.IndexOf(ControllerRadialDialog.Actions, action) + 1) % ControllerRadialDialog.Actions.Length;
                        input.UpdateProfile(p => p.RadialActions[slot] = ControllerRadialDialog.Actions[next]);
                        RequestRefresh();
                        return true;
                    }, RowBounds(180, y, 250, 28));
                y += 40;
            }
        }
        else if (page <= 2 || page == 4)
        {
            ControllerButtonBinding[] bindings = page == 4 ? ControllerButtonBindings.Inventory : ControllerButtonBindings.All;
            int first = page == 4 ? 0 : (page - 1) * 5;
            int end = page == 1 ? Math.Min(first + 5, bindings.Length) : bindings.Length;
            for (int i = first; i < end; i++)
            {
                ControllerButtonBinding binding = bindings[i];
                string glyph = ControllerGlyphs.ButtonGlyph(binding.Get(profile), input.ButtonName);
                composer.AddStaticText(binding.Label, CairoFont.WhiteSmallishText(), RowBounds(0, y, 205, 26))
                    .AddStaticCustomDraw(RowBounds(215, y, 48, 26),
                        (ctx, surface, bounds) => ControllerGlyphs.Draw(ctx, capi, bounds, glyph))
                    .AddSmallButton(input.ButtonName(binding.Get(profile)),
                        () => BeginBinding(binding.Code), RowBounds(270, y, 215, 26));
                y += 35;
            }
            if (page == 2)
            {
                composer.AddStaticText("Swap left/right triggers", CairoFont.WhiteSmallishText(),
                    RowBounds(0, y, 205, 26))
                    .AddSmallButton("Swap", () =>
                    {
                        input.UpdateProfile(p =>
                        {
                            (p.PrimaryTriggerAxis, p.SecondaryTriggerAxis) =
                                (p.SecondaryTriggerAxis, p.PrimaryTriggerAxis);
                            (p.PrimaryTriggerNegative, p.SecondaryTriggerNegative) =
                                (p.SecondaryTriggerNegative, p.PrimaryTriggerNegative);
                        });
                        RequestRefresh();
                        return true;
                    }, RowBounds(215, y, 90, 26));
                y += 40;
                Switch("Toggle sneak", "toggleSneak", profile.ToggleSneak,
                    value => input.UpdateProfile(p => p.ToggleSneak = value));
                Switch("Toggle sprint (game)", "toggleSprint", ClientSettings.ToggleSprint,
                    value => ClientSettings.ToggleSprint = value);
            }
            string status = input.PendingBinding is string action
                ? (input.BindingWaitingForRelease ? "Release buttons, then press a new one for " : "Press a new button for ") +
                    ControllerButtonBindings.Find(action).Label
                : "Choose an action, then press a gamepad button.";
            composer.AddStaticText(status, CairoFont.WhiteSmallText(), RowBounds(0, y + 4, 485, 28));
            composer.AddStaticText("Start + Back cancels", CairoFont.WhiteSmallText(), RowBounds(0, y + 32, 485, 24));
            y += 64;
            composer.AddSmallButton("Cancel binding", CancelBinding, RowBounds(0, y, 150, 28));
        }
        else
        {
            AxisRow("Move horizontal", profile.MoveXAxis, profile.InvertMoveX,
                (p, axis) => p.MoveXAxis = axis, (p, invert) => p.InvertMoveX = invert);
            AxisRow("Move vertical", profile.MoveYAxis, profile.InvertMoveY,
                (p, axis) => p.MoveYAxis = axis, (p, invert) => p.InvertMoveY = invert);
            AxisRow("Look horizontal", profile.LookXAxis, profile.InvertLookX,
                (p, axis) => p.LookXAxis = axis, (p, invert) => p.InvertLookX = invert);
            AxisRow("Look vertical", profile.LookYAxis, profile.InvertLookY,
                (p, axis) => p.LookYAxis = axis, (p, invert) => p.InvertLookY = invert);
            AxisRow("Left click", profile.PrimaryTriggerAxis, profile.PrimaryTriggerNegative,
                (p, axis) => p.PrimaryTriggerAxis = axis,
                (p, negative) => p.PrimaryTriggerNegative = negative);
            AxisRow("Right click", profile.SecondaryTriggerAxis, profile.SecondaryTriggerNegative,
                (p, axis) => p.SecondaryTriggerAxis = axis,
                (p, negative) => p.SecondaryTriggerNegative = negative);
            composer.AddStaticText("Choose an axis, then its active direction.", CairoFont.WhiteSmallText(),
                RowBounds(0, y + 4, 485, 24));
            y += 34;
        }
        composer.AddStaticText("Text field: LS + RS opens controller keyboard",
            CairoFont.WhiteSmallText(), RowBounds(0, y + 2, 485, 25));
        y += 30;
        body.fixedHeight = y;
        composer.EndChildElements().EndClip();
        composer.AddSmallButton("Close", ClosePanel, ElementBounds.Fixed(width - 110, height - 30, 110, 30));
        SingleComposer = composer.EndChildElements().Compose();
        foreach (Action action in initialize) action();
        var scrollbar = composer.GetScrollbar("controller-scroll");
        scrollbar.SetHeights((float)visibleHeight, (float)Math.Max(visibleHeight, y));
        scrollbar.CurrentYPosition = Math.Clamp(scrollOffset, 0, (float)Math.Max(0, y - visibleHeight));
        scrollbar.TriggerChanged();

        ElementBounds RowBounds(double x, double rowY, double rowWidth, double rowHeight) =>
            ElementBounds.Fixed(x * rowScale, rowY, rowWidth * rowScale, rowHeight);

        void Slider(string label, string key, ActionConsumable<int> changed, int value, int min, int max, int step)
        {
            composer.AddStaticText(label, CairoFont.WhiteSmallishText(), RowBounds(0, y, 205, 26))
                .AddSlider(changed, RowBounds(215, y, 270, 26), key);
            int snapped = Math.Clamp(min + (int)Math.Round((value - min) / (double)step) * step, min, max);
            initialize.Add(() => composer.GetSlider(key).SetValues(snapped, min, max, step));
            y += 40;
        }

        void Switch(string label, string key, bool value, Action<bool> changed)
        {
            composer.AddStaticText(label, CairoFont.WhiteSmallishText(), RowBounds(0, y, 205, 26))
                .AddSwitch(changed, RowBounds(215, y, 35, 26), key);
            initialize.Add(() => composer.GetSwitch(key).SetValue(value));
            y += 40;
        }

        void AxisRow(string label, int axis, bool negative,
            Action<ControllerProfile, int> setAxis, Action<ControllerProfile, bool> setDirection)
        {
            string glyph = ControllerGlyphs.AxisGlyph(axis, negative, input.ButtonName);
            composer.AddStaticText(label, CairoFont.WhiteSmallishText(), RowBounds(0, y, 150, 26))
                .AddStaticCustomDraw(RowBounds(160, y, 40, 26),
                    (ctx, surface, bounds) => ControllerGlyphs.Draw(ctx, capi, bounds, glyph))
                .AddSmallButton(SdlGamepadInput.AxisName(axis), () =>
                {
                    input.UpdateProfile(p => setAxis(p, (axis + 1) % 6));
                    RequestRefresh();
                    return true;
                }, RowBounds(205, y, 155, 26))
                .AddSmallButton(negative ? "Negative" : "Positive", () =>
                {
                    input.UpdateProfile(p => setDirection(p, !negative));
                    RequestRefresh();
                    return true;
                }, RowBounds(375, y, 110, 26));
            y += 35;
        }
    }

    private bool BeginBinding(string action) { input.BeginBinding(action); return true; }
    private bool CancelBinding() { input.CancelBinding(); return true; }
    private bool ClosePanel() { TryClose(); return true; }
    private void OnTitleBarClose() => TryClose();
    /// <inheritdoc />
    /// <remarks>Ends button capture and forces the current device profile's pending write.</remarks>
    public override void OnGuiClosed()
    {
        input.CancelBinding(refresh: false);
        input.FlushProfile(true);
    }
}
