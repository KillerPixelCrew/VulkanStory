using System;
using Cairo;
using OpenTK.Mathematics;
using Vintagestory.API.Client;

namespace VulkanStory.Game.Input;

/// <summary>Eight-sector in-world action wheel selected by processed controller stick direction.</summary>
internal sealed class ControllerRadialDialog : GuiDialog
{
    private static readonly (string Action, string Label)[] Definitions =
        [("inventory", "Inventory"), ("menu", "Pause"), ("settings", "Settings"), ("drop", "Drop"), ("previous", "Previous"), ("next", "Next"), ("firstslot", "Slot 1"), ("screenshot", "Screenshot"), ("none", "Empty")];
    internal static readonly string[] Actions = Definitions.Select(item => item.Action).ToArray();
    /// <summary>Creates the default wheel from the first eight supported action definitions.</summary>
    internal static string[] DefaultActions() => Definitions.Take(8).Select(item => item.Action).ToArray();
    private readonly string[] slots;
    /// <summary>Selected sector index, or -1 while the stick is below the selection threshold.</summary>
    internal int Selected { get; private set; } = -1;
    /// <summary>Configured action in the selected sector, or null while no sector is selected.</summary>
    internal string? SelectedAction => Selected >= 0 ? slots[Selected] : null;

    /// <summary>Creates the wheel from a cloned eight-slot action array.</summary>
    internal ControllerRadialDialog(ICoreClientAPI api, string[] actions, string cancelButton) : base(api)
    {
        slots = (string[])actions.Clone();
        ElementBounds background = ElementStdBounds.DialogBackground().WithFixedPadding(12, 12);
        SingleComposer = api.Gui.CreateCompo("vulkanstory-controller-radial", ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(background)
            .BeginChildElements(background)
            .AddStaticText("Choose an action. " + cancelButton + " cancels.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 0, 360, 24))
            .AddDynamicCustomDraw(ElementBounds.Fixed(0, 28, 360, 360), DrawWheel, "wheel")
            .EndChildElements().Compose();
    }

    /// <inheritdoc />
    public override bool DisableMouseGrab => true;
    /// <inheritdoc />
    public override string ToggleKeyCombinationCode => null!;
    /// <inheritdoc />
    public override double DrawOrder => 0.97;
    /// <inheritdoc />
    public override double InputOrder => 0.02;

    /// <summary>Looks up an action label; unknown tokens display as Empty.</summary>
    internal static string Label(string action)
    {
        int index = Array.IndexOf(Actions, action);
        return index >= 0 ? Definitions[index].Label : "Empty";
    }

    /// <summary>Selects the nearest clockwise sector from up for stick magnitude at least 0.35, then redraws on change.</summary>
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
