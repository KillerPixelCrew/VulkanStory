using System;
using System.Numerics;

namespace VulkanStory.Platform.Sdl;

/// <summary>Convert SDL window coordinates to the drawable-pixel space used by the game UI.</summary>
public static class SdlWindowCoordinates
{
    public static Vector2 ToPixels(Vector2 logical, (int Width, int Height) windowSize,
        (int Width, int Height) pixelSize) => new(
        Scale(logical.X, windowSize.Width, pixelSize.Width),
        Scale(logical.Y, windowSize.Height, pixelSize.Height));

    public static Vector2 ToLogical(Vector2 pixels, (int Width, int Height) windowSize,
        (int Width, int Height) pixelSize) => new(
        Scale(pixels.X, pixelSize.Width, windowSize.Width),
        Scale(pixels.Y, pixelSize.Height, windowSize.Height));

    public static int TextInputCursorOffset(double caretPixelX, double fieldPixelX,
        int fieldLogicalWidth, int windowLogicalWidth, int windowPixelWidth) =>
        windowLogicalWidth <= 0 || windowPixelWidth <= 0 || fieldLogicalWidth <= 0 ? 0 :
        Math.Clamp((int)Math.Round((caretPixelX - fieldPixelX) * windowLogicalWidth / windowPixelWidth),
            0, fieldLogicalWidth);

    private static float Scale(float value, int from, int to) =>
        from > 0 && to > 0 ? value * to / from : value;
}
