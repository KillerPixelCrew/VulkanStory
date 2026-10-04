using OpenTK.Mathematics;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private ClientMain? controllerRadialWorld;
    private int controllerRadialStep;
    private long controllerRadialNextFrame;

    private void StartControllerRadialDiagnostic(ClientMain world)
    {
        if (!HeadlessHarnessOptions.Enabled || controllers?.OpenRadial(world) != true)
            throw new InvalidOperationException("Controller radial diagnostic could not open its dialog.");
        controllerRadialWorld = world;
        controllerRadialStep = 0;
        controllerRadialNextFrame = headlessWorldFrame + 10;
    }

    private void DriveControllerRadialDiagnosticBeforeInput()
    {
        if (controllerRadialWorld is not { } world || headlessWorldFrame < controllerRadialNextFrame) return;
        var dialog = controllers?.OpenedRadial(world) ?? throw new InvalidOperationException("Radial diagnostic lost its dialog.");
        double angle = controllerRadialStep * Math.Tau / 8;
        dialog.Select(new Vector2((float)Math.Sin(angle), (float)-Math.Cos(angle)));
        if (dialog.Selected != controllerRadialStep) throw new InvalidOperationException("Radial selection did not match its displayed sector.");
        controllerRadialStep++;
        controllerRadialNextFrame = headlessWorldFrame + 10;
        if (controllerRadialStep < 8) return;
        platform.Logger.Notification("[VulkanStory] Controller radial UI PASS: all eight sectors selected/redrawn.");
        controllerRadialWorld = null;
    }
}
