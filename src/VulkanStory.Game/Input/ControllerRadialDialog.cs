using System;
using Cairo;
using OpenTK.Mathematics;
using Vintagestory.API.Client;

namespace VulkanStory.Game.Input;

internal sealed class ControllerRadialDialog : GuiDialog
{
    internal static readonly string[] Actions = ["inventory", "menu", "settings", "drop", "previous", "next", "firstslot", "screenshot", "none"];
    private static readonly string[] Labels = ["Inventory", "Pause", "Settings", "Drop", "Previous", "Next", "Slot 1", "Screenshot", "Empty"];
    private readonly string[] slots;
    internal int Selected { get; private set; } = -1;
    internal string? SelectedAction => Selected >= 0 ? slots[Selected] : null;

    internal ControllerRadialDialog(ICoreClientAPI api, string[] actions) : base(api)
    {
        slots = (string[])actions.Clone();
        ElementBounds background = ElementStdBounds.DialogBackground().WithFixedPadding(12, 12);
        SingleComposer = api.Gui.CreateCompo("vulkanstory-controller-radial", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background)
            .BeginChildElements(background)
            .AddStaticText("Choose an action. B cancels.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 0, 360, 24))
            .AddDynamicCustomDraw(ElementBounds.Fixed(0, 28, 360, 360), DrawWheel, "wheel")
            .EndChildElements().Compose();
    }

    public override bool DisableMouseGrab => true;
    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.97;
    public override double InputOrder => 0.02;

    internal static string Label(string action)
    {
        int index = Array.IndexOf(Actions, action);
        return index >= 0 ? Labels[index] : "Empty";
    }

    internal void Select(Vector2 direction)
    {
        int selected = -1;
        if (direction.Length >= 0.35f)
        {
            double angle = Math.Atan2(direction.X, -direction.Y);
            if (angle < 0) angle += Math.Tau;
            selected = (int)Math.Floor(angle / (Math.Tau / 8) + 0.5) % 8;
        }
        if (Selected == selected) return;
        Selected = selected;
        (SingleComposer.GetElement("wheel") as GuiElementCustomDraw)?.Redraw();
    }

    private void DrawWheel(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        // Dynamic custom draw owns a local surface, not the dialog's canvas.
        double cx = bounds.OuterWidth / 2, cy = bounds.OuterHeight / 2;
        double radius = Math.Min(bounds.OuterWidth, bounds.OuterHeight) / 2 - 8;
        double step = Math.Tau / 8;
        CairoFont font = CairoFont.WhiteSmallText().WithFontSize(18);
        font.SetupContext(ctx);
        for (int i = 0; i < 8; i++)
        {
            double angle = -Math.PI / 2 + i * step;
            ctx.MoveTo(cx, cy);
            ctx.Arc(cx, cy, radius, angle - step / 2, angle + step / 2);
            ctx.ClosePath();
            if (Selected == i) ctx.SetSourceRGBA(0.65, 0.42, 0.12, 0.95);
            else ctx.SetSourceRGBA(0.12, 0.12, 0.12, 0.88);
            ctx.FillPreserve();
            ctx.SetSourceRGBA(0.8, 0.75, 0.65, 0.8);
            ctx.LineWidth = 1;
            ctx.Stroke();
            string label = Label(slots[i]);
            ctx.SetSourceRGBA(1, 1, 1, 1);
            double width = font.GetTextExtents(label).Width;
            capi.Gui.Text.DrawTextLine(ctx, font, label,
                cx + Math.Cos(angle) * radius * 0.64 - width / 2,
                cy + Math.Sin(angle) * radius * 0.64 - 8);
        }
    }
}
