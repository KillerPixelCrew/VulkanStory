using System;
using System.Numerics;

namespace VulkanStory.Platform.Sdl;

/// <summary>Convert SDL window coordinates to the drawable-pixel space used by the game UI.</summary>
public static class SdlWindowCoordinates
{
    /// <summary>Scales logical SDL coordinates independently on each axis to drawable pixels.</summary>
    /// <remarks>An axis with a nonpositive source or destination extent retains its input coordinate.</remarks>
    public static Vector2 ToPixels(Vector2 logical, (int Width, int Height) windowSize,
        (int Width, int Height) pixelSize) => new(
        Scale(logical.X, windowSize.Width, pixelSize.Width),
        Scale(logical.Y, windowSize.Height, pixelSize.Height));

    /// <summary>Scales drawable-pixel coordinates independently on each axis to logical SDL coordinates.</summary>
    /// <remarks>An axis with a nonpositive source or destination extent retains its input coordinate.</remarks>
    public static Vector2 ToLogical(Vector2 pixels, (int Width, int Height) windowSize,
        (int Width, int Height) pixelSize) => new(
        Scale(pixels.X, pixelSize.Width, windowSize.Width),
        Scale(pixels.Y, pixelSize.Height, windowSize.Height));

    /// <summary>Converts a pixel-space caret to a bounded logical offset within the SDL IME text-input rectangle.</summary>
    /// <param name="caretPixelX">Absolute caret X in drawable pixels.</param>
    /// <param name="fieldPixelX">Absolute field-left X in drawable pixels.</param>
    /// <param name="fieldLogicalWidth">Text-input rectangle width in logical units.</param>
    /// <param name="windowLogicalWidth">SDL logical window width.</param>
    /// <param name="windowPixelWidth">Drawable pixel width.</param>
    /// <returns>Rounded logical offset clamped to the field width, or zero when required extents are nonpositive.</returns>
    public static int TextInputCursorOffset(double caretPixelX, double fieldPixelX,
        int fieldLogicalWidth, int windowLogicalWidth, int windowPixelWidth) =>
        windowLogicalWidth <= 0 || windowPixelWidth <= 0 || fieldLogicalWidth <= 0 ? 0 :
        Math.Clamp((int)Math.Round((caretPixelX - fieldPixelX) * windowLogicalWidth / windowPixelWidth),
            0, fieldLogicalWidth);

    private static float Scale(float value, int from, int to) =>
        from > 0 && to > 0 ? value * to / from : value;
}
