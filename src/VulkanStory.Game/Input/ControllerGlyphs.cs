using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace VulkanStory.Game.Input;

internal static class ControllerGlyphs
{
    public static Dictionary<string, string> Build(ControllerProfile profile, Func<int, string> buttonName)
    {
        string Button(int index) => ButtonGlyph(index, buttonName);
        string Axis(int index, bool negative) => AxisGlyph(index, negative, buttonName);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jump"] = Button(profile.AcceptButton),
            ["sneak"] = Button(profile.SneakButton),
            ["shift"] = Button(profile.SneakButton),
            ["sprint"] = Button(profile.SprintButton),
            ["ctrl"] = Button(profile.SprintButton),
            ["inventorydialog"] = Button(profile.InventoryButton),
            ["escapemenudialog"] = Button(profile.MenuButton),
            ["dropitem"] = Button(profile.DropButton),
            ["primarymouse"] = Axis(profile.PrimaryTriggerAxis, profile.PrimaryTriggerNegative),
            ["secondarymouse"] = Axis(profile.SecondaryTriggerAxis, profile.SecondaryTriggerNegative),
        };
    }

    public static string AxisGlyph(int index, bool negative, Func<int, string> buttonName)
    {
        string south = buttonName(0);
        bool playStation = south is "Cross" or "×";
        bool nintendo = south == "B";
        return index switch
        {
            4 => playStation ? "L2" : nintendo ? "ZL" : "LT",
            5 => playStation ? "R2" : nintendo ? "ZR" : "RT",
            0 => negative ? "LX−" : "LX+",
            1 => negative ? "LY−" : "LY+",
            2 => negative ? "RX−" : "RX+",
            3 => negative ? "RY−" : "RY+",
            _ => "Axis " + index,
        };
    }

    public static string ButtonGlyph(int index, Func<int, string> buttonName)
    {
        string name = buttonName(index);
        bool playStation = buttonName(0) is "Cross" or "×";
        bool nintendo = buttonName(0) == "B";
        if (index == 4) return playStation ? "Share" : nintendo ? "−" : "View";
        if (index == 6) return playStation ? "Options" : nintendo ? "+" : "Menu";
        if (index == 9) return playStation ? "L1" : nintendo ? "L" : "LB";
        if (index == 10) return playStation ? "R1" : nintendo ? "R" : "RB";
        return name switch
        {
            "Cross" => "×", "Circle" => "○", "Square" => "□", "Triangle" => "△",
            "A" or "B" or "X" or "Y" => name,
            _ => index switch
            {
                0 => "A", 1 => "B", 2 => "X", 3 => "Y",
                4 => "View", 5 => "Home", 6 => "Menu",
                7 => "LS", 8 => "RS", 9 => playStation ? "L1" : "LB",
                10 => playStation ? "R1" : "RB",
                11 => "D↑", 12 => "D↓", 13 => "D←", 14 => "D→",
                _ => "B" + index
            }
        };
    }

    public static void Draw(Context ctx, ICoreClientAPI api, ElementBounds bounds, string glyph)
    {
        double x = bounds.drawX + 1, y = bounds.drawY + 1;
        double width = bounds.OuterWidth - 2, height = bounds.OuterHeight - 2;
        if (ControllerPromptFont.TryDraw(ctx, api, glyph, x, y, width, height, [1, 1, 1, .85])) return;
        if (glyph.Length == 1) ctx.Arc(x + width / 2, y + height / 2, height / 2 - 1, 0, Math.PI * 2);
        else GuiElement.RoundRectangle(ctx, x, y, width, height, 5);
        ctx.SetSourceRGBA(1, 1, 1, 0.85);
        ctx.LineWidth = 1.5;
        ctx.StrokePreserve();
        ctx.SetSourceRGBA(0.15, 0.15, 0.15, 0.75);
        ctx.Fill();
        CairoFont font = CairoFont.WhiteSmallText();
        double textWidth = font.GetTextExtents(glyph).Width;
        double textHeight = font.GetFontExtents().Height;
        api.Gui.Text.DrawTextLine(ctx, font, glyph,
            x + (width - textWidth) / 2, y + (height - textHeight) / 2 + 2);
    }
}
