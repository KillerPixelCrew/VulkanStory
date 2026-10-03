using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VulkanStory.Platform.Sdl;

namespace VulkanStory.Game.Input;

/// <summary>
/// Turns one SDL finger into game mouse actions. A tap clicks on release, a drag
/// starts after movement clears the slop, and a stationary hold right-clicks.
/// SDL's synthetic touch-mouse events must be filtered before reaching this path.
/// </summary>
internal sealed class SdlTouchMouse(
    Action<float, float, float, float> move,
    Action<EnumMouseButton, bool, float, float> button)
{
    private const ulong LongPressNanoseconds = 500_000_000;
    private const float DragSlopLogicalPixels = 12f;
    private readonly HashSet<(ulong Touch, ulong Finger)> contacts = new();
    private (ulong Touch, ulong Finger)? primary;
    private float startX, startY, lastX, lastY;
    private ulong downNanoseconds;
    private bool dragging, longPressed, suppressUntilAllReleased;

    public void Handle(SdlInputEvent input, (int Width, int Height) logicalSize,
        (int Width, int Height) pixelSize, ulong nowNanoseconds)
    {
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return;
        var finger = (input.TouchId, input.FingerId);
        float x = Math.Clamp(input.X * pixelSize.Width, 0, pixelSize.Width - 1);
        float y = Math.Clamp(input.Y * pixelSize.Height, 0, pixelSize.Height - 1);
        switch (input.Type)
        {
            case SdlEventPump.FingerDown:
                if (!contacts.Add(finger)) return;
                if (contacts.Count != 1 || suppressUntilAllReleased)
                {
                    CancelPrimary();
                    suppressUntilAllReleased = true;
                    return;
                }
                primary = finger;
                startX = lastX = x;
                startY = lastY = y;
                downNanoseconds = input.TimestampNanoseconds != 0 ? input.TimestampNanoseconds : nowNanoseconds;
                dragging = longPressed = false;
                move(x, y, 0, 0);
                break;
            case SdlEventPump.FingerMotion:
                if (primary != finger || suppressUntilAllReleased) return;
                float dx = x - lastX, dy = y - lastY;
                float scaleX = logicalSize.Width > 0 ? pixelSize.Width / (float)logicalSize.Width : 1;
                float scaleY = logicalSize.Height > 0 ? pixelSize.Height / (float)logicalSize.Height : 1;
                float slop = DragSlopLogicalPixels * Math.Max(scaleX, scaleY);
                if (!dragging && !longPressed &&
                    (x - startX) * (x - startX) + (y - startY) * (y - startY) >= slop * slop)
                {
                    button(EnumMouseButton.Left, true, startX, startY);
                    dragging = true;
                }
                move(x, y, dx, dy);
                lastX = x; lastY = y;
                break;
            case SdlEventPump.FingerUp:
            case SdlEventPump.FingerCanceled:
                if (!contacts.Remove(finger)) return;
                if (primary == finger && !suppressUntilAllReleased)
                {
                    move(x, y, x - lastX, y - lastY);
                    if (dragging) button(EnumMouseButton.Left, false, x, y);
                    else if (!longPressed && input.Type == SdlEventPump.FingerUp)
                    {
                        button(EnumMouseButton.Left, true, x, y);
                        button(EnumMouseButton.Left, false, x, y);
                    }
                    primary = null;
                    dragging = longPressed = false;
                }
                if (contacts.Count == 0) suppressUntilAllReleased = false;
                break;
        }
    }

    public void Tick(ulong nowNanoseconds)
    {
        if (primary == null || dragging || longPressed || suppressUntilAllReleased ||
            nowNanoseconds < downNanoseconds ||
            nowNanoseconds - downNanoseconds < LongPressNanoseconds) return;
        longPressed = true;
        button(EnumMouseButton.Right, true, lastX, lastY);
        button(EnumMouseButton.Right, false, lastX, lastY);
    }

    public void Cancel()
    {
        CancelPrimary();
        contacts.Clear();
        suppressUntilAllReleased = false;
    }

    private void CancelPrimary()
    {
        if (dragging) button(EnumMouseButton.Left, false, lastX, lastY);
        primary = null;
        dragging = longPressed = false;
    }
}
