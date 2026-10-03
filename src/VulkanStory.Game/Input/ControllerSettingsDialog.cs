using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Input;

/// <summary>Small in-game controller panel; device-specific edits apply immediately.</summary>
internal sealed class ControllerSettingsDialog : GuiDialog
{
    private readonly SdlGamepadInput input;
    private int page;
    private bool refreshRequested;

    public ControllerSettingsDialog(ICoreClientAPI api, SdlGamepadInput input) : base(api)
    {
        this.input = input;
        ComposePanel();
    }

    public override bool DisableMouseGrab => true;
    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.95;
    public override double InputOrder => 0.05;

    public void RequestRefresh() => refreshRequested = true;

    public void ApplyPendingRefresh()
    {
        if (!refreshRequested || !IsOpened()) return;
        refreshRequested = false;
        ComposePanel();
    }

    private void ComposePanel()
    {
        ClearComposers();
        ControllerProfile profile = input.CurrentProfile;
        ElementBounds background = ElementStdBounds.DialogBackground()
            .WithFixedPadding(GuiStyle.ElementToDialogPadding, GuiStyle.ElementToDialogPadding);
        GuiComposer composer = capi.Gui.CreateCompo("vulkanstory-controller-settings", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background)
            .AddDialogTitleBar("Controller: " + profile.Name, OnTitleBarClose)
            .BeginChildElements(background);
        var initialize = new List<Action>();

        composer.AddSmallButton("Tuning", ShowTuning, ElementBounds.Fixed(0, 0, 115, 28))
            .AddSmallButton("Actions", ShowActions, ElementBounds.Fixed(125, 0, 115, 28))
            .AddSmallButton("More", ShowMore, ElementBounds.Fixed(250, 0, 115, 28))
            .AddSmallButton("Axes", ShowAxes, ElementBounds.Fixed(375, 0, 115, 28));
        double y = 46;
        if (page == 0)
        {
            Slider("Movement deadzone", "moveDeadzone", OnMoveDeadzone, (int)MathF.Round(profile.MoveDeadzone * 100), 0, 80, 1);
            Slider("Look deadzone", "lookDeadzone", OnLookDeadzone, (int)MathF.Round(profile.LookDeadzone * 100), 0, 80, 1);
            Slider("Look sensitivity", "lookSensitivity", OnLookSensitivity, (int)profile.LookSensitivity, 20, 3000, 20);
            Slider("Menu cursor speed", "cursorSensitivity", OnCursorSensitivity, (int)profile.CursorSensitivity, 20, 3000, 20);
            Slider("Gyro sensitivity", "gyroSensitivity", OnGyroSensitivity, (int)profile.GyroSensitivity, 20, 3000, 20);
            Switch("Invert look Y", "invertLookY", profile.InvertLookY, value => input.UpdateProfile(p => p.InvertLookY = value));
            Switch("Gyro aiming", "gyroEnabled", profile.GyroEnabled, value => input.UpdateProfile(p => p.GyroEnabled = value));
            Switch("Gyro only with right trigger", "gyroAimOnly", profile.GyroRequireSecondaryTrigger,
                value => input.UpdateProfile(p => p.GyroRequireSecondaryTrigger = value));
            Switch("Controller rumble", "rumbleEnabled", profile.RumbleEnabled, value => input.UpdateProfile(p => p.RumbleEnabled = value));
            Switch("Trigger rumble", "triggerRumbleEnabled", profile.TriggerRumbleEnabled,
                value => input.UpdateProfile(p => p.TriggerRumbleEnabled = value));
        }
        else if (page <= 2)
        {
            int first = (page - 1) * 5;
            for (int i = first; i < Math.Min(first + 5, ControllerButtonBindings.All.Length); i++)
            {
                ControllerButtonBinding binding = ControllerButtonBindings.All[i];
                string glyph = ControllerGlyphs.ButtonGlyph(binding.Get(profile), input.ButtonName);
                composer.AddStaticText(binding.Label, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, 205, 26))
                    .AddStaticCustomDraw(ElementBounds.Fixed(215, y, 48, 26),
                        (ctx, surface, bounds) => ControllerGlyphs.Draw(ctx, capi, bounds, glyph))
                    .AddSmallButton(input.ButtonName(binding.Get(profile)),
                        () => BeginBinding(binding.Code), ElementBounds.Fixed(270, y, 215, 26));
                y += 35;
            }
            if (page == 2)
            {
                composer.AddStaticText("Swap left/right triggers", CairoFont.WhiteSmallishText(),
                    ElementBounds.Fixed(0, y, 205, 26))
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
                    }, ElementBounds.Fixed(215, y, 90, 26));
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
            composer.AddStaticText(status, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y + 4, 485, 28));
            composer.AddStaticText("Start + Back cancels", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y + 32, 485, 24));
            y += 64;
            composer.AddSmallButton("Cancel binding", CancelBinding, ElementBounds.Fixed(0, y, 150, 28));
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
                ElementBounds.Fixed(0, y + 4, 485, 24));
            y += 34;
        }
        composer.AddStaticText("Text field: LS + RS opens controller keyboard",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y + 2, 485, 25));
        y += 30;
        composer.AddSmallButton("Close", ClosePanel, ElementBounds.Fixed(375, y + 12, 110, 30));
        SingleComposer = composer.EndChildElements().Compose();
        foreach (Action action in initialize) action();

        void Slider(string label, string key, ActionConsumable<int> changed, int value, int min, int max, int step)
        {
            composer.AddStaticText(label, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, 205, 26))
                .AddSlider(changed, ElementBounds.Fixed(215, y, 270, 26), key);
            int snapped = Math.Clamp(min + (int)Math.Round((value - min) / (double)step) * step, min, max);
            initialize.Add(() => composer.GetSlider(key).SetValues(snapped, min, max, step));
            y += 40;
        }

        void Switch(string label, string key, bool value, Action<bool> changed)
        {
            composer.AddStaticText(label, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, 205, 26))
                .AddSwitch(changed, ElementBounds.Fixed(215, y, 35, 26), key);
            initialize.Add(() => composer.GetSwitch(key).SetValue(value));
            y += 40;
        }

        void AxisRow(string label, int axis, bool negative,
            Action<ControllerProfile, int> setAxis, Action<ControllerProfile, bool> setDirection)
        {
            string glyph = ControllerGlyphs.AxisGlyph(axis, negative, input.ButtonName);
            composer.AddStaticText(label, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, 150, 26))
                .AddStaticCustomDraw(ElementBounds.Fixed(160, y, 40, 26),
                    (ctx, surface, bounds) => ControllerGlyphs.Draw(ctx, capi, bounds, glyph))
                .AddSmallButton(SdlGamepadInput.AxisName(axis), () =>
                {
                    input.UpdateProfile(p => setAxis(p, (axis + 1) % 6));
                    RequestRefresh();
                    return true;
                }, ElementBounds.Fixed(205, y, 155, 26))
                .AddSmallButton(negative ? "Negative" : "Positive", () =>
                {
                    input.UpdateProfile(p => setDirection(p, !negative));
                    RequestRefresh();
                    return true;
                }, ElementBounds.Fixed(375, y, 110, 26));
            y += 35;
        }
    }

    private bool OnMoveDeadzone(int value) { input.UpdateProfile(p => p.MoveDeadzone = value / 100f); return true; }
    private bool OnLookDeadzone(int value) { input.UpdateProfile(p => p.LookDeadzone = value / 100f); return true; }
    private bool OnLookSensitivity(int value) { input.UpdateProfile(p => p.LookSensitivity = value); return true; }
    private bool OnCursorSensitivity(int value) { input.UpdateProfile(p => p.CursorSensitivity = value); return true; }
    private bool OnGyroSensitivity(int value) { input.UpdateProfile(p => p.GyroSensitivity = value); return true; }
    private bool ShowTuning() { page = 0; RequestRefresh(); return true; }
    private bool ShowActions() { page = 1; RequestRefresh(); return true; }
    private bool ShowMore() { page = 2; RequestRefresh(); return true; }
    private bool ShowAxes() { page = 3; RequestRefresh(); return true; }
    private bool BeginBinding(string action) { input.BeginBinding(action); return true; }
    private bool CancelBinding() { input.CancelBinding(); return true; }
    private bool ClosePanel() { TryClose(); return true; }
    private void OnTitleBarClose() => TryClose();
    public override void OnGuiClosed()
    {
        input.CancelBinding(refresh: false);
        input.FlushProfile(true);
    }
}
