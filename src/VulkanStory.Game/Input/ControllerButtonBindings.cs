using System;

namespace VulkanStory.Game.Input;

internal readonly record struct ControllerButtonBinding(
    string Code, string Label, Func<ControllerProfile, int> Get, Action<ControllerProfile, int> Set);

internal static class ControllerButtonBindings
{
    public static readonly ControllerButtonBinding[] All =
    [
        new("accept", "Jump", p => p.AcceptButton, (p, v) => p.AcceptButton = v),
        new("back", "Menu back / cancel", p => p.GuiBackButton, (p, v) => p.GuiBackButton = v),
        new("drop", "Drop item", p => p.DropButton, (p, v) => p.DropButton = v),
        new("inventory", "Inventory", p => p.InventoryButton, (p, v) => p.InventoryButton = v),
        new("menu", "Game menu", p => p.MenuButton, (p, v) => p.MenuButton = v),
        new("settings", "Controller settings", p => p.SettingsButton, (p, v) => p.SettingsButton = v),
        new("sneak", "Sneak / Shift", p => p.SneakButton, (p, v) => p.SneakButton = v),
        new("sprint", "Sprint / Ctrl", p => p.SprintButton, (p, v) => p.SprintButton = v),
        new("previous", "Previous hotbar slot", p => p.PreviousHotbarButton, (p, v) => p.PreviousHotbarButton = v),
        new("next", "Next hotbar slot", p => p.NextHotbarButton, (p, v) => p.NextHotbarButton = v),
        new("crafting", "Crafting / inventory", p => p.CraftingButton, (p, v) => p.CraftingButton = v),
        new("radial", "Radial action menu", p => p.RadialButton, (p, v) => p.RadialButton = v)
    ];

    public static readonly ControllerButtonBinding[] Inventory =
    [
        new("select", "Select / accept", p => p.GuiSelectButton, (p, v) => p.GuiSelectButton = v),
        new("cancel", "Back / close", p => p.GuiBackButton, (p, v) => p.GuiBackButton = v),
        new("takehalf", "Take half stack", p => p.TakeHalfButton, (p, v) => p.TakeHalfButton = v),
        new("quickmove", "Quick transfer", p => p.QuickMoveButton, (p, v) => p.QuickMoveButton = v),
    ];

    public static ControllerButtonBinding Find(string code)
    {
        foreach (ControllerButtonBinding binding in All)
            if (binding.Code == code) return binding;
        foreach (ControllerButtonBinding binding in Inventory)
            if (binding.Code == code) return binding;
        throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown controller action");
    }

    public static void AssignUnique(ControllerProfile profile, string code, int button)
    {
        ControllerButtonBinding selected = Find(code);
        int oldButton = selected.Get(profile);
        if (oldButton == button) return;
        bool inventory = Array.Exists(Inventory, binding => binding.Code == code);
        foreach (ControllerButtonBinding other in inventory ? Inventory : All)
        {
            if (other.Code == code || other.Get(profile) != button) continue;
            // B is shared by world sneak and menu cancel in the default layout.
            if ((code == "sneak" && other.Code == "back") || (code == "back" && other.Code == "sneak")) continue;
            other.Set(profile, oldButton);
            break;
        }
        selected.Set(profile, button);
    }

    public static string Name(int button, int faceLabel = 0)
    {
        if (button is >= 0 and <= 3)
        {
            string? label = faceLabel switch
            {
                1 => "A", 2 => "B", 3 => "X", 4 => "Y",
                5 => "Cross", 6 => "Circle", 7 => "Square", 8 => "Triangle",
                _ => null
            };
            if (label != null) return label;
        }
        return button switch
        {
            0 => "South / A", 1 => "East / B", 2 => "West / X", 3 => "North / Y",
            4 => "Back / View", 5 => "Guide", 6 => "Start / Menu",
            7 => "Left stick", 8 => "Right stick", 9 => "Left shoulder", 10 => "Right shoulder",
            11 => "D-pad up", 12 => "D-pad down", 13 => "D-pad left", 14 => "D-pad right",
            _ => "Button " + button
        };
    }
}
