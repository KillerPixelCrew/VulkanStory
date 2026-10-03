using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>Shared in-game and main-menu key grid and text-field editing behavior.</summary>
internal sealed class ControllerKeyboardContent(
    ICoreClientAPI api, GuiElementEditableTextBase target, Action close, Action? submit = null)
{
    private bool shifted;
    private bool refreshRequested;

    public bool TakeRefreshRequest()
    {
        bool refresh = refreshRequested;
        refreshRequested = false;
        return refresh;
    }

    public void AddKeys(GuiComposer composer)
    {
        string[] rows = { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm" };
        double y = 10;
        foreach (string row in rows)
        {
            double x = (10 - row.Length) * 23;
            foreach (char key in row)
            {
                char value = shifted ? char.ToUpperInvariant(key) : key;
                composer.AddSmallButton(value.ToString(), () => Type(value), ElementBounds.Fixed(x, y, 43, 32));
                x += 46;
            }
            y += 38;
        }
        composer.AddSmallButton(shifted ? "Shift ON" : "Shift", ToggleShift, ElementBounds.Fixed(0, y, 86, 34))
            .AddSmallButton("Space", () => Type(' '), ElementBounds.Fixed(94, y, 132, 34))
            .AddSmallButton("Backspace", Backspace, ElementBounds.Fixed(234, y, 102, 34))
            .AddSmallButton("Enter", Enter, ElementBounds.Fixed(344, y, 76, 34))
            .AddSmallButton("Close", Close, ElementBounds.Fixed(428, y, 70, 34));
        composer.AddStaticText("D-pad / stick to select · A to type · LS + RS to close",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y + 42, 500, 25));
    }

    public void PhysicalKeyPress(KeyEvent key)
    {
        EnsureTargetFocus();
        target.OnKeyPress(api, key);
    }

    public void PhysicalKeyDown(KeyEvent key)
    {
        EnsureTargetFocus();
        target.OnKeyDown(api, key);
    }

    private bool Type(char value)
    {
        PhysicalKeyPress(new KeyEvent { KeyCode = value, KeyChar = value });
        if (shifted && char.IsLetter(value))
        {
            shifted = false;
            refreshRequested = true;
        }
        return true;
    }

    private bool Backspace()
    {
        PhysicalKeyDown(new KeyEvent { KeyCode = (int)GlKeys.BackSpace });
        return true;
    }

    private bool Enter()
    {
        PhysicalKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Enter });
        submit?.Invoke();
        close();
        return true;
    }

    private bool ToggleShift()
    {
        shifted = !shifted;
        refreshRequested = true;
        return true;
    }

    private bool Close() { close(); return true; }

    private void EnsureTargetFocus()
    {
        if (!target.HasFocus) target.OnFocusGained();
    }
}
