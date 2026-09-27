using OpenTK.Mathematics;
using Optimum.Render.Vulkan.Platform;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public sealed class SdlWindowCoordinatesTests
{
    [Fact]
    public void PointerAndControllerCoordinatesRoundTripAcrossHighDensityWindow()
    {
        var logicalSize = (Width: 800, Height: 600);
        var pixelSize = (Width: 1600, Height: 900);
        var logicalPosition = new Vector2(125.5f, 75f);
        Vector2 gamePosition = SdlWindowCoordinates.ToPixels(logicalPosition, logicalSize, pixelSize);
        Assert.Equal(new Vector2(251f, 112.5f), gamePosition);
        Assert.Equal(logicalPosition, SdlWindowCoordinates.ToLogical(gamePosition, logicalSize, pixelSize));

        // Relative motion must use the same scale as the absolute pointer.
        Assert.Equal(new Vector2(4f, -3f),
            SdlWindowCoordinates.ToPixels(new Vector2(2f, -2f), logicalSize, pixelSize));
    }

    [Fact]
    public void UnavailableDimensionsDoNotDividePointerCoordinatesByZero()
    {
        var position = new Vector2(23f, 31f);
        Assert.Equal(position, SdlWindowCoordinates.ToPixels(position, (0, 0), (0, 0)));
    }

    [Fact]
    public void NativeImeCursorTracksScrolledCaretAtHighDpiAndStaysInsideField()
    {
        // A 2× drawable has a caret 120 pixels into a field starting at pixel 200.
        Assert.Equal(60, SdlWindowCoordinates.TextInputCursorOffset(320, 200, 140, 800, 1600));
        Assert.Equal(0, SdlWindowCoordinates.TextInputCursorOffset(180, 200, 140, 800, 1600));
        Assert.Equal(140, SdlWindowCoordinates.TextInputCursorOffset(600, 200, 140, 800, 1600));
    }
}
